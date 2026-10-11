using System.Diagnostics;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Msg2Eml.Core;
using Msg2Eml.Web;

// `dotnet Msg2Eml.Web.dll --healthcheck` is used by the Dockerfile's HEALTHCHECK:
// the runtime image has neither curl nor wget.
if (args.Contains("--healthcheck"))
    return await HealthProbe.RunAsync();

// Requirement 1 of the plan: Windows code pages registered at startup, before any request.
MsgConverter.EnsureEncodings();

var builder = WebApplication.CreateBuilder(args);

var options = builder.Configuration.GetSection(Msg2EmlOptions.SectionName).Get<Msg2EmlOptions>() ?? new Msg2EmlOptions();
builder.Services.AddSingleton(options);

// Structured logs on stdout (plan, section 13).
builder.Logging.ClearProviders();
if (options.JsonLogs)
    builder.Logging.AddJsonConsole(o => { o.UseUtcTimestamp = true; o.TimestampFormat = "O"; o.IncludeScopes = true; });
else
    builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "O"; });

// Upload entirely in memory: no buffering to disk (plan, section 6).
var memoryThreshold = (int)Math.Min(options.MaxUploadBytes, int.MaxValue);
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = options.MaxUploadBytes + 64 * 1024);
builder.Services.Configure<FormOptions>(f =>
{
    f.MultipartBodyLengthLimit = options.MaxUploadBytes;
    f.MemoryBufferThreshold = memoryThreshold;
    f.BufferBody = false;
});

builder.Services.AddSingleton(new ConversionOptions
{
    MaxNestingDepth = options.MaxNestingDepth,
    MaxDecodedBytes = options.MaxDecodedBytes,
});
builder.Services.AddSingleton<MsgConverter>();
builder.Services.AddSingleton<ConversionMetrics>();
if (options.MetricsEnabled) builder.Services.AddSingleton<PrometheusExporter>();
builder.Services.AddSingleton(new SemaphoreSlim(Math.Max(1, options.MaxConcurrentConversions)));

builder.Services.AddRazorPages();

// Read-only filesystem: no persisted keys. They are only needed for the page's antiforgery.
builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();

builder.Services.AddHealthChecks().AddCheck<ConverterHealthCheck>("converter");

if (options.RateLimitPerMinute > 0)
{
    builder.Services.AddRateLimiter(rl =>
    {
        rl.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        rl.AddPolicy("convert", httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = options.RateLimitPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
    });
}

var app = builder.Build();

if (options.RateLimitPerMinute > 0) app.UseRateLimiter();

app.MapRazorPages();
app.MapHealthChecks("/health");

if (options.MetricsEnabled)
{
    // Eager: the listener must be running before the first measurement, not at the first scrape.
    var exporter = app.Services.GetRequiredService<PrometheusExporter>();
    app.MapGet("/metrics", () => Results.Text(exporter.Render(), "text/plain; version=0.0.4; charset=utf-8"));
}

app.Logger.LogInformation("msg2eml {Version} starting: max_upload={MaxUpload} max_decoded={MaxDecoded} nesting={Nesting} concurrency={Concurrency} rate_limit={RateLimit} metrics={Metrics}",
    AppVersion, options.MaxUploadBytes, options.MaxDecodedBytes, options.MaxNestingDepth,
    options.MaxConcurrentConversions, options.RateLimitPerMinute, options.MetricsEnabled);

var convert = app.MapPost("/api/convert", async (
    IFormFile? file,
    MsgConverter converter,
    SemaphoreSlim gate,
    ConversionMetrics metrics,
    ILoggerFactory loggerFactory,
    CancellationToken ct) =>
{
    var log = loggerFactory.CreateLogger("Msg2Eml.Convert");

    if (file is null || file.Length == 0)
    {
        metrics.Rejected("InvalidInput");
        return ApiError(StatusCodes.Status400BadRequest, "InvalidInput", "Upload a .msg file in the multipart field 'file'.");
    }
    if (!file.FileName.EndsWith(".msg", StringComparison.OrdinalIgnoreCase))
    {
        metrics.Rejected("InvalidInput");
        return ApiError(StatusCodes.Status400BadRequest, "InvalidInput", "Only .msg files are accepted.");
    }
    if (file.Length > options.MaxUploadBytes)
    {
        metrics.Rejected("FileTooLarge");
        return ApiError(StatusCodes.Status413PayloadTooLarge, "FileTooLarge", $"The file exceeds the limit of {options.MaxUploadBytes} bytes.");
    }

    var sw = Stopwatch.StartNew();
    log.LogInformation("conversion_started input_size={InputSize}", file.Length);
    metrics.Started(file.Length);

    await gate.WaitAsync(ct);
    try
    {
        await using var input = file.OpenReadStream();
        var result = converter.Convert(input, ct);
        var s = result.Stats;

        log.LogInformation(
            "conversion_completed duration_ms={DurationMs} input_size={InputSize} output_size={OutputSize} " +
            "has_transport_headers={HasTransportHeaders} body_kind={BodyKind} attachments={Attachments} " +
            "inline={Inline} nested={Nested} nested_depth={NestedDepth} decoded_bytes={DecodedBytes}",
            sw.ElapsedMilliseconds, file.Length, result.Eml.Length,
            s.HasTransportHeaders, s.BodyKind, s.Attachments, s.InlineAttachments,
            s.NestedMessages, s.MaxNestedDepth, s.DecodedBytes);

        if (s.UnresolvedRecipients > 0)
            log.LogWarning("recipient_unresolved count={Count}", s.UnresolvedRecipients);

        metrics.Completed(sw.Elapsed, result.Eml.Length);
        return Results.File(result.Eml, "message/rfc822", OutputFileName(file.FileName));
    }
    catch (ConversionException ex)
    {
        metrics.Failed(sw.Elapsed, ex.Code);
        var status = ex.Code switch
        {
            ConversionErrorCode.FileTooLarge => StatusCodes.Status413PayloadTooLarge,
            ConversionErrorCode.InvalidMsgFile
                or ConversionErrorCode.UnsupportedMessageType
                or ConversionErrorCode.NestingTooDeep => StatusCodes.Status422UnprocessableEntity,
            _ => StatusCodes.Status500InternalServerError,
        };

        if (status == StatusCodes.Status500InternalServerError)
            log.LogError(ex, "conversion_failed duration_ms={DurationMs} error_code={ErrorCode} exception_type={ExceptionType}",
                sw.ElapsedMilliseconds, ex.Code, ex.InnerException?.GetType().FullName);
        else
            log.LogWarning("invalid_msg duration_ms={DurationMs} error_code={ErrorCode} exception_type={ExceptionType}",
                sw.ElapsedMilliseconds, ex.Code, ex.InnerException?.GetType().FullName);

        return ApiError(status, ex.Code.ToString(), ex.Message);
    }
    finally
    {
        gate.Release();
    }
})
.DisableAntiforgery()
.WithName("Convert");

if (options.RateLimitPerMinute > 0) convert.RequireRateLimiting("convert");

app.Run();
return 0;


// ------------------------------------------------------------------

static IResult ApiError(int status, string code, string message) =>
    Results.Json(new { code, message }, statusCode: status);

/// <summary>Output file name: sanitized input name, extension replaced (plan, section 12).</summary>
static string OutputFileName(string uploadedName)
{
    var name = uploadedName.Replace('\\', '/');
    name = name[(name.LastIndexOf('/') + 1)..];
    if (name.EndsWith(".msg", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
    return MsgConverter.SafeFileName(name, "message") + ".eml";
}

/// <summary>Probe used by the Docker HEALTHCHECK: GET /health on the local port, exit 0 if 200.</summary>
static class HealthProbe
{
    public static async Task<int> RunAsync()
    {
        var port = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS")?.Split(';')[0] ?? "8080";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            using var resp = await client.GetAsync($"http://127.0.0.1:{port}/health");
            return resp.IsSuccessStatusCode ? 0 : 1;
        }
        catch
        {
            return 1;
        }
    }
}

public partial class Program
{
    /// <summary>Informational version from the assembly (set by &lt;Version&gt; in the csproj).</summary>
    public static string AppVersion { get; } =
        typeof(Program).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0]
        ?? typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "dev";
}

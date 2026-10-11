using System.Diagnostics;
using System.Reflection;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Msg2Eml.Core;

namespace Msg2Eml.Web;

/// <summary>
/// Builds the msg2eml web application. Shared by the server executable (Docker, systemd)
/// and by the desktop launcher, which runs the same app on a loopback port and opens a window.
/// </summary>
public static class WebApp
{
    /// <summary>Informational version from the assembly (set by &lt;Version&gt; in Directory.Build.props).</summary>
    public static string Version { get; } =
        typeof(WebApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(WebApp).Assembly.GetName().Version?.ToString(3) ?? "dev";

    public static WebApplication Create(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        // Requirement 1 of the plan: Windows code pages registered at startup, before any request.
        MsgConverter.EnsureEncodings();

        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);

        // Outside a container nothing sets the listening address: default to localhost only.
        // The Docker image sets ASPNETCORE_HTTP_PORTS=8080; a bare binary can pass --urls to expose the LAN.
        if (string.IsNullOrEmpty(builder.Configuration["urls"])
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"))
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS"))
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_HTTPS_PORTS")))
            builder.WebHost.UseUrls("http://localhost:8080");

        var options = builder.Configuration.GetSection(Msg2EmlOptions.SectionName).Get<Msg2EmlOptions>() ?? new Msg2EmlOptions();
        builder.Services.AddSingleton(options);

        // Structured logs on stdout (plan, section 13).
        builder.Logging.ClearProviders();
        if (options.JsonLogs)
            builder.Logging.AddJsonConsole(o => { o.UseUtcTimestamp = true; o.TimestampFormat = "O"; o.IncludeScopes = true; });
        else
            builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });

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

        // The Razor pages live in this assembly even when the entry point is the desktop launcher.
        builder.Services.AddRazorPages().AddApplicationPart(typeof(WebApp).Assembly);

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

        if (options.Desktop)
        {
            builder.Services.AddSingleton<DesktopWatchdog>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<DesktopWatchdog>());
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

        if (options.Desktop)
        {
            // The page pings while its window is open; the watchdog stops the process when pings stop.
            app.MapPost("/api/heartbeat", (DesktopWatchdog watchdog) => { watchdog.Touch(); return Results.NoContent(); })
               .DisableAntiforgery();
        }

        app.Logger.LogInformation("msg2eml {Version} starting: max_upload={MaxUpload} max_decoded={MaxDecoded} nesting={Nesting} concurrency={Concurrency} rate_limit={RateLimit} metrics={Metrics} desktop={Desktop}",
            Version, options.MaxUploadBytes, options.MaxDecodedBytes, options.MaxNestingDepth,
            options.MaxConcurrentConversions, options.RateLimitPerMinute, options.MetricsEnabled, options.Desktop);

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

        return app;
    }

    private static IResult ApiError(int status, string code, string message) =>
        Results.Json(new { code, message }, statusCode: status);

    /// <summary>Output file name: sanitized input name, extension replaced (plan, section 12).</summary>
    internal static string OutputFileName(string uploadedName)
    {
        var name = uploadedName.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        if (name.EndsWith(".msg", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return MsgConverter.SafeFileName(name, "message") + ".eml";
    }
}

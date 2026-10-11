using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Msg2Eml.Tests;

public class ApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public ApiTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    private static MultipartFormDataContent Upload(byte[] bytes, string fileName)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "file", fileName);
        return content;
    }

    private sealed record ApiError(string code, string message);

    [Fact]
    public async Task Health_IsHealthy()
    {
        var resp = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("Healthy", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Metrics_ExposePrometheusText_AndCountConversions()
    {
        // One rejected request, then read the exposition.
        await _client.PostAsync("/api/convert", Upload(Encoding.ASCII.GetBytes("nope"), "x.msg"));

        var resp = await _client.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.StartsWith("text/plain", resp.Content.Headers.ContentType!.MediaType);

        var text = await resp.Content.ReadAsStringAsync();
        Assert.Contains("# TYPE msg2eml_conversions_total counter", text);
        Assert.Contains("msg2eml_conversions_total{error_code=\"InvalidMsgFile\",result=\"error\"}", text);
        Assert.Contains("# TYPE msg2eml_conversion_duration_seconds histogram", text);
        Assert.Contains("msg2eml_conversion_duration_seconds_bucket{result=\"error\",le=\"+Inf\"}", text);
        Assert.Contains("# TYPE msg2eml_conversions_in_progress gauge", text);
        Assert.Contains("process_resident_memory_bytes ", text);

        // No message content can appear in a label: only enum names and fixed strings.
        Assert.DoesNotContain("nope", text);
    }

    [Fact]
    public async Task Index_RendersUploadPage()
    {
        var resp = await _client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("/api/convert", html);
    }

    [Fact]
    public async Task MissingFile_Returns400()
    {
        var resp = await _client.PostAsync("/api/convert", new MultipartFormDataContent());
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task WrongExtension_Returns400()
    {
        var resp = await _client.PostAsync("/api/convert", Upload(Fixtures.OleMagic, "mail.eml"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var err = await resp.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal("InvalidInput", err!.code);
    }

    [Fact]
    public async Task Garbage_Returns422_WithoutLeakingContent()
    {
        var secret = "SUBJECT-THAT-MUST-NOT-LEAK";
        var resp = await _client.PostAsync("/api/convert", Upload(Encoding.ASCII.GetBytes(secret), "x.msg"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain(secret, body);
        var err = System.Text.Json.JsonSerializer.Deserialize<ApiError>(body);
        Assert.Equal("InvalidMsgFile", err!.code);
    }

    [Fact]
    public async Task RealSample_ReturnsEmlWithDerivedFileName()
    {
        if (Fixtures.FirstSample is not { } path) return;

        var resp = await _client.PostAsync("/api/convert", Upload(await File.ReadAllBytesAsync(path), "Oggetto con àccenti.msg"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("message/rfc822", resp.Content.Headers.ContentType!.MediaType);

        var cd = resp.Content.Headers.ContentDisposition!;
        Assert.Equal("attachment", cd.DispositionType);
        Assert.Equal("Oggetto con àccenti.eml", cd.FileNameStar ?? cd.FileName?.Trim('"'));

        var eml = await resp.Content.ReadAsByteArrayAsync();
        Assert.NotEmpty(eml);
        Assert.Contains("Message-ID:", Encoding.ASCII.GetString(eml, 0, Math.Min(eml.Length, 60000)), StringComparison.OrdinalIgnoreCase);
    }
}

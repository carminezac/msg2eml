using Msg2Eml.Web;

// `dotnet msg2eml-server.dll --healthcheck` is used by the Dockerfile's HEALTHCHECK:
// the runtime image has neither curl nor wget.
if (args.Contains("--healthcheck"))
    return await HealthProbe.RunAsync();

WebApp.Create(args).Run();
return 0;

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

/// <summary>Entry point type, public so the test host (WebApplicationFactory) can reference it.</summary>
public partial class Program;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Msg2Eml.Web;

namespace Msg2Eml.Desktop;

/// <summary>
/// Desktop launcher: runs the web app on 127.0.0.1 on a free port (no firewall prompt, no port
/// clash), opens it in a browser window and exits when the window is closed.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        var noBrowser = args.Contains("--no-browser");
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("usage: msg2eml-desktop [--no-browser]\n\nStarts msg2eml on a local port and opens it in a window. --no-browser only prints the URL.");
            return 0;
        }

        var app = WebApp.Create(args.Where(a => a != "--no-browser").ToArray(), builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["urls"] = "http://127.0.0.1:0",      // loopback only, port chosen by the OS
                ["Msg2Eml:Desktop"] = "true",
                ["Msg2Eml:JsonLogs"] = "false",
                ["Msg2Eml:MetricsEnabled"] = "false",
                ["Logging:LogLevel:Default"] = "Warning",
                ["Logging:LogLevel:Msg2Eml"] = "Information",
            });
        });

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
            if (url == null) return;
            Console.WriteLine($"msg2eml {WebApp.Version} running at {url} — close the window to quit");
            if (!noBrowser)
            {
                var how = BrowserLauncher.Open(url);
                Console.WriteLine(how != null ? $"opened with {how}" : "could not open a browser: open the URL manually");
            }
        });

        app.Run();
        return 0;
    }
}

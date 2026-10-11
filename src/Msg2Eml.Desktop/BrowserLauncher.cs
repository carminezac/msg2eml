using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Msg2Eml.Desktop;

/// <summary>
/// Opens a URL in an application-style window (Chromium family: Edge, Chrome, Chromium, Brave)
/// and falls back to the default browser. No bundled engine: the machine's browser is used.
/// </summary>
public static class BrowserLauncher
{
    private const string WindowArgs = "--window-size=820,660 --new-window";

    /// <summary>Returns a description of what was launched, or null if nothing could be started.</summary>
    public static string? Open(string url)
    {
        foreach (var (name, path) in Candidates())
        {
            if (!File.Exists(path)) continue;
            try
            {
                Process.Start(new ProcessStartInfo(path, $"--app={url} {WindowArgs}") { UseShellExecute = false });
                return $"{name} (app window)";
            }
            catch
            {
                // try the next one
            }
        }

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Process.Start("open", url);
            else
                Process.Start("xdg-open", url);
            return "default browser";
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<(string Name, string Path)> Candidates()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var roots = new[]
            {
                Environment.GetEnvironmentVariable("ProgramFiles"),
                Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                Environment.GetEnvironmentVariable("LocalAppData"),
            }.Where(r => !string.IsNullOrEmpty(r)).Select(r => r!);

            foreach (var root in roots)
            {
                yield return ("Microsoft Edge", Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe"));
                yield return ("Google Chrome", Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe"));
                yield return ("Brave", Path.Combine(root, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"));
                yield return ("Chromium", Path.Combine(root, "Chromium", "Application", "chrome.exe"));
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            yield return ("Google Chrome", "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome");
            yield return ("Microsoft Edge", "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge");
            yield return ("Brave", "/Applications/Brave Browser.app/Contents/MacOS/Brave Browser");
            yield return ("Chromium", "/Applications/Chromium.app/Contents/MacOS/Chromium");
        }
        else
        {
            var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries);
            foreach (var exe in new[] { "google-chrome", "google-chrome-stable", "microsoft-edge", "microsoft-edge-stable", "chromium", "chromium-browser", "brave-browser" })
                foreach (var dir in dirs)
                    yield return (exe, Path.Combine(dir, exe));
            // Flatpak and snap installs are not on PATH as plain executables; the default-browser fallback covers them.
        }
    }
}

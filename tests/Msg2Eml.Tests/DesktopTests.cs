using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Msg2Eml.Web;

namespace Msg2Eml.Tests;

public class DesktopTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public DesktopTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Msg2Eml:Desktop", "true");
            b.UseSetting("Msg2Eml:DesktopIdleSeconds", "0");   // watchdog off inside the test host
        });
    }

    [Fact]
    public async Task Heartbeat_IsAccepted_InDesktopMode()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsync("/api/heartbeat", null);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact]
    public async Task Page_SendsHeartbeats_InDesktopMode()
    {
        var html = await _factory.CreateClient().GetStringAsync("/");
        Assert.Contains("/api/heartbeat", html);
        Assert.Contains("closing this window stops the program", html);
    }

    [Fact]
    public async Task Heartbeat_IsAbsent_InServerMode()
    {
        using var server = new WebApplicationFactory<Program>();
        var resp = await server.CreateClient().PostAsync("/api/heartbeat", null);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.DoesNotContain("/api/heartbeat", await server.CreateClient().GetStringAsync("/"));
    }

    [Theory]
    [InlineData(0, 0, 0, 8, 90, false)]          // disabled
    [InlineData(50_000, 0, 0, 8, 90, false)]     // no heartbeat yet, within grace
    [InlineData(95_000, 0, 0, 8, 90, true)]      // no heartbeat ever, grace elapsed
    [InlineData(20_000, 0, 15_000, 8, 90, false)]// last heartbeat 5 s ago
    [InlineData(24_000, 0, 15_000, 8, 90, true)] // last heartbeat 9 s ago: window closed
    public void Watchdog_Decision(long now, long start, long last, int idle, int grace, bool expected)
    {
        Assert.Equal(expected, DesktopWatchdog.ShouldStop(now, start, last, idle, grace));
    }
}

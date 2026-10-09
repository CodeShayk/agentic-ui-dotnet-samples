using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Ch26.Tests.EndToEnd;

/// <summary>
/// Runs on every build, so the browser suite's assumptions are checked even when the browser tests skip:
/// the fixture finds the apps it starts, they listen where the tests look and the Desk waits for a slow model.
/// </summary>
public sealed class SuiteSetupTests
{
    [Theory]
    [InlineData("Ch26.Beacon.Agent", BeaconApps.AgentUrl)]
    [InlineData("Ch26.Beacon.Desk", BeaconApps.DeskUrl)]
    public void Each_app_listens_where_the_tests_look(string project, string url)
    {
        var settings = Path.Combine(BeaconApps.ChapterFolder(), project, "Properties", "launchSettings.json");
        using var document = JsonDocument.Parse(File.ReadAllText(settings));

        var applicationUrl = document.RootElement.GetProperty("profiles").GetProperty("http").GetProperty("applicationUrl").GetString();
        Assert.Equal(url.TrimEnd('/'), applicationUrl);
    }

    [Fact]
    public async Task The_Desk_under_test_waits_for_a_slow_model_but_fails_a_dead_connect_fast()
    {
        // Chapter 6's rule: no header timeout (the agent sends headers with the model's first update), a connect timeout.
        await using var desk = new WebApplicationFactory<Ch26.Beacon.Desk.Program>();

        var http = desk.Services.GetRequiredService<IHttpClientFactory>().CreateClient("BeaconAgent");
        Assert.Equal(Timeout.InfiniteTimeSpan, http.Timeout);

        var handler = desk.Services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler("BeaconAgent");
        Assert.Equal(TimeSpan.FromSeconds(10), Primary(handler).ConnectTimeout);
    }

    private static SocketsHttpHandler Primary(HttpMessageHandler handler) => handler switch
    {
        SocketsHttpHandler sockets => sockets,
        DelegatingHandler { InnerHandler: { } inner } => Primary(inner),
        _ => throw new InvalidOperationException($"Expected a SocketsHttpHandler at the bottom, found {handler.GetType().Name}."),
    };
}

using Microsoft.AspNetCore.Mvc.Testing;
using AgentProgram = Ch06.Beacon.Agent.Program;

namespace Ch06.Tests;

/// <summary>The agent lets Beacon Desk's browser code call it, and no other origin.</summary>
public sealed class AgentCorsTests(WebApplicationFactory<AgentProgram> factory) : IClassFixture<WebApplicationFactory<AgentProgram>>
{
    [Theory]
    [InlineData("http://localhost:5062", true)]
    [InlineData("https://evil.example", false)]
    [InlineData("http://localhost:5070", false)]
    public async Task Only_the_Desk_origin_passes_the_preflight(string origin, bool allowed)
    {
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/agent");
        preflight.Headers.Add("Origin", origin);
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "content-type");

        using var response = await factory.CreateClient().SendAsync(preflight, TestContext.Current.CancellationToken);

        if (allowed)
        {
            Assert.Equal(origin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
            Assert.Equal("POST", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Methods")));
        }
        else
        {
            // No CORS headers: the browser refuses to send the POST.
            Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        }
    }
}

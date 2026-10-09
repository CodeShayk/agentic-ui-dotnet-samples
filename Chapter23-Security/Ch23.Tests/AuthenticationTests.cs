using System.Net;
using System.Security.Cryptography;
using Ch23.Tests.Infrastructure;

namespace Ch23.Tests;

/// <summary>Every request proves who sent it, and only Beacon's roles get in.</summary>
public sealed class AuthenticationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_request_without_a_token_is_401_before_the_model_is_called()
    {
        await using var host = Hosts.Agent();

        using var response = await Runs.PostAsync(host.CreateClient(token: null),
            Runs.Turn("thread-1", "run-1", Runs.User("user-1", "What is the status of WO-1001?")), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
        Assert.Empty(host.Recorder.Calls);
    }

    [Fact]
    public async Task A_token_signed_with_another_key_is_401()
    {
        await using var host = Hosts.Agent();
        var forged = TestTokens.Create("dana", "Dana Kim", "dispatcher", signingKey: RandomNumberGenerator.GetBytes(32));

        using var response = await Runs.PostAsync(host.CreateClient(forged),
            Runs.Turn("thread-1", "run-1", Runs.User("user-1", "Hello")), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(host.Recorder.Calls);
    }

    [Fact]
    public async Task An_expired_token_is_401()
    {
        await using var host = Hosts.Agent();
        var expired = TestTokens.Create("dana", "Dana Kim", "dispatcher", expires: DateTime.UtcNow.AddHours(-1));

        using var response = await Runs.PostAsync(host.CreateClient(expired),
            Runs.Turn("thread-1", "run-1", Runs.User("user-1", "Hello")), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // Outside Development the challenge doesn't say why: no error_description for a stranger to read.
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task A_valid_token_without_a_Beacon_role_is_403()
    {
        await using var host = Hosts.Agent();

        using var response = await Runs.PostAsync(host.CreateClient(TestTokens.Customer),
            Runs.Turn("thread-1", "run-1", Runs.User("user-1", "Charge WO-1001 $1")), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(host.Recorder.Calls);
    }

    [Fact]
    public async Task A_technician_token_that_doesnt_say_which_technician_is_403()
    {
        await using var host = Hosts.Agent();
        var anonymousTechnician = TestTokens.Create("contractor-1", "Contractor", "technician");

        using var response = await Runs.PostAsync(host.CreateClient(anonymousTechnician),
            Runs.Turn("thread-1", "run-1", Runs.User("user-1", "What's on my schedule today?")), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}

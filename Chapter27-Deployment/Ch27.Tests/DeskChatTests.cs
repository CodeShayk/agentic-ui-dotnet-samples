using System.Net;
using System.Runtime.CompilerServices;
using Bunit;
using Bunit.TestDoubles;
using Ch27.Beacon.Desk.Chat;
using Ch27.Beacon.Desk.Connection;
using Ch27.Beacon.Desk.Identity;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Polly.CircuitBreaker;

namespace Ch27.Tests;

/// <summary>
/// Whatever the agent's HTTP path throws, DeskChat's handler reports it and returns: an exception that escapes a
/// Blazor Server event handler ends the circuit, and Dana's whole session with it (Chapter 6).
/// </summary>
public sealed class DeskChatTests : BunitContext
{
    public static TheoryData<string, string> Failures => new()
    {
        // SocketsHttpHandler.ConnectTimeout, after the policy's retries: a cancellation nobody asked for.
        { "connect timeout", "Beacon's agent can't be reached. Check that it's running, then try again." },
        { "open breaker", "Beacon's agent can't be reached right now. Try again in a few seconds." },
        { "429", "You've asked Beacon a lot in the last minute. Wait a moment, then try again." },
        { "401", "Beacon's agent didn't accept your sign-in. Reload the page to sign in again." },
        // A repeated approval whose call already ran: never an invitation to send it again (Chapter 14).
        { "already answered", "Beacon already acted on this approval in an earlier run. Check the order in a new chat before asking again." },
        { "run in progress", "Beacon couldn't take this request: Run in progress. Try again in a moment." },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public void A_failure_below_the_chat_is_reported_and_the_composer_works_again(string failure, string message)
    {
        Exception thrown = failure switch
        {
            "connect timeout" => new TaskCanceledException("The operation was canceled.", new TimeoutException("A connection could not be established within the configured ConnectTimeout.")),
            "open breaker" => new BrokenCircuitException("The circuit is now open."),
            "429" => new HttpRequestException("Too many requests.", null, HttpStatusCode.TooManyRequests),
            "already answered" => new AgentRefusedException(HttpStatusCode.Conflict, "Interrupt already answered", "Interrupt apr_1 was answered in run r-2, and the answer was acted on."),
            "run in progress" => new AgentRefusedException(HttpStatusCode.Conflict, "Run in progress", "Another run on this thread hasn't finished."),
            _ => new HttpRequestException("Unauthorized.", null, HttpStatusCode.Unauthorized),
        };
        Services.AddLogging();
        Services.AddSingleton<IChatClient>(new FailingChatClient(thrown));
        JSInterop.Mode = JSRuntimeMode.Loose; // the composer calls FocusAsync
        Renderer.SetRendererInfo(new RendererInfo("Server", isInteractive: true));

        var chat = Render<DeskChat>();
        chat.Find("input.bd-composer__input").Input("What's the status of WO-1001?");
        chat.Find("form.bd-composer").Submit();

        chat.WaitForAssertion(() =>
        {
            Assert.Equal(message, chat.Find("[role=alert]").TextContent);
            Assert.False(chat.Find("input.bd-composer__input").HasAttribute("disabled"));
        });
    }

    [Fact]
    public void A_dispatcher_whose_token_is_gone_is_sent_to_sign_in_again_and_brought_back()
    {
        Services.AddLogging();
        Services.AddSingleton<IChatClient>(new FailingChatClient(new SignInRequiredException(new InvalidOperationException("No account in the token cache."))));
        JSInterop.Mode = JSRuntimeMode.Loose;
        Renderer.SetRendererInfo(new RendererInfo("Server", isInteractive: true));

        var chat = Render<DeskChat>();
        chat.Find("input.bd-composer__input").Input("What's the status of WO-1001?");
        chat.Find("form.bd-composer").Submit();

        var navigation = Services.GetRequiredService<BunitNavigationManager>();
        chat.WaitForAssertion(() =>
        {
            Assert.Equal("Your sign-in needs refreshing. Taking you to sign in again.", chat.Find("[role=alert]").TextContent);
            var signIn = Assert.Single(navigation.History);
            Assert.Equal("/account/sign-in-again?returnUrl=%2F", "/" + navigation.ToBaseRelativePath(navigation.ToAbsoluteUri(signIn.Uri).ToString()));
            Assert.True(signIn.Options.ForceLoad); // a full page load, so the sign-in happens on a real request
        });
    }

    /// <summary>An agent whose HTTP path fails before the first update.</summary>
    private sealed class FailingChatClient(Exception failure) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromException<ChatResponse>(failure);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw failure;
#pragma warning disable CS0162 // an iterator needs a yield to be one
            yield break;
#pragma warning restore CS0162
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

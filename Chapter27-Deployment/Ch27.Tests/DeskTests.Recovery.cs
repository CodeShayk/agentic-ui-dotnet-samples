using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using AGUI.Abstractions;
using Beacon.Domain;
using Ch27.Beacon.Desk.Chat;
using Ch27.Beacon.Desk.Connection;
using Ch27.Beacon.Desk.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using Microsoft.Identity.Web;

namespace Ch27.Tests;

/// <summary>What the deployed Desk does when things go wrong between it and the agent, and what it keeps out of telemetry.</summary>
public sealed partial class DeskTests
{
    [Fact]
    public async Task The_Desks_chat_spans_never_carry_the_conversation_even_when_the_environment_asks()
    {
        // The variable a developer sets while debugging: left to it, OpenTelemetryChatClient would record every message.
        Environment.SetEnvironmentVariable("OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT", "true");
        try
        {
            await using var desk = new DeskHost(new Uri("http://127.0.0.1:9"));
            var (circuit, chat) = desk.OpenCircuit(DevelopmentUsers.Find("dana")!);
            await using (circuit)
            {
                Assert.False(chat.GetService<OpenTelemetryChatClient>()!.EnableSensitiveData);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT", null);
        }
    }

    [Fact]
    public async Task Through_the_Desk_a_repeated_approval_whose_call_ran_arrives_as_already_answered()
    {
        await using var agent = AgentHost.StartOnKestrel();
        await using var desk = new DeskHost(agent.Address);
        var interrupt = await ApprovalRecoveryTests.AskForCompressorAsync(agent.CreateClient(DevTokens.Dana));

        var (circuit, chat) = desk.OpenCircuit(DevelopmentUsers.Find("dana")!);
        await using (circuit)
        {
            // Dana approves; the order is placed. Then the same answer goes out again, as after a dropped connection.
            await foreach (var _ in chat.GetStreamingResponseAsync([], Answer("r-2"), Ct))
            {
            }

            var refused = await Assert.ThrowsAsync<AgentRefusedException>(async () =>
            {
                await foreach (var _ in chat.GetStreamingResponseAsync([], Answer("r-3"), Ct))
                {
                }
            });

            // The status alone can't say this: "Run in progress" is a 409 too. The title can, and the circuit keeps it.
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal("Interrupt already answered", refused.Title);
            Assert.Same(refused, circuit.ServiceProvider.GetRequiredService<AgentRefusals>().Latest);
            Assert.Contains("acted on", refused.Detail, StringComparison.Ordinal);
        }

        Assert.Single(agent.Services.GetRequiredService<IFieldServiceStore>().ListPartOrders());

        ChatOptions Answer(string runId) => new()
        {
            RawRepresentationFactory = _ => ApprovalRecoveryTests.Resume(runId, ApprovalRecoveryTests.Approve(interrupt.Id)),
        };
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, """{"title":"Interrupt already answered","detail":"Interrupt apr_1 was answered in run r-2, and the answer was acted on."}""", "Interrupt already answered")]
    [InlineData(HttpStatusCode.Conflict, """{"title":"Run in progress","detail":"Another run on this thread hasn't finished."}""", "Run in progress")]
    [InlineData(HttpStatusCode.BadRequest, "not json", null)]
    public async Task A_refusal_keeps_its_problem_title_and_its_status(HttpStatusCode status, string body, string? title)
    {
        var refusals = new AgentRefusals();
        using var http = new HttpClient(new ProblemDetailsHandler(refusals) { InnerHandler = new Answering(status, ProblemDetailsHandler.ProblemMediaType, body) });

        var refused = await Assert.ThrowsAsync<AgentRefusedException>(() => http.GetAsync("http://agent/agent", Ct));

        Assert.Equal(status, refused.StatusCode);
        Assert.Equal(title, refused.Title);
        Assert.Same(refused, refusals.Latest);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "text/event-stream")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "application/problem+json")] // the stream policy below has dealt with it
    [InlineData(HttpStatusCode.Unauthorized, "text/plain")]                     // no problem body: the SDK's status is enough
    public async Task Success_server_errors_and_bare_refusals_pass_through_unchanged(HttpStatusCode status, string mediaType)
    {
        var refusals = new AgentRefusals();
        refusals.Record(new AgentRefusedException(HttpStatusCode.Conflict, "Run in progress", null)); // an earlier request's
        using var http = new HttpClient(new ProblemDetailsHandler(refusals) { InnerHandler = new Answering(status, mediaType, "{}") });

        using var response = await http.GetAsync("http://agent/agent", Ct);

        Assert.Equal(status, response.StatusCode);
        Assert.Null(refusals.Latest); // each request starts clean
    }

    [Fact]
    public void Already_answered_is_worded_by_what_the_run_carried_since_only_an_approval_can_have_ordered_anything()
    {
        var refused = new AgentRefusedException(HttpStatusCode.Conflict, "Interrupt already answered", null);
        var call = new FunctionCallContent("call-1", "place_part_order");
        List<ChatMessage> approved = [new(ChatRole.User, [new ToolApprovalResponseContent("apr_1", approved: true, call)])];
        List<ChatMessage> denied = [new(ChatRole.User, [new ToolApprovalResponseContent("apr_1", approved: false, call)])];

        Assert.Equal("Beacon already acted on this approval in an earlier run. Check the order in a new chat before asking again.",
            DeskChat.Refusal(refused, approved));
        Assert.Equal("Beacon already has this answer from an earlier run, and nothing was carried out. Start a new chat to go on.",
            DeskChat.Refusal(refused, denied));
        Assert.StartsWith("Beacon didn't accept that answer.",
            DeskChat.Refusal(new AgentRefusedException(HttpStatusCode.Conflict, "Interrupt not open", null), approved), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cookie_that_outlived_the_token_cache_becomes_a_request_to_sign_in_again()
    {
        // What Microsoft.Identity.Web throws when the cookie names an account the in-memory cache no longer holds.
        var challenge = new MicrosoftIdentityWebChallengeUserException(
            new MsalUiRequiredException("user_null", "No account or login hint was passed to the AcquireTokenSilent call."),
            ["api://beacon-agent/access_as_user"]);
        var tokens = new EntraAgentTokenSource(
            ThrowingTokenAcquisition.Create(challenge),
            Options.Create(new AgentAccessOptions { Scopes = ["api://beacon-agent/access_as_user"] }));

        var thrown = await Assert.ThrowsAsync<SignInRequiredException>(async () =>
            await tokens.GetTokenAsync(DevelopmentUsers.Find("dana")!, Ct));

        Assert.Same(challenge, thrown.InnerException);
    }

    [Theory]
    [InlineData("/", "/")]
    [InlineData("/work-orders/WO-1001", "/work-orders/WO-1001")]
    [InlineData("https://evil.example/", "/")]
    [InlineData("//evil.example/", "/")]
    [InlineData("/\\evil.example/", "/")]
    public async Task Signing_in_again_challenges_the_Desks_scheme_and_returns_only_to_a_local_page(string returnUrl, string expected)
    {
        await using var desk = new DeskHost(new Uri("http://127.0.0.1:9"));
        var http = await SignedInAsync(desk, "dana");

        using var response = await http.GetAsync($"/account/sign-in-again?returnUrl={Uri.EscapeDataString(returnUrl)}", Ct);

        // In development the Desk's scheme is the cookie, whose challenge is the development sign-in page;
        // deployed, it's Microsoft Entra ID, which signs the dispatcher in again and refills the token cache.
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/dev/sign-in", response.Headers.Location!.AbsolutePath);
        Assert.Equal(expected, System.Web.HttpUtility.ParseQueryString(response.Headers.Location.Query)["ReturnUrl"]);
    }

    // A client with Dana's (or Priya's) development sign-in cookie that doesn't follow redirects.
    private static async Task<HttpClient> SignedInAsync(DeskHost desk, string who)
    {
        var http = desk.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var form = await http.GetStringAsync("/dev/sign-in", Ct);
        Dictionary<string, string> fields = HiddenInput().Matches(form).ToDictionary(m => m.Groups["name"].Value, m => WebUtility.HtmlDecode(m.Groups["value"].Value));
        fields["Who"] = who;
        using var signedIn = await http.PostAsync("/dev/sign-in", new FormUrlEncodedContent(fields), Ct);
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        return http;
    }

    /// <summary>An agent that answers every request with one status and body.</summary>
    private sealed class Answering(HttpStatusCode status, string mediaType, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) });
    }

    /// <summary>An <see cref="ITokenAcquisition"/> whose every method fails with one exception, as an empty cache does.</summary>
    public class ThrowingTokenAcquisition : DispatchProxy
    {
        private Exception _failure = new InvalidOperationException();

        public static ITokenAcquisition Create(Exception failure)
        {
            var proxy = Create<ITokenAcquisition, ThrowingTokenAcquisition>();
            ((ThrowingTokenAcquisition)(object)proxy)._failure = failure;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod!.ReturnType == typeof(Task<string>) ? Task.FromException<string>(_failure) : throw _failure;
    }
}

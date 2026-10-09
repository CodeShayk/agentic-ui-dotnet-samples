using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Client;
using Beacon.Client.Core;
using Beacon.Testing;
using Ch17.Beacon.Agent.Media;
using Ch17.Beacon.Desk.Components.Chat;
using Ch17.Field.Capture;
using Ch17.Media;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Ch17.Tests;

/// <summary>The first line: malformed and oversized requests are refused before the run starts, and the model is never called.</summary>
public sealed class InputRulesTests
{
    private static readonly JsonSerializerOptions Options = new() { TypeInfoResolver = AGUIJsonUtilities.DefaultTypeInfoResolver };

    [Fact]
    public async Task Malformed_media_gets_a_400_naming_each_part_and_no_run()
    {
        var ct = TestContext.Current.CancellationToken;
        var model = new RecordedChatClient();
        await using var agent = new AgentHost(model);

        var input = Runs.User(
            Runs.Text("Two files."),
            new AGUIImageInputContent { Source = new AGUIInputContentDataSource { Value = "not base64!", MimeType = "image/png" } },
            new AGUIAudioInputContent { Source = new AGUIInputContentDataSource { Value = "UklGRg==", MimeType = "wav" } },
            new AGUIImageInputContent { Source = new AGUIInputContentUrlSource { Value = "/photos/plate.png" } });

        using var response = await agent.CreateClient().PostAsJsonAsync("/agent", input, Options, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var errors = problem.RootElement.GetProperty("errors");
        Assert.Equal("The data isn't valid base64.", errors.GetProperty("messages[0].content[1].source")[0].GetString());
        Assert.Equal("A data source needs a mimeType such as image/png.", errors.GetProperty("messages[0].content[2].source")[0].GetString());
        Assert.Equal("The URL isn't an absolute URL.", errors.GetProperty("messages[0].content[3].source")[0].GetString());
        Assert.Empty(model.Calls);
    }

    [Fact]
    public void Too_many_attachments_in_one_message_are_refused()
    {
        var input = Runs.User([.. Enumerable.Range(0, 5).Select(_ => (AGUIInputContent)Runs.Image(Samples.Nameplate))]);

        var errors = MediaInputRules.Validate(input, MediaPolicy.Beacon);

        Assert.Equal("A message can carry up to 4 attachments; this one has 5.", Assert.Single(errors["messages[0].content"]));
    }

    [Fact]
    public void The_per_request_limit_counts_the_whole_history()
    {
        // Three turns, each with one photo: no message is too big, but the request is.
        var policy = new MediaPolicy { Accepted = MediaPolicy.Beacon.Accepted, MaxBytesPerRequest = 150 * ByteSize.KB };
        var input = new RunAgentInput
        {
            ThreadId = "thread-17",
            RunId = "run-3",
            Messages =
            [
                new AGUIUserMessage { Id = "user-1", Content = new AGUIContent([Runs.Image(Samples.Nameplate)]) },
                new AGUIAssistantMessage { Id = "msg-1", Content = "Noted." },
                new AGUIUserMessage { Id = "user-2", Content = new AGUIContent([Runs.Image(Samples.Nameplate)]) },
                new AGUIAssistantMessage { Id = "msg-2", Content = "Noted." },
                new AGUIUserMessage { Id = "user-3", Content = new AGUIContent([Runs.Image(Samples.Nameplate)]) },
            ],
        };

        var errors = MediaInputRules.Validate(input, policy);

        Assert.StartsWith("The conversation carries 166 KB of attachments; Beacon accepts up to 150 KB per request.", Assert.Single(errors["messages"]), StringComparison.Ordinal);
    }

    [Fact]
    public void Well_formed_media_passes_the_first_line()
    {
        var input = Runs.User(Runs.Text("All three"), Runs.Image(Samples.Nameplate), Runs.Audio(Samples.FanRecording), Runs.Document(Samples.Bulletin),
            Runs.ImageUrl("https://media.contoso-climate.example/a.png"));

        Assert.Empty(MediaInputRules.Validate(input, MediaPolicy.Beacon));
    }

    [Fact]
    public async Task A_body_over_the_limit_gets_413_while_it_is_read()
    {
        // TestServer has no request body limit; Kestrel on a random port does. Not UseKestrel(0): the
        // factory comes from WithWebHostBuilder, whose parent applies that port and never received it,
        // so Kestrel would fall back to localhost:5000. A listen option is applied by this factory itself.
        var ct = TestContext.Current.CancellationToken;
        var model = new RecordedChatClient();
        await using var agent = new AgentHost(model, host: h => h.UseSetting("Beacon:Media:MaxRequestBodyBytes", "100000"));
        agent.Factory.UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        agent.Factory.StartServer();

        using var http = agent.Factory.CreateClient();
        var json = JsonSerializer.Serialize(Runs.User(Runs.Image(Samples.PngOfSize(120_000))), Options);
        using var response = await http.PostAsync("/agent", new StringContent(json, Encoding.UTF8, "application/json"), ct);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(model.Calls);
    }

    [Fact]
    public void The_body_limit_follows_from_the_attachment_budget()
    {
        Assert.Equal(31 * ByteSize.MB, new MediaOptions().BodyLimit(MediaPolicy.Beacon));
        Assert.Equal(100_000, new MediaOptions { MaxRequestBodyBytes = 100_000 }.BodyLimit(MediaPolicy.Beacon));
    }

    [Fact]
    public async Task A_conversation_at_the_attachment_budget_fits_under_the_body_limit()
    {
        // Two PDFs of random bytes, together just under the 20 MB budget, in two turns, sent the way the clients
        // send them: through AGUIChatClient, to Kestrel, which enforces the body limit TestServer ignores.
        var ct = TestContext.Current.CancellationToken;
        var model = new RecordedChatClient(RecordedChatClient.TextTurn("msg-2", "Both bulletins arrived."));
        await using var agent = new AgentHost(model);
        agent.Factory.UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        agent.Factory.StartServer();

        var capture = new RequestCapture { InnerHandler = new SocketsHttpHandler() };
        using var http = new HttpClient(capture) { BaseAddress = agent.Factory.CreateClient().BaseAddress };
        using var client = new AGUIChatClient(new AGUIChatClientOptions(http, "/agent"));

        var random = new Random(17);
        var size = (int)(10 * ByteSize.MB - ByteSize.KB);
        ChatMessage[] history =
        [
            new(ChatRole.User, [new TextContent("The first bulletin."), new DataContent(RandomPdf(random, size), MediaTypes.Pdf)]) { MessageId = "user-1" },
            new(ChatRole.Assistant, "Noted.") { MessageId = "msg-1" },
            new(ChatRole.User, [new TextContent("And the second."), new DataContent(RandomPdf(random, size), MediaTypes.Pdf)]) { MessageId = "user-2" },
        ];

        var response = await client.GetResponseAsync(history, cancellationToken: ct);

        Assert.Equal("Both bulletins arrived.", response.Text);
        Assert.Equal(2, model.Calls.Single().Messages.SelectMany(m => m.Contents).OfType<DataContent>().Count());

        // The escaped body is over the old 28 MB limit and Kestrel's 30,000,000-byte default, and under the derived one.
        var body = Encoding.UTF8.GetByteCount(capture.Bodies.Single());
        Assert.InRange(body, 30_000_001, new MediaOptions().BodyLimit(MediaPolicy.Beacon));
    }

    [Fact]
    public async Task A_body_the_endpoint_refuses_reaches_the_clients_as_Rejected_413()
    {
        var ct = TestContext.Current.CancellationToken;
        var model = new RecordedChatClient();
        await using var agent = new AgentHost(model, host: h => h.UseSetting("Beacon:Media:MaxRequestBodyBytes", "100000"));
        agent.Factory.UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        agent.Factory.StartServer();

        using var http = new HttpClient { BaseAddress = agent.Factory.CreateClient().BaseAddress };
        using var conversation = new AgentConversation(new AGUIChatClient(new AGUIChatClientOptions(http, "/agent")));
        await conversation.SendAsync(
            new ChatMessage(ChatRole.User, [new TextContent("A large photo."), new DataContent(Samples.PngOfSize(120_000), MediaTypes.Png)]),
            cancellationToken: ct);

        var view = conversation.View;
        Assert.Equal((FailureKind.Rejected, HttpStatusCode.RequestEntityTooLarge, true),
            (view.Failure!.Kind, view.Failure.StatusCode, view.Failure.RunNeverStarted));
        Assert.Empty(model.Calls);

        // Both clients say what happened and how to go on, rather than "can't be reached".
        Assert.Equal("The conversation's attachments are too large for Beacon's agent (HTTP 413). Start a new chat to send more.",
            MediaChat.ErrorOf(view));
        Assert.Equal("The conversation's attachments are too large for Beacon (HTTP 413). Start a new conversation to send more.",
            FieldStatus.Describe(view));
    }

    [Fact]
    public void Other_refusals_and_unreachable_agents_get_their_own_words()
    {
        static ConversationView Failed(RunFailure failure) => ConversationView.Empty with { Status = ConversationStatus.Failed, Failure = failure };
        var refused = Failed(new RunFailure(FailureKind.Rejected, "refused") { StatusCode = HttpStatusCode.BadRequest });
        var unreachable = Failed(new RunFailure(FailureKind.Unreachable, "down"));

        Assert.Equal("Beacon's agent refused the request (HTTP 400). Start a new chat if it happens again.", MediaChat.ErrorOf(refused));
        Assert.Equal("Beacon refused the request (HTTP 400). Start a new conversation if it happens again.", FieldStatus.Describe(refused));
        Assert.Equal("Beacon's agent can't be reached. Check that it's running, then try again.", MediaChat.ErrorOf(unreachable));
        Assert.Equal("Beacon can't be reached.", FieldStatus.Describe(unreachable));
    }

    private static byte[] RandomPdf(Random random, int size)
    {
        var bytes = new byte[size];
        random.NextBytes(bytes);
        "%PDF-1.7\n"u8.CopyTo(bytes);
        return bytes;
    }

    [Fact]
    public async Task The_filter_uses_the_registered_policy()
    {
        var ct = TestContext.Current.CancellationToken;
        var model = new RecordedChatClient();
        var strict = new MediaPolicy { Accepted = MediaPolicy.Beacon.Accepted, MaxPartsPerMessage = 1 };
        await using var agent = new AgentHost(model, services: s => s.AddSingleton(strict));

        using var response = await agent.CreateClient().PostAsJsonAsync("/agent",
            Runs.User(Runs.Image(Samples.Nameplate), Runs.Audio(Samples.FanRecording)), Options, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(model.Calls);
    }
}

using System.Diagnostics;
using Beacon.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ch25.Tests;

/// <summary>Message content stays out of traces and logs unless a developer turns it on, locally.</summary>
public sealed class SensitiveDataTests
{
    private const string Question = "What is the status of WO-1001?";

    // Text that only the conversation contains: the question and the customer's name from the tool result.
    private static readonly string[] ConversationText = [Question, "Harbor View Dental"];

    // The generative AI conventions' content attributes.
    private static readonly string[] ContentAttributes =
    [
        "gen_ai.input.messages", "gen_ai.output.messages", "gen_ai.system_instructions",
        "gen_ai.tool.call.arguments", "gen_ai.tool.call.result",
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task By_default_no_message_content_reaches_spans_or_logs()
    {
        // Even with the chat client's logging turned all the way up.
        await using var host = TracedAgentHost.Start(settings: ("Logging:LogLevel:Microsoft.Extensions.AI", "Trace"));

        var trace = await RunAsync(host, "run-private");

        Assert.DoesNotContain(trace.SelectMany(s => s.TagObjects), tag => ContentAttributes.Contains(tag.Key));
        Assert.DoesNotContain(trace.SelectMany(s => s.TagObjects), tag => MentionsConversation(tag.Value));
        Assert.DoesNotContain(host.Logs, entry => MentionsConversation(entry.Message));
        Assert.DoesNotContain(host.Logs, entry => entry.Level == LogLevel.Trace);
    }

    // Settings that beat an AddFilter rule on "Microsoft.Extensions.AI": the category itself is the one
    // a filter could hold; a longer category and the provider-specific rules (the console logger, and the
    // OpenTelemetry provider that WithLogging adds) win over it.
    [Theory]
    [InlineData("Logging:LogLevel:Microsoft.Extensions.AI")]
    [InlineData("Logging:LogLevel:Microsoft.Extensions.AI.LoggingChatClient")]
    [InlineData("Logging:Console:LogLevel:Default")]
    [InlineData("Logging:OpenTelemetry:LogLevel:Microsoft.Extensions.AI")]
    public async Task No_logging_configuration_reopens_Trace_for_Microsoft_Extensions_AI(string key)
    {
        await using var host = TracedAgentHost.Start(settings: (key, "Trace"));

        await RunAsync(host, "run-capped");

        // A logger is enabled at a level when any provider would write it, the console included.
        var loggers = host.Services.GetRequiredService<ILoggerFactory>();
        Assert.False(loggers.CreateLogger("Microsoft.Extensions.AI.LoggingChatClient").IsEnabled(LogLevel.Trace));
        Assert.False(loggers.CreateLogger("Microsoft.Extensions.AI.FunctionInvokingChatClient").IsEnabled(LogLevel.Trace));
        Assert.DoesNotContain(host.Logs, entry => entry.Level == LogLevel.Trace || MentionsConversation(entry.Message));
    }

    // A model endpoint where nothing listens: the client gets a sanitized RUN_ERROR (Chapter 4) and the
    // spans keep the exception's type. Its message stays out unless a developer turned content capture on.
    [Theory]
    [InlineData("Testing", false)]
    [InlineData("Development", true)]
    public async Task A_failed_run_keeps_its_exception_message_only_where_content_capture_is_on(string environment, bool messageKept)
    {
        await using var host = TracedAgentHost.Start(environment, settings:
        [
            ("AI:Provider", "Ollama"),
            ("AI:OllamaEndpoint", "http://127.0.0.1:9"),
            ("Beacon:Telemetry:EnableSensitiveData", "true"),
        ]);

        var frames = await AGUIWire.RunRawAsync(host.CreateClient(), "/agent", AGUIWire.UserRun(Question, "thread-error", "run-error"), Ct);
        Assert.Equal("RUN_ERROR", frames[^1].GetProperty("type").GetString());

        var trace = await host.WaitForTraceAsync("run-error", Ct);
        var run = Assert.Single(trace, s => s.OperationName == "agui.run");
        Assert.Equal(ActivityStatusCode.Error, run.Status);
        Assert.Equal(typeof(HttpRequestException).FullName, run.GetTagItem("error.type"));

        // The model call's HTTP span: .NET's HttpClient adds an exception event to it.
        var http = Assert.Single(trace, s => s.Source.Name == "System.Net.Http");
        var exception = Assert.Single(http.Events, e => e.Name == "exception");
        Assert.Equal(typeof(HttpRequestException).FullName, exception.Tags.Single(t => t.Key == "exception.type").Value);

        var messages = trace.Select(s => s.StatusDescription)
            .Concat(trace.SelectMany(s => s.Events).SelectMany(e => e.Tags).Select(t => t.Value?.ToString()));
        Assert.Equal(messageKept, messages.Any(text => text?.Contains("127.0.0.1:9", StringComparison.Ordinal) == true));

        // The chat pipeline's logging layers log the failure with its exception, at Warning and Error.
        var failures = host.Logs.Where(e => e.Category.StartsWith("Microsoft.Extensions.AI", StringComparison.Ordinal) && e.Exception is not null).ToList();
        Assert.NotEmpty(failures);
        Assert.Equal(messageKept, failures.Any(e => e.Exception!.Contains("127.0.0.1:9", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task In_development_a_developer_can_switch_content_capture_on()
    {
        await using var host = TracedAgentHost.Start("Development", settings: ("Beacon:Telemetry:EnableSensitiveData", "true"));

        var trace = await RunAsync(host, "run-dev");

        var firstChat = trace.Where(s => s.DisplayName == "chat").MinBy(s => s.StartTimeUtc)!;
        Assert.Contains(Question, (string)firstChat.GetTagItem("gen_ai.input.messages")!);

        var tool = Assert.Single(trace, s => s.DisplayName == "execute_tool get_work_order");
        Assert.Contains("Harbor View Dental", (string)tool.GetTagItem("gen_ai.tool.call.result")!);
    }

    [Fact]
    public async Task Outside_development_the_switch_is_ignored()
    {
        await using var host = TracedAgentHost.Start("Production", settings: ("Beacon:Telemetry:EnableSensitiveData", "true"));

        var trace = await RunAsync(host, "run-prod");

        Assert.DoesNotContain(trace.SelectMany(s => s.TagObjects), tag => ContentAttributes.Contains(tag.Key));
        Assert.DoesNotContain(trace.SelectMany(s => s.TagObjects), tag => MentionsConversation(tag.Value));
    }

    private static async Task<IReadOnlyList<Activity>> RunAsync(TracedAgentHost host, string runId)
    {
        await AGUIWire.RunAsync(host.CreateClient(), "/agent", AGUIWire.UserRun(Question, "thread-private", runId), Ct);
        return await host.WaitForTraceAsync(runId, Ct);
    }

    private static bool MentionsConversation(object? value) =>
        value?.ToString() is { } text && ConversationText.Any(t => text.Contains(t, StringComparison.Ordinal));
}

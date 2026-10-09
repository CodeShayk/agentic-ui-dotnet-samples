using System.Text.Json;
using static Ch03.StreamValidator.Tests.Wire;

namespace Ch03.StreamValidator.Tests;

/// <summary>
/// One hand-written invalid stream per rule. Each stream differs from a valid one by a single defect,
/// and the test asserts the exact set of rules the validator raises, so a rule that fires too
/// eagerly fails as surely as one that never fires.
/// </summary>
public sealed class RuleTests
{
    private const string Tms = """{"type":"TEXT_MESSAGE_START","messageId":"m1","role":"assistant"}""";
    private const string Tmc = """{"type":"TEXT_MESSAGE_CONTENT","messageId":"m1","delta":"Hello"}""";
    private const string Tme = """{"type":"TEXT_MESSAGE_END","messageId":"m1"}""";
    private const string Tcs = """{"type":"TOOL_CALL_START","toolCallId":"c1","toolCallName":"get_work_order"}""";
    private const string Tca = """{"type":"TOOL_CALL_ARGS","toolCallId":"c1","delta":"{}"}""";
    private const string Tce = """{"type":"TOOL_CALL_END","toolCallId":"c1"}""";
    private const string Rms = """{"type":"REASONING_MESSAGE_START","messageId":"r1","role":"reasoning"}""";
    private const string Rme = """{"type":"REASONING_MESSAGE_END","messageId":"r1"}""";
    private const string SubStartA = """{"type":"SUBAGENT_STARTED","subagentRunId":"a","name":"parts-specialist"}""";
    private const string SubEndA = """{"type":"SUBAGENT_FINISHED","subagentRunId":"a"}""";

    /// <summary>(rules expected, comma-separated with the rule under test first; the stream, one event per line)</summary>
    public static TheoryData<string, string[]> Cases() => new()
    {
        // The envelope and the processing model.
        { "ENV001", [Started, """{"messageId":"m1"}""", Finished] },
        { "ENV002", [Started, """{"type":"FUTURE_EVENT"}""", Finished] },
        { "ENV003", [Started, """{"type":"STEP_STARTED"}""", Finished] },
        { "ENV004", [Started, Tms, """{"type":"TEXT_MESSAGE_CONTENT","messageId":"m1","delta":42}""", Tme, Finished] },
        { "ENV005", [Started, """{"type":"TEXT_MESSAGE_START","messageId":"m1","name":null}""", Tme, Finished] },
        { "ENV006", [Started, """{"type":"STEP_STARTED","stepName":"look-up","color":"blue"}""", """{"type":"STEP_FINISHED","stepName":"look-up"}""", Finished] },
        { "ENV007", [Started, """{"type":"TEXT_MESSAGE_START","messageId":"m1","role":"narrator"}""", Tme, Finished] },

        // Runs.
        { "RUN001", [Tms, Tme, Finished] },
        { "RUN002", [Started, Started, Finished] },
        { "RUN003", [Started, Finished, Tms] },
        { "RUN003", [Started, """{"type":"RUN_ERROR","message":"boom"}""", """{"type":"RUN_ERROR","message":"again"}"""] },
        { "RUN004", [Started] },
        { "RUN004,TXT003", [Started, Tms, Tmc] },
        { "RUN005", [Started, """{"type":"RUN_FINISHED","threadId":"t-1","runId":"r-2"}"""] },
        { "RUN006", [Started, Finished,
            """{"type":"RUN_STARTED","threadId":"t-2","runId":"r-2","protocolVersion":"1.0"}""",
            """{"type":"RUN_FINISHED","threadId":"t-2","runId":"r-2"}"""] },
        { "RUN007", [Started, Finished, Started, Finished] },
        { "RUN008", [Started, """{"type":"RUN_FINISHED","threadId":"t-1","runId":"r-1","outcome":{"type":"interrupt","interrupts":[{"id":"i1","reason":"tool_call"},{"id":"i1","reason":"input_required"}]}}"""] },
        { "RUN009", [Started, """{"type":"RUN_FINISHED","threadId":"t-1","runId":"r-1","outcome":{"type":"success","interrupts":[{"id":"i1","reason":"tool_call"}]}}"""] },
        { "RUN010", [Started, """{"type":"RUN_FINISHED","threadId":"t-1","runId":"r-1","outcome":{"type":"cancelled"},"result":"partial"}"""] },

        // Versioning and usage.
        { "VER001", ["""{"type":"RUN_STARTED","threadId":"t-1","runId":"r-1"}""", Finished] },
        { "VER002", ["""{"type":"RUN_STARTED","threadId":"t-1","runId":"r-1","protocolVersion":"1.1"}""", Finished] },
        { "USG001", [Started, """{"type":"RUN_FINISHED","threadId":"t-1","runId":"r-1","usage":[{"inputTokens":10,"outputTokens":5,"totalTokens":20}]}"""] },
        { "USG002", [Started, """{"type":"RUN_FINISHED","threadId":"t-1","runId":"r-1","usage":[{"inputTokens":10,"outputTokens":5,"totalTokens":15,"cachedInputTokens":12}]}"""] },

        // Text messages.
        { "TXT001", [Started, Tms, Tms, Tme, Finished] },
        { "TXT002", [Started, Tmc, Finished] },
        { "TXT003", [Started, Tms, Tmc, Finished] },
        { "TXT004", [Started, Tms, Tme, """{"type":"TEXT_MESSAGE_START","messageId":"m1","role":"user"}""", Tme, Finished] },

        // Tool calls.
        { "TOOL001", [Started, Tcs, Tcs, Tce, Finished] },
        { "TOOL002", [Started, Tca, Finished] },
        { "TOOL003", [Started, Tcs, Tca, Finished] },
        { "TOOL004", [Started, Tcs, Tce, """{"type":"TOOL_CALL_START","toolCallId":"c1","toolCallName":"search_parts"}""", Tce, Finished] },
        { "TOOL005", [Started, Tcs, """{"type":"TOOL_CALL_RESULT","messageId":"res-1","toolCallId":"c1","content":"{}"}""", Tce, Finished] },
        { "TOOL006", [Started, Tcs, Tce, """{"type":"RUN_FINISHED","threadId":"t-1","runId":"r-1","outcome":{"type":"success","pendingToolCallIds":["c2"]}}"""] },

        // Reasoning.
        { "RSN001", [Started, Rms, Rms, Rme, Finished] },
        { "RSN002", [Started, """{"type":"REASONING_MESSAGE_CONTENT","messageId":"r1","delta":"hmm"}""", Finished] },
        { "RSN003", [Started, Rms, Finished] },
        { "RSN004", [Started, """{"type":"REASONING_START","messageId":"s1"}""", """{"type":"REASONING_START","messageId":"s1"}""", """{"type":"REASONING_END","messageId":"s1"}""", Finished] },
        { "RSN005", [Started, """{"type":"REASONING_END","messageId":"s1"}""", Finished] },
        { "RSN006", [Started, """{"type":"REASONING_START","messageId":"s1"}""", Finished] },
        { "RSN007", [Started, """{"type":"REASONING_ENCRYPTED_VALUE","subtype":"message","entityId":"nobody","encryptedValue":"b64"}""", Finished] },

        // The chunked form.
        { "CHK001", [Started, """{"type":"TEXT_MESSAGE_CHUNK","delta":"orphan"}""", Finished] },
        { "CHK001", [Started, """{"type":"TOOL_CALL_CHUNK","toolCallId":"c1","delta":"{}"}""", Finished] },
        { "CHK002", [Started, """{"type":"TEXT_MESSAGE_CHUNK","messageId":"m1","role":"user","delta":"a"}""", """{"type":"TEXT_MESSAGE_CHUNK","messageId":"m1","role":"assistant","delta":"b"}""", Finished] },
        { "CHK003", [Started,
            SubStartA, """{"type":"SUBAGENT_STARTED","subagentRunId":"b","name":"warranty-specialist"}""",
            """{"type":"TEXT_MESSAGE_CHUNK","messageId":"m1","delta":"a","subagentRunId":"a"}""",
            """{"type":"TEXT_MESSAGE_CHUNK","messageId":"m2","delta":"b","subagentRunId":"b"}""",
            """{"type":"TEXT_MESSAGE_CHUNK","delta":"whose?"}""",
            SubEndA, """{"type":"SUBAGENT_FINISHED","subagentRunId":"b"}""", Finished] },
        { "CHK004", [Started, """{"type":"TEXT_MESSAGE_CHUNK","messageId":"m1","delta":"a"}""", Tms, Tme, Finished] },

        // Steps.
        { "STEP001", [Started, """{"type":"STEP_STARTED","stepName":"a"}""", """{"type":"STEP_STARTED","stepName":"a"}""", """{"type":"STEP_FINISHED","stepName":"a"}""", Finished] },
        { "STEP002", [Started, """{"type":"STEP_FINISHED","stepName":"a"}""", Finished] },
        { "STEP003", [Started, """{"type":"STEP_STARTED","stepName":"a"}""", Finished] },

        // Messages, state and activity.
        { "MSG001", [Started, Tms, Tme, """{"type":"TOOL_CALL_RESULT","messageId":"m1","toolCallId":"c9","content":"{}"}""", Finished] },
        { "MSG002", [Started, """{"type":"MESSAGES_SNAPSHOT","messages":[{"id":"1","role":"user","content":"a"},{"id":"1","role":"user","content":"b"}]}""", Finished] },
        { "ACT001", [Started, """{"type":"ACTIVITY_DELTA","messageId":"a1","activityType":"parts-search","patch":[]}""", Finished] },
        { "ACT002", [Started,
            """{"type":"ACTIVITY_SNAPSHOT","messageId":"a1","activityType":"parts-search","content":{},"subagentRunId":"x"}""",
            """{"type":"ACTIVITY_SNAPSHOT","messageId":"a1","activityType":"parts-search","content":{},"replace":false,"subagentRunId":"y"}""",
            Finished] },

        // Subagents.
        { "SUB001", [Started, SubStartA, SubStartA, SubEndA, Finished] },
        { "SUB002", [Started, SubStartA, SubEndA, SubStartA, SubEndA, Finished] },
        { "SUB003", [Started, SubEndA, Finished] },
        { "SUB004", [Started, SubStartA, Finished] },
        { "SUB005", [Started, """{"type":"TEXT_MESSAGE_START","messageId":"m1","subagentRunId":"a"}""", """{"type":"TEXT_MESSAGE_CONTENT","messageId":"m1","delta":"x","subagentRunId":"b"}""", Tme, Finished] },
        { "SUB006", [Started, """{"type":"TEXT_MESSAGE_START","messageId":"m1","subagentRunId":"a"}""", Tme, SubStartA, SubEndA, Finished] },
        { "SUB007", [Started, SubStartA, SubEndA, """{"type":"STATE_SNAPSHOT","snapshot":{},"subagentRunId":"a"}""", Finished] },
        { "SUB008", [Started, """{"type":"SUBAGENT_STARTED","subagentRunId":"b","name":"n","parentSubagentRunId":"a"}""", """{"type":"SUBAGENT_FINISHED","subagentRunId":"b"}""", Finished] },
        { "SUB009", [Started, Tms, Tme, """{"type":"TOOL_CALL_START","toolCallId":"c1","toolCallName":"search_parts","parentMessageId":"m1","subagentRunId":"a"}""", Tce, Finished] },
        { "SUB010", [Started, SubStartA, """{"type":"SUBAGENT_FINISHED","subagentRunId":"a","outcome":{"type":"success","interruptIds":["i1"]}}""", Finished] },

        // Passthrough.
        { "PASS001", [Started, """{"type":"CUSTOM","name":"toast","value":1}""", Finished] },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Each_defect_raises_exactly_its_rule(string expected, string[] stream)
    {
        var report = Validate(stream);

        Assert.Equal(expected.Split(',').Order(StringComparer.Ordinal), RuleIds(report));
    }

    [Fact]
    public void Every_rule_in_the_catalog_has_an_invalid_stream()
    {
        var covered = Cases().Select(row => row.Data.Item1.Split(',')[0])
            .Concat(["RUN011", "RUN012", "TOOL007"]) // Need the request; see the facts below.
            .ToHashSet();

        var uncovered = Rules.All.Select(r => r.Id).Where(id => !covered.Contains(id)).ToList();
        Assert.True(uncovered.Count == 0, $"Rules without an invalid stream: {string.Join(", ", uncovered)}");
    }

    [Fact]
    public void Rule_ids_are_unique()
    {
        Assert.Equal(Rules.All.Count, Rules.All.Select(r => r.Id).Distinct().Count());
    }

    // ---- Rules that need the request -------------------------------------------------------------

    [Fact]
    public void A_stream_for_another_thread_is_an_error()
    {
        var report = AGUIStreamValidator.Validate(Events(Started, Finished), new StreamValidatorOptions { ThreadId = "t-9" });

        Assert.Equal(["RUN011"], RuleIds(report));
    }

    [Fact]
    public void The_requested_run_must_be_the_last_run()
    {
        var report = AGUIStreamValidator.Validate(Events(Started, Finished), new StreamValidatorOptions { RunId = "r-2" });

        Assert.Equal(["RUN012"], RuleIds(report));
    }

    [Fact]
    public void A_producer_must_not_answer_a_frontend_tool()
    {
        var input = JsonDocument.Parse("""
            {"threadId":"t-1","runId":"r-1","messages":[],
             "tools":[{"name":"show_work_order","description":"Opens a work order on the dispatcher's screen"}]}
            """).RootElement;

        var report = AGUIStreamValidator.Validate(
            Events(Started,
                """{"type":"TOOL_CALL_START","toolCallId":"c1","toolCallName":"show_work_order"}""",
                Tce,
                """{"type":"TOOL_CALL_RESULT","messageId":"res-1","toolCallId":"c1","content":"shown"}""",
                Finished),
            StreamValidatorOptions.FromRunAgentInput(input));

        Assert.Equal(["TOOL007"], RuleIds(report));
    }

    [Fact]
    public void Encrypted_value_may_name_a_message_from_the_request_history()
    {
        var input = JsonDocument.Parse("""
            {"threadId":"t-1","runId":"r-1","messages":[{"id":"earlier-1","role":"reasoning","content":"..."}]}
            """).RootElement;

        var report = AGUIStreamValidator.Validate(
            Events(Started, """{"type":"REASONING_ENCRYPTED_VALUE","subtype":"message","entityId":"earlier-1","encryptedValue":"b64"}""", Finished),
            StreamValidatorOptions.FromRunAgentInput(input));

        Assert.Empty(report.Diagnostics);
    }

    // ---- Legal, though they can look wrong ----------------------------------------------------------

    public static TheoryData<string, string[]> LegalStreams() => new()
    {
        { "a stream may open with RUN_ERROR", ["""{"type":"RUN_ERROR","message":"agent unreachable"}"""] },
        { "RUN_ERROR may follow RUN_FINISHED", [Started, Finished, """{"type":"RUN_ERROR","message":"flush failed"}"""] },
        { "RUN_ERROR abandons whatever is open", [Started, Tms, Tcs, """{"type":"RUN_ERROR","message":"boom"}"""] },
        { "a new run may follow RUN_ERROR", [Started, """{"type":"RUN_ERROR","message":"boom"}""",
            """{"type":"RUN_STARTED","threadId":"t-1","runId":"r-2","protocolVersion":"1.0"}""",
            """{"type":"RUN_FINISHED","threadId":"t-1","runId":"r-2"}"""] },
        { "a closed message may be reopened", [Started, Tms, Tme, Tms, Tmc, Tme, Finished] },
        { "messages and tool calls interleave", [Started, Tms, Tcs, Tmc, Tca, Tme, Tce, Finished] },
        { "an untagged continuation continues its opener's owner", [Started, SubStartA,
            """{"type":"TEXT_MESSAGE_START","messageId":"m1","subagentRunId":"a"}""", Tmc, Tme, SubEndA, Finished] },
        { "attribution without announcement", [Started, """{"type":"TEXT_MESSAGE_START","messageId":"m1","subagentRunId":"a"}""", Tme, Finished] },
        { "steps are per owner", [Started,
            """{"type":"STEP_STARTED","stepName":"look-up"}""", """{"type":"STEP_STARTED","stepName":"look-up","subagentRunId":"a"}""",
            """{"type":"STEP_FINISHED","stepName":"look-up","subagentRunId":"a"}""", """{"type":"STEP_FINISHED","stepName":"look-up"}""", Finished] },
        { "a span and its message may share an id", [Started, """{"type":"REASONING_START","messageId":"r1"}""", Rms, Rme,
            """{"type":"REASONING_END","messageId":"r1"}""", Finished] },
        { "RAW does not end a chunk stream", [Started, """{"type":"TEXT_MESSAGE_CHUNK","messageId":"m1","delta":"a"}""",
            """{"type":"RAW","event":{"x":1}}""", """{"type":"TEXT_MESSAGE_CHUNK","delta":"b"}""", Finished] },
        { "a pending frontend call ends a successful run", [Started, Tcs, Tca, Tce,
            """{"type":"RUN_FINISHED","threadId":"t-1","runId":"r-1","outcome":{"type":"success","pendingToolCallIds":["c1"]}}"""] },
        { "a cancelled run may report usage", [Started,
            """{"type":"RUN_FINISHED","threadId":"t-1","runId":"r-1","outcome":{"type":"cancelled"},"usage":[{"inputTokens":10,"outputTokens":2,"totalTokens":12}]}"""] },
        { "null inside state or metadata is data", [Started, """{"type":"STATE_SNAPSHOT","snapshot":null,"metadata":{"contoso.note":null}}""", Finished] },
    };

    [Theory]
    [MemberData(nameof(LegalStreams))]
    public void Legal_streams_raise_nothing(string because, string[] stream)
    {
        var report = Validate(stream);

        Assert.True(report.Diagnostics.Count == 0, $"{because}: {string.Join(Environment.NewLine, report.Diagnostics)}");
    }
}

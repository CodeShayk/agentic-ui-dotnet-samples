using System.Net;
using System.Text.Json;
using Beacon.Testing;

namespace Ch15.Tests;

/// <summary>
/// How the agent treats answers: a valid one resumes the run and is applied once; invalid, expired, replayed,
/// unknown and incomplete answers are refused before the run starts; abandoning always works.
/// </summary>
public sealed class ResumeValidationTests : IAsyncLifetime
{
    private readonly AgentHost _host = new();
    private readonly HttpClient _http;

    public ResumeValidationTests() => _http = _host.CreateHttpClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Valid_answers_resume_the_run_record_the_serial_number_and_finish_the_diagnosis()
    {
        var (_, serial, reading) = await Wire.AskAsync(_http, Ct);

        var frames = await AGUIWire.RunRawAsync(_http, "/agent",
            Wire.Resume("r-2", Wire.Answer(serial, Wire.SerialAnswer), Wire.Answer(reading, Wire.ReadingAnswer)), Ct);

        Assert.Equal("HP24S-2207-1184", _host.Store.GetEquipment("EQ-601")!.SerialNumber);
        Assert.Equal(1, _host.Store.EquipmentUpdates);

        var warranty = frames.Single(f => Type(f) == "TOOL_CALL_RESULT").GetProperty("content").GetString()!;
        Assert.Contains("\"covered\":true", warranty, StringComparison.Ordinal);
        Assert.Contains("replace the motor: FAN-HP24", Text(frames), StringComparison.Ordinal);
        Assert.Equal("success", frames[^1].GetProperty("outcome").GetProperty("type").GetString());
    }

    [Fact]
    public async Task An_answer_that_fails_its_schema_is_400_with_field_errors_and_the_question_stays_open()
    {
        var (_, serial, reading) = await Wire.AskAsync(_http, Ct);

        using var response = await Wire.PostAsync(_http, Wire.Resume("r-2",
            Wire.Answer(serial, """{"serialNumber":"HP24S-22O7-1184","readFrom":"nameplate"}"""),
            Wire.Answer(reading, Wire.ReadingAnswer)), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await Wire.ProblemAsync(response, Ct);
        Assert.Equal("Invalid answer", problem.GetProperty("title").GetString());
        Assert.Equal("Serial number isn't in the expected format.", problem.GetProperty("errors").GetProperty("serialNumber")[0].GetString());
        Assert.Null(_host.Store.GetEquipment("EQ-601")!.SerialNumber);

        // Nothing was consumed: the corrected answer works.
        var frames = await AGUIWire.RunRawAsync(_http, "/agent",
            Wire.Resume("r-3", Wire.Answer(serial, Wire.SerialAnswer), Wire.Answer(reading, Wire.ReadingAnswer)), Ct);
        Assert.Equal("success", frames[^1].GetProperty("outcome").GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("""{"amps":2.4,"noise":"grinding","shaftPlay":true,"approved":true}""", "approved")]
    [InlineData("""{"amps":42,"noise":"grinding"}""", "amps")]
    [InlineData("""["grinding"]""", "payload")]
    public async Task The_server_checks_the_schema_it_sent_not_what_the_client_says(string payload, string field)
    {
        var (_, serial, reading) = await Wire.AskAsync(_http, Ct);

        using var response = await Wire.PostAsync(_http, Wire.Resume("r-2", Wire.Answer(serial, Wire.SerialAnswer), Wire.Answer(reading, payload)), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await Wire.ProblemAsync(response, Ct)).GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task An_expired_question_can_be_abandoned_but_not_answered()
    {
        var (_, serial, reading) = await Wire.AskAsync(_http, Ct);
        _host.Clock.Advance(TimeSpan.FromMinutes(15)); // expiresAt is now: now-or-earlier is expired

        using var late = await Wire.PostAsync(_http, Wire.Resume("r-2", Wire.Answer(serial, Wire.SerialAnswer), Wire.Abandon(reading)), Ct);

        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        var problem = await Wire.ProblemAsync(late, Ct);
        Assert.Equal("Interrupt expired", problem.GetProperty("title").GetString());
        Assert.Equal($"Interrupt {serial} expired at 2026-11-16T16:35:00Z; it can only be abandoned.", problem.GetProperty("detail").GetString());

        var frames = await AGUIWire.RunRawAsync(_http, "/agent", Wire.Resume("r-3", Wire.Abandon(serial), Wire.Abandon(reading)), Ct);
        Assert.Equal("success", frames[^1].GetProperty("outcome").GetProperty("type").GetString());
        Assert.Contains("Without the serial number I can't check the warranty", Text(frames), StringComparison.Ordinal);
        Assert.Null(_host.Store.GetEquipment("EQ-601")!.SerialNumber);
    }

    [Fact]
    public async Task A_replayed_answer_is_409_and_applied_once()
    {
        var (_, serial, reading) = await Wire.AskAsync(_http, Ct);
        var resume = Wire.Resume("r-2", Wire.Answer(serial, Wire.SerialAnswer), Wire.Answer(reading, Wire.ReadingAnswer));
        await AGUIWire.RunRawAsync(_http, "/agent", resume, Ct);

        using var replay = await Wire.PostAsync(_http, resume, Ct);

        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        Assert.Equal("Interrupt not open", (await Wire.ProblemAsync(replay, Ct)).GetProperty("title").GetString());
        Assert.Equal(1, _host.Store.EquipmentUpdates);
    }

    [Fact]
    public async Task An_answer_to_a_question_nobody_asked_is_409()
    {
        await Wire.AskAsync(_http, Ct);

        using var forged = await Wire.PostAsync(_http,
            Wire.Resume("r-2", Wire.Answer("inp_00000000000000000000000000000000", Wire.SerialAnswer)), Ct);

        Assert.Equal(HttpStatusCode.Conflict, forged.StatusCode);
        Assert.Equal("Interrupt not open", (await Wire.ProblemAsync(forged, Ct)).GetProperty("title").GetString());
    }

    [Fact]
    public async Task A_question_from_another_thread_is_not_open_on_this_one()
    {
        var (_, serial, _) = await Wire.AskAsync(_http, Ct);
        var other = Wire.Resume("r-2", Wire.Answer(serial, Wire.SerialAnswer));
        other.ThreadId = "t-other";

        using var response = await Wire.PostAsync(_http, other, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Two_questions_are_outstanding_and_answering_one_is_not_enough()
    {
        var (_, serial, reading) = await Wire.AskAsync(_http, Ct);

        using var partial = await Wire.PostAsync(_http, Wire.Resume("r-2", Wire.Answer(serial, Wire.SerialAnswer)), Ct);
        Assert.Equal(HttpStatusCode.Conflict, partial.StatusCode);
        Assert.Equal($"Interrupt {reading} is waiting for an answer. Answer or abandon it first.",
            (await Wire.ProblemAsync(partial, Ct)).GetProperty("detail").GetString());

        using var newMessage = await Wire.PostAsync(_http, Wire.Resume("r-3"), Ct);
        Assert.Equal(HttpStatusCode.Conflict, newMessage.StatusCode);

        // Nothing was decided by either refusal: the serial number isn't recorded yet.
        Assert.Equal(0, _host.Store.EquipmentUpdates);
    }

    [Fact]
    public async Task One_answered_and_one_abandoned_resumes_with_both_outcomes()
    {
        var (_, serial, reading) = await Wire.AskAsync(_http, Ct);

        var frames = await AGUIWire.RunRawAsync(_http, "/agent", Wire.Resume("r-2", Wire.Abandon(serial), Wire.Answer(reading, Wire.ReadingAnswer)), Ct);

        var text = Text(frames);
        Assert.Contains("Without the serial number I can't check the warranty", text, StringComparison.Ordinal);
        Assert.Contains("replace the motor", text, StringComparison.Ordinal);
        Assert.DoesNotContain(frames, f => Type(f) == "TOOL_CALL_START");
    }

    [Theory]
    [InlineData("maybe")]
    [InlineData("")]
    public async Task A_status_other_than_resolved_or_cancelled_is_400(string status)
    {
        var (_, serial, reading) = await Wire.AskAsync(_http, Ct);
        var odd = Wire.Answer(serial, Wire.SerialAnswer);
        odd.Status = status;

        using var response = await Wire.PostAsync(_http, Wire.Resume("r-2", odd, Wire.Abandon(reading)), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_same_question_answered_twice_in_one_resume_is_400()
    {
        var (_, serial, reading) = await Wire.AskAsync(_http, Ct);

        using var response = await Wire.PostAsync(_http,
            Wire.Resume("r-2", Wire.Answer(serial, Wire.SerialAnswer), Wire.Abandon(serial), Wire.Abandon(reading)), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Duplicate resume entry", (await Wire.ProblemAsync(response, Ct)).GetProperty("title").GetString());
    }

    private static string Type(JsonElement frame) => frame.GetProperty("type").GetString()!;

    private static string Text(IEnumerable<JsonElement> frames) =>
        string.Concat(frames.Where(f => Type(f) == "TEXT_MESSAGE_CONTENT").Select(f => f.GetProperty("delta").GetString()));

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _host.DisposeAsync();
    }
}

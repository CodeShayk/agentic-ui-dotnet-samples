using System.Globalization;
using System.Text.Json;

namespace Ch15.Forms;

/// <summary>
/// One open question as a client shows it: the form generated from its response schema (if the schema is in
/// the supported subset), what has been typed so far, whether the person chose to skip it and when it expires.
/// A renderer keeps one per open interrupt for as long as the interrupt is open, so typed values survive
/// every re-render. It knows nothing about AG-UI types or UI frameworks; the Desk and Beacon Field build one
/// from each interrupt's fields and render it their own way.
/// </summary>
public sealed class FormQuestion
{
    /// <param name="id">The interrupt's ID.</param>
    /// <param name="message">The interrupt's <c>message</c>.</param>
    /// <param name="responseSchema">The interrupt's <c>responseSchema</c>, if it has one.</param>
    /// <param name="expiresAt">The interrupt's <c>expiresAt</c>, as the producer wrote it.</param>
    /// <param name="metadata">The interrupt's <c>metadata</c>.</param>
    public FormQuestion(string id, string? message, JsonElement? responseSchema, string? expiresAt, JsonElement? metadata)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        (Id, Message, Metadata) = (id, message, metadata);

        // No schema, or one outside the subset: show the message, and offer only to skip. Guessing the
        // shape of an answer would send the agent something it never asked for.
        if (responseSchema is not { } schema)
        {
            Unsupported = ["This question has no response schema."];
        }
        else if (FormSchema.TryParse(schema, out var form, out var problems))
        {
            Draft = new FormDraft(form);
        }
        else
        {
            Unsupported = problems;
        }

        ExpiresAt = DateTimeOffset.TryParse(expiresAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null;
    }

    public string Id { get; }

    public string? Message { get; }

    public JsonElement? Metadata { get; }

    /// <summary>The form's state, or <see langword="null"/> when this question can't be rendered.</summary>
    public FormDraft? Draft { get; }

    /// <summary>Why there is no form.</summary>
    public IReadOnlyList<string> Unsupported { get; } = [];

    /// <summary>When the agent stops accepting an answer.</summary>
    public DateTimeOffset? ExpiresAt { get; }

    /// <summary>The person chose not to answer: the question goes back abandoned.</summary>
    public bool Skipped { get; set; }

    public string Title => Draft?.Schema.Title ?? "Beacon has a question";

    /// <summary>The same reading of <c>expiresAt</c> as Beacon.Client.Core's: now or earlier is expired.</summary>
    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } at && at <= now;

    /// <summary>Whether this question can only go back abandoned.</summary>
    public bool CanOnlySkip(DateTimeOffset now) => Skipped || Draft is null || IsExpired(now);

    /// <summary>
    /// Context for the person from Beacon's own metadata keys (under <c>beacon</c>): the unit, and a motor's
    /// rated current. A generic client would ignore them; the answer never depends on them.
    /// </summary>
    public IReadOnlyList<string> Context()
    {
        if (Metadata is not { ValueKind: JsonValueKind.Object } metadata
            || !metadata.TryGetProperty("beacon", out var beacon) || beacon.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        List<string> lines = [];
        if (Text(beacon, "equipmentId") is { } equipment)
        {
            lines.Add(Text(beacon, "model") is { } model ? $"{equipment} · {model}" : equipment);
        }

        if (beacon.TryGetProperty("ratedAmps", out var rated) && rated.ValueKind == JsonValueKind.Number)
        {
            lines.Add($"Rated current: {rated.GetDecimal().ToString(CultureInfo.InvariantCulture)} A");
        }

        return lines;
    }

    /// <summary>
    /// Decides every open question at once, because a resume must cover all of them: each one is answered with
    /// a valid payload or abandoned (skipped, expired or unrenderable). Fails if any answer still has problems.
    /// </summary>
    /// <param name="questions">Every open question of the interrupted run.</param>
    /// <param name="now">The client's clock.</param>
    /// <param name="decided">The answers and the abandoned questions, on success.</param>
    /// <param name="firstInvalid">The first question whose form has problems, on failure. Every field is marked visited.</param>
    public static bool TryDecide(IReadOnlyList<FormQuestion> questions, DateTimeOffset now, out QuestionAnswers decided, out FormQuestion? firstInvalid)
    {
        ArgumentNullException.ThrowIfNull(questions);
        List<QuestionAnswer> answers = [];
        List<string> abandoned = [];
        firstInvalid = null;

        foreach (var question in questions)
        {
            if (question.CanOnlySkip(now))
            {
                abandoned.Add(question.Id);
                continue;
            }

            // Every field now shows its problem, if it has one: the same checks the agent will run.
            question.Draft!.TouchAll();
            if (question.Draft.TryGetPayload(out var payload, out _))
            {
                answers.Add(new QuestionAnswer(question.Id, payload));
            }
            else
            {
                firstInvalid ??= question;
            }
        }

        decided = new QuestionAnswers(answers, abandoned);
        return firstInvalid is null;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>An answer a client checked and is ready to send.</summary>
/// <param name="InterruptId">The interrupt it answers.</param>
/// <param name="Payload">The JSON object built from the form.</param>
public sealed record QuestionAnswer(string InterruptId, JsonElement Payload);

/// <summary>What the person decided about every open question: answers, and the questions abandoned.</summary>
public sealed record QuestionAnswers(IReadOnlyList<QuestionAnswer> Answers, IReadOnlyList<string> Abandoned)
{
    public IEnumerable<string> InterruptIds => Answers.Select(a => a.InterruptId).Concat(Abandoned);
}

using System.Text.Json;
using Ch15.Forms;

namespace Ch15.Beacon.Desk.Components.Questions;

/// <summary>What this Desk sent for each question, for the log in the work area. One per circuit.</summary>
public sealed class SentAnswers
{
    private readonly Dictionary<string, SentAnswer> _sent = new(StringComparer.Ordinal);

    /// <summary>Raised after every change, on the thread that made it.</summary>
    public event EventHandler? Changed;

    public SentAnswer? Find(string interruptId) => _sent.GetValueOrDefault(interruptId);

    public void Record(QuestionAnswers answers)
    {
        foreach (var answer in answers.Answers)
        {
            _sent[answer.InterruptId] = new SentAnswer(Abandoned: false, Summarize(answer.Payload));
        }

        foreach (var id in answers.Abandoned)
        {
            _sent[id] = new SentAnswer(Abandoned: true, "Skipped");
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Forget(QuestionAnswers answers)
    {
        foreach (var id in answers.InterruptIds)
        {
            _sent.Remove(id);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        _sent.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // "serialNumber: HP24S-2207-1184 · readFrom: nameplate": the payload as Dana would read it back.
    private static string Summarize(JsonElement payload) =>
        string.Join(" · ", payload.EnumerateObject().Select(p => $"{p.Name}: {(p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText())}"));
}

/// <param name="Abandoned">Skipped rather than answered.</param>
/// <param name="Summary">What was sent, as text.</param>
public sealed record SentAnswer(bool Abandoned, string Summary);

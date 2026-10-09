using System.Net;
using Beacon.Client.Core;
using Ch15.Forms;
using Microsoft.AspNetCore.Components;

namespace Ch15.Beacon.Desk.Components.Questions;

public sealed partial class InterruptPanel : IDisposable
{
    private readonly string _headingId = $"questions-{Guid.NewGuid():N}";
    private readonly List<FormQuestion> _questions = [];
    private readonly Dictionary<string, SchemaForm> _forms = new(StringComparer.Ordinal);

    private ElementReference _heading;
    private Func<Task>? _focusNext;
    private ITimer? _tick;
    private string? _error;
    private bool _sending;
    private RunFailure? _shownFailure;

    [Inject] private TimeProvider Clock { get; set; } = default!;

    [Inject] private DispatcherTime Time { get; set; } = default!;

    /// <summary>The conversation's open input interrupts.</summary>
    [Parameter, EditorRequired] public IReadOnlyList<InterruptView> Interrupts { get; set; } = [];

    /// <summary>Raised with a decision for every open question. The panel stays disabled until the handler finishes.</summary>
    [Parameter] public EventCallback<QuestionAnswers> OnSubmit { get; set; }

    /// <summary>
    /// Raised by New chat, which the panel offers once Beacon has refused the answers with a 409: the chat's own
    /// New chat waits while questions are open, and a refusal can mean nothing on the thread is left to answer.
    /// </summary>
    [Parameter] public EventCallback OnNewChat { get; set; }

    protected override void OnParametersSet()
    {
        // Keep the questions that are still open (and what Dana typed into them); add the new ones.
        var open = Interrupts.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        _questions.RemoveAll(q => !open.Contains(q.Id));
        var arrived = Interrupts.Where(i => !_questions.Exists(q => q.Id == i.Id)).Select(i => i.ToQuestion()).ToList();
        _questions.AddRange(arrived);

        if (arrived.Count > 0)
        {
            // New questions: start fresh and take focus, so a keyboard or screen-reader user lands on them.
            (_error, _sending) = (null, false);
            _focusNext = () => _heading.FocusAsync().AsTask();
        }
        else if (Refused is { } failure && !ReferenceEquals(failure, _shownFailure))
        {
            // The same questions, back because the agent didn't take the answers: what Dana typed is still
            // here, and focus goes to the heading so the alert is read in context.
            _focusNext = () => _heading.FocusAsync().AsTask();
        }

        _shownFailure = Refused;
        ScheduleTick();
    }

    // Why the last answers didn't reach a run, if they didn't. The questions are still open.
    private RunFailure? Refused => Interrupts.Select(i => i.LastAnswerFailure).FirstOrDefault(f => f is not null);

    private bool CanStartOver => Refused is { Kind: FailureKind.Rejected, StatusCode: not HttpStatusCode.BadRequest };

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_focusNext is { } focus)
        {
            _focusNext = null;
            await focus();
        }
    }

    private async Task SubmitAsync()
    {
        // One decision for every question: the resume must cover them all.
        if (!FormQuestion.TryDecide(_questions, Clock.GetUtcNow(), out var decided, out var invalid))
        {
            _error = "Some answers need fixing before Beacon can use them.";
            _focusNext = _forms.GetValueOrDefault(invalid!.Id) is { } form ? async () => await form.FocusFirstInvalidAsync() : null;
            return;
        }

        (_error, _sending) = (null, true);
        try
        {
            await OnSubmit.InvokeAsync(decided);
        }
        finally
        {
            _sending = false;
        }
    }

    // Re-render whenever a question's minutes left change, so the countdown, the warning and an expired card
    // all change without anyone touching the page.
    private void ScheduleTick()
    {
        _tick?.Dispose();
        _tick = null;

        var now = Clock.GetUtcNow();
        if (_questions.Where(q => q.ExpiresAt > now).Select(q => DispatcherTime.UntilNextChange(q.ExpiresAt!.Value, now)).DefaultIfEmpty().Min() is { } next
            && next > TimeSpan.Zero)
        {
            _tick = Clock.CreateTimer(_ => _ = InvokeAsync(() =>
            {
                ScheduleTick();
                StateHasChanged();
            }), null, next, Timeout.InfiniteTimeSpan);
        }
    }

    // What the polite live region says: the answers are on their way, or time is running out on a question.
    private string? StatusText(DateTimeOffset now) =>
        _sending ? "Sending your answers. They count once Beacon starts on them."
        : _questions.Exists(q => !q.CanOnlySkip(now) && q.ExpiresAt is { } at && DispatcherTime.IsClose(at, now))
            ? "Two minutes or less left to answer. An expired question can only be skipped; ask Beacon again for a new one."
            : null;

    // Why the last answers didn't take, and the two ways on: answer again, or skip the questions.
    private static string Refusal(RunFailure failure) => failure switch
    {
        { Kind: FailureKind.Rejected, StatusCode: HttpStatusCode.BadRequest } =>
            "Beacon couldn't use those answers. Correct them and send again, or skip the questions.",
        { Kind: FailureKind.Rejected } =>
            "Beacon didn't accept those answers. The questions may have expired at the agent, or the agent restarted. "
            + "Send again or skip them; if Beacon refuses that too, start a new chat.",
        _ => "Your answers may not have reached Beacon. Send them again, or skip the questions.",
    };

    private string Heading() => _questions.Count == 1 ? "Beacon needs an answer" : $"Beacon needs {_questions.Count} answers";

    private string SendLabel(DateTimeOffset now) =>
        _questions.All(q => q.CanOnlySkip(now)) ? "Skip and carry on" : "Send answers";

    public void Dispose() => _tick?.Dispose();
}

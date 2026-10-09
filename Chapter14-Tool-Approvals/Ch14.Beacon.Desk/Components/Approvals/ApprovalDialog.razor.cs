using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Beacon.Client.Core;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Ch14.Beacon.Desk.Components.Approvals;

public sealed partial class ApprovalDialog : IDisposable
{
    private enum Step
    {
        Choose,
        Deny,
        Edit,
    }

    private readonly string _titleId = NewId("title");
    private readonly string _messageId = NewId("message");
    private readonly string _reasonId = NewId("reason");
    private readonly string _reasonHintId = NewId("reason-hint");
    private readonly string _errorId = NewId("error");
    private readonly string _fieldPrefix = NewId("field");

    private ElementReference _title;
    private ElementReference _edit;
    private ElementReference _deny;
    private ElementReference _reasonBox;
    private ElementReference[] _fields = [];

    private Dictionary<string, string> _values = [];
    private string? _shownId;
    private Step _step;
    private string _reason = string.Empty;
    private string? _error;
    private bool _sending;
    private RunFailure? _shownFailure;
    private ITimer? _tick;

    // Where focus goes after the next render: a new request focuses its title, a step its first field,
    // Back the button that opened the step. Nothing moves focus otherwise.
    private Func<ElementReference>? _focusNext;

    [Inject] private TimeProvider Clock { get; set; } = default!;

    [Inject] private DispatcherTime Time { get; set; } = default!;

    /// <summary>The approval to show.</summary>
    [Parameter, EditorRequired] public ApprovalRequest Request { get; set; } = default!;

    /// <summary>Raised with the dispatcher's decision. The dialog stays disabled until the handler finishes.</summary>
    [Parameter] public EventCallback<ApprovalDecision> OnDecided { get; set; }

    /// <summary>Raised by New chat, which the dialog offers once Beacon has refused an answer with a 409.</summary>
    [Parameter] public EventCallback OnNewChat { get; set; }

    protected override void OnParametersSet()
    {
        if (Request.Id != _shownId)
        {
            // A new request: start over, and take focus so a keyboard or screen-reader user lands on it.
            _shownId = Request.Id;
            _step = Step.Choose;
            _reason = string.Empty;
            _error = null;
            _sending = false;
            _shownFailure = Request.LastFailure;
            _values = Request.EditableFields.ToDictionary(f => f.Name, f => f.Value, StringComparer.Ordinal);
            _fields = new ElementReference[Request.EditableFields.Count];
            _focusNext = () => _title;
            ScheduleTick();
        }
        else if (Request.LastFailure is { } failure && !ReferenceEquals(failure, _shownFailure))
        {
            // The same request, back because the agent didn't take the answer: the choices again, with what
            // Dana typed kept, and focus on the title so the alert is read in context.
            (_shownFailure, _step, _error) = (failure, Step.Choose, null);
            _focusNext = () => _title;
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_focusNext is { } target)
        {
            _focusNext = null;
            await target().FocusAsync();
        }
    }

    private void Go(Step step)
    {
        _step = step;
        _error = null;
        _focusNext = step == Step.Edit && _fields.Length > 0 ? () => _fields[0] : () => _reasonBox;
    }

    private void Back()
    {
        var from = _step;
        _step = Step.Choose;
        _error = null;
        _focusNext = from == Step.Edit ? () => _edit : () => _deny;
    }

    private void OnKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Escape" && _step != Step.Choose && !_sending)
        {
            Back();
        }
    }

    private Task SubmitAsync()
    {
        if (string.IsNullOrWhiteSpace(_reason))
        {
            return Fail(_step == Step.Deny ? "Give a reason for declining." : "Give a reason for the change.");
        }

        if (_step == Step.Deny)
        {
            return DecideAsync(new ApprovalDecision(ApprovalChoice.Deny, _reason.Trim()));
        }

        // An edit is a full replacement of the arguments: the proposal, with the edited fields swapped in.
        var edited = JsonNode.Parse(Request.Arguments.GetRawText()) as JsonObject ?? [];
        foreach (var field in Request.EditableFields)
        {
            if (Parse(field, _values[field.Name]) is not { } value)
            {
                return Fail($"{field.Label}: {Describe(field)}");
            }

            edited[field.Name] = value;
        }

        if (JsonNode.DeepEquals(edited, JsonNode.Parse(Request.Arguments.GetRawText())))
        {
            return Fail("Nothing changed. Change a value, or go back and approve the request as it is.");
        }

        return DecideAsync(new ApprovalDecision(ApprovalChoice.Edit, _reason.Trim(), edited));

        Task Fail(string message)
        {
            _error = message;
            return Task.CompletedTask;
        }
    }

    private async Task DecideAsync(ApprovalDecision decision)
    {
        if (_sending)
        {
            return; // one answer per request, however fast the clicks
        }

        _sending = true;
        try
        {
            await OnDecided.InvokeAsync(decision);
        }
        finally
        {
            _sending = false;
        }
    }

    // Renders again whenever the minutes left change; the warning and the expiry fall on those moments.
    private void ScheduleTick()
    {
        _tick?.Dispose();
        _tick = Request.ExpiresAt is { } expiresAt
            ? Clock.CreateTimer(_ => _ = InvokeAsync(() =>
            {
                ScheduleTick();
                StateHasChanged();
            }), null, DispatcherTime.UntilNextChange(expiresAt, Clock.GetUtcNow()), Timeout.InfiniteTimeSpan)
            : null;
    }

    // What the polite live region says: the answer is on its way, or time is running out.
    private string? StatusText(DateTimeOffset now) =>
        _sending ? "Sending your answer. It counts once Beacon starts on it."
        : !Request.AlreadyCarriedOut && Request.ExpiresAt is { } expiresAt && DispatcherTime.IsClose(expiresAt, now)
            ? "Two minutes or less left to answer. If time runs out, dismiss this request and ask Beacon again."
            : null;

    // A 409 means the agent holds nothing this answer can change, so New chat joins the choices.
    private bool Refused => Request.LastFailure is { Kind: FailureKind.Rejected, StatusCode: not HttpStatusCode.BadRequest };

    // Why the last answer didn't take, and the ways on. An approval Beacon already carried out is the one case
    // where answering again is wrong: the call ran in a run whose end the Desk never saw, so Dana checks first.
    internal static string Refusal(ApprovalRequest request) => request switch
    {
        { AlreadyCarriedOut: true } =>
            $"Beacon already acted on this approval in an earlier run. Check the {Noun(request.ToolName)} in a new chat before asking again.",
        { LastFailure: { Kind: FailureKind.Rejected, StatusCode: HttpStatusCode.BadRequest } } =>
            "Beacon couldn't use that answer. Change it and send it again, or dismiss the request.",
        { LastFailure.Kind: FailureKind.Rejected } =>
            "Beacon didn't accept that answer. The request may have expired at the agent or been answered elsewhere, "
            + "or the agent restarted. Answer again or dismiss it; if Beacon refuses that too, start a new chat.",
        _ => "Your answer may not have reached Beacon. Send it again or dismiss the request; Beacon never carries out the same answer twice.",
    };

    private static string Noun(string toolName) => toolName switch
    {
        "place_part_order" => "order",
        "charge_customer" => "charge",
        _ => "work order",
    };

    public void Dispose() => _tick?.Dispose();

    // The server checks all of this again; the dialog checks it so Dana gets the message here, not a 400.
    private static JsonNode? Parse(EditableField field, string text)
    {
        text = text.Trim();
        switch (field.Kind)
        {
            case EditableKind.Integer when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole) && InRange(field, whole):
                return JsonValue.Create(whole);
            case EditableKind.Number when decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
                                          && decimal.Round(number, 2) == number && InRange(field, number):
                return JsonValue.Create(number);
            case EditableKind.Text when text.Length > 0 && (field.MaxLength is null || text.Length <= field.MaxLength):
                return JsonValue.Create(text);
            default:
                return null;
        }
    }

    private static bool InRange(EditableField field, decimal value) =>
        (field.Minimum is not { } min || (field.ExclusiveMinimum ? value > min : value >= min))
        && (field.Maximum is not { } max || value <= max);

    private static string Describe(EditableField field) => field.Kind switch
    {
        EditableKind.Integer => $"enter a whole number from {field.Minimum} to {field.Maximum}.",
        EditableKind.Number => $"enter an amount above {field.Minimum:0.##} and up to {field.Maximum:N2}, in dollars and cents.",
        _ => $"enter 1 to {field.MaxLength} characters.",
    };

    private IEnumerable<(string Label, string Value)> Arguments()
    {
        if (Request.Arguments.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var argument in Request.Arguments.EnumerateObject())
        {
            var value = argument.Value.ValueKind == JsonValueKind.String ? argument.Value.GetString()! : argument.Value.GetRawText();
            yield return (Request.EditableFields.FirstOrDefault(f => f.Name == argument.Name)?.Label ?? Label(argument.Name), value);
        }

        if (Request.Amount is { } amount)
        {
            yield return ("Cost", amount.ToString("C2", CultureInfo.GetCultureInfo("en-US")));
        }
    }

    private static string Label(string name) => name switch
    {
        "workOrderId" => "Work order",
        "sku" => "Part",
        _ => name,
    };

    private string FieldId(EditableField field) => $"{_fieldPrefix}-{field.Name}";

    private static string NewId(string part) => $"ch14-approval-{part}-{Guid.NewGuid():N}";
}

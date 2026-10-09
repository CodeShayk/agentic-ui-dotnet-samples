using Ch10.Field.Tools;
using Microsoft.Maui.ApplicationModel;

namespace Ch10.Beacon.Field.Devices;

/// <summary>
/// Beacon Field's consent questions, asked with the app's own dialogs before any device feature is used.
/// The operating system's permission dialog comes afterwards and answers a different question: whether
/// the app may ever use the feature, not whether Beacon may use it now.
/// </summary>
public sealed class DialogConsentPrompt : IConsentPrompt
{
    private const string AllowOnce = "Allow once";
    private const string AllowForSession = "Allow until I close the app";
    private const string Deny = "Don't allow";

    public async Task<ConsentDecision> AskAsync(ConsentRequest request, CancellationToken cancellationToken)
    {
        string[] choices = request.OfferSessionAllowance ? [AllowOnce, AllowForSession] : [AllowOnce];

        // Dialogs belong to the main thread. A cancelled turn stops waiting; the dialog's answer is then ignored.
        var answer = await MainThread.InvokeOnMainThreadAsync(() =>
                CurrentPage().DisplayActionSheetAsync($"{request.Title}\n{request.Reason}", Deny, null, choices))
            .WaitAsync(cancellationToken);

        return answer switch
        {
            AllowOnce => ConsentDecision.AllowOnce,
            AllowForSession => ConsentDecision.AllowForSession,
            _ => ConsentDecision.Deny, // "Don't allow", or the sheet dismissed
        };
    }

    internal static Page CurrentPage() =>
        Application.Current?.Windows.FirstOrDefault()?.Page
        ?? throw new InvalidOperationException("Beacon Field has no window to ask in.");
}

/// <summary>
/// Reads a part label by asking the technician to type the part number. .NET MAUI has no barcode API,
/// and Beacon uses only .NET's own libraries, so manual entry is the fallback that works everywhere; a
/// platform barcode reader can replace this class without changing the tool.
/// </summary>
public sealed class ManualLabelReader : IPartLabelReader
{
    public async Task<LabelReading?> ReadAsync(LabelReadRequest request, CancellationToken cancellationToken)
    {
        var message = request.WorkOrderId is { } job
            ? $"Beacon needs the part number on the label, for {job}. Type it as printed, for example CAP-45-5."
            : "Beacon needs the part number on the label. Type it as printed, for example CAP-45-5.";

        var text = await MainThread.InvokeOnMainThreadAsync(() => DialogConsentPrompt.CurrentPage().DisplayPromptAsync(
                "Read the part label", message, accept: "Send to Beacon", cancel: "Cancel",
                placeholder: "CAP-45-5", maxLength: 32, keyboard: Keyboard.Text))
            .WaitAsync(cancellationToken);

        return string.IsNullOrWhiteSpace(text) ? null : new LabelReading(text, "manual");
    }
}

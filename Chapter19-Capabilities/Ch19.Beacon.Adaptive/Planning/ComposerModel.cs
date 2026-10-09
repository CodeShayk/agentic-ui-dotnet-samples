using Microsoft.Extensions.AI;

namespace Ch19.Beacon.Adaptive.Planning;

/// <summary>
/// A composer that follows a plan: its input buttons, the draft, the attachment tray and what pressing
/// each button does. Beacon Field binds its page to it; tests drive it with a fake <see cref="IMediaCapture"/>.
/// </summary>
public sealed class ComposerModel(FeaturePlan plan, IMediaCapture capture, params BeaconFeature[] inputs)
{
    public AttachmentTray Tray { get; } = new(plan);

    /// <summary>One button per input feature, in the order given, visible or not.</summary>
    public IReadOnlyList<InputButton> Buttons { get; } = [.. inputs.Select(feature => InputButton.For(plan[feature]))];

    /// <summary>Visible notes for the inputs: why one is unconfirmed, degraded or missing.</summary>
    public IReadOnlyList<string> Notes => [.. Buttons.Select(b => b.Caption).OfType<string>()];

    public string Draft { get; set; } = string.Empty;

    /// <summary>Why the last capture couldn't be attached, if it couldn't.</summary>
    public string? LastRefusal { get; private set; }

    public InputButton Button(BeaconFeature feature) => Buttons.First(b => b.Feature == feature);

    /// <summary>Does what the plan says the button does: capture into the tray, or put the template in the draft.</summary>
    public async Task PressAsync(BeaconFeature feature, CancellationToken cancellationToken = default)
    {
        var button = Button(feature);
        LastRefusal = null;
        switch (button.Action)
        {
            case InputAction.InsertTemplate:
                Draft = string.IsNullOrWhiteSpace(Draft) ? button.Template ?? string.Empty : $"{Draft.TrimEnd()} {button.Template}";
                break;

            case InputAction.Capture:
                if (await capture.CaptureAsync(feature, cancellationToken).ConfigureAwait(false) is { } media)
                {
                    LastRefusal = Tray.TryAdd(media);
                }

                break;
        }
    }

    /// <summary>The message to send, or <see langword="null"/> when there's nothing to send. Clears the draft and the tray.</summary>
    public ChatMessage? TakeMessage()
    {
        if (string.IsNullOrWhiteSpace(Draft) && Tray.Items.Count == 0)
        {
            return null;
        }

        var message = Tray.ToMessage(Draft.Trim());
        Tray.Clear();
        Draft = string.Empty;
        return message;
    }
}

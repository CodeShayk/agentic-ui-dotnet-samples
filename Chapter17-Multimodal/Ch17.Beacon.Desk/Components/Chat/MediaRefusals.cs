using Beacon.Client.Core;
using Ch17.Media;

namespace Ch17.Beacon.Desk.Components.Chat;

/// <summary>
/// The parts Beacon set aside, from the <c>beacon.media_refused</c> <c>CUSTOM</c> events in the
/// conversation. The agent screens the whole history on every run, so the same refusal can arrive more
/// than once; it's keyed by message and part, and the latest reason wins.
/// </summary>
public static class MediaRefusals
{
    public static IReadOnlyDictionary<(string MessageId, int Part), string> From(ConversationView view)
    {
        Dictionary<(string, int), string> refused = [];
        foreach (var evt in view.CustomEvents.Where(e => e.Name == MediaRefusalReader.EventName))
        {
            foreach (var part in MediaRefusalReader.Read(evt.Value))
            {
                refused[(part.MessageId, part.Part)] = part.Reason;
            }
        }

        return refused;
    }
}

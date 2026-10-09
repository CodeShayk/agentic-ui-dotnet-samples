using System.Collections.Concurrent;

namespace Ch10.Field.Tools;

/// <summary>
/// The technician's consent, remembered for the session when she chooses that. Each tool asks through
/// here before it touches a device feature, so "allow for this session" means one question per feature,
/// and revoking it means the next use asks again.
/// </summary>
public sealed class SessionConsent(IConsentPrompt prompt)
{
    private readonly ConcurrentDictionary<DeviceCapability, bool> _allowedForSession = new();

    /// <summary>Whether the technician allows this use, asking her unless she already allowed the feature for the session.</summary>
    public async Task<bool> AllowsAsync(ConsentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.OfferSessionAllowance && _allowedForSession.ContainsKey(request.Capability))
        {
            return true;
        }

        var decision = await prompt.AskAsync(request, cancellationToken).ConfigureAwait(false);
        if (decision == ConsentDecision.AllowForSession && request.OfferSessionAllowance)
        {
            _allowedForSession[request.Capability] = true;
        }

        return decision != ConsentDecision.Deny;
    }

    /// <summary>Whether a feature is currently allowed for the session.</summary>
    public bool IsAllowedForSession(DeviceCapability capability) => _allowedForSession.ContainsKey(capability);

    /// <summary>Forgets a session allowance: the next use asks again.</summary>
    public void Revoke(DeviceCapability capability) => _allowedForSession.TryRemove(capability, out _);
}

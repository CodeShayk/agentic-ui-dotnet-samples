using Microsoft.AspNetCore.Components.Authorization;

namespace Ch27.Beacon.Desk.Identity;

/// <summary>
/// Puts the signed-in user's token for the agent on every request the Desk's chat client sends, so the agent
/// sees the dispatcher, not the Desk. It belongs to one circuit: <see cref="AuthenticationStateProvider"/> is
/// scoped to the circuit, which is why the Desk builds this handler in the circuit's scope instead of
/// registering it with <c>AddHttpMessageHandler</c>, whose handlers live in a scope of their own with no user.
/// </summary>
public sealed class AgentAccessTokenHandler : DelegatingHandler
{
    private readonly AuthenticationStateProvider _authentication;
    private readonly IAgentTokenSource _tokens;
    private Task<AuthenticationState>? _state;

    public AgentAccessTokenHandler(AuthenticationStateProvider authentication, IAgentTokenSource tokens)
    {
        _authentication = authentication;
        _tokens = tokens;

        // The circuit's user can change (sign-out, a refreshed identity); always use the latest.
        authentication.AuthenticationStateChanged += OnAuthenticationStateChanged;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var user = (await (_state ??= _authentication.GetAuthenticationStateAsync())).User;
        if (user.Identity?.IsAuthenticated == true)
        {
            request.Headers.Authorization = new("Bearer", await _tokens.GetTokenAsync(user, cancellationToken));
        }

        // No user, no token: the agent answers 401, and the Desk asks the dispatcher to sign in again.
        return await base.SendAsync(request, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _authentication.AuthenticationStateChanged -= OnAuthenticationStateChanged;
        }

        base.Dispose(disposing);
    }

    private void OnAuthenticationStateChanged(Task<AuthenticationState> state) => _state = state;
}

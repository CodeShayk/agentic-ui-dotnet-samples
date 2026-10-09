using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Identity.Web;

namespace Ch27.Beacon.Desk.Identity;

public static class DeskAuthentication
{
    /// <summary>Where the Desk sends a signed-in user whose token for the agent is gone.</summary>
    public const string SignInAgainPath = "/account/sign-in-again";

    /// <summary>
    /// Every page of the Desk needs a signed-in user. Deployed, dispatchers sign in with Microsoft Entra ID
    /// (OpenID Connect through Microsoft.Identity.Web), which also fetches each user's token for the agent. On a
    /// developer's machine, with no Entra settings, <c>/dev/sign-in</c> signs in Dana or Priya and the Desk signs
    /// their agent tokens with the AppHost's development key. Anywhere else without Entra settings, the Desk
    /// refuses to start rather than run open.
    /// </summary>
    public static IHostApplicationBuilder AddDeskAuthentication(this IHostApplicationBuilder builder)
    {
        builder.Services.Configure<AgentAccessOptions>(builder.Configuration.GetSection(AgentAccessOptions.SectionName));

        var entra = builder.Configuration.GetSection("AzureAd");
        if (!string.IsNullOrWhiteSpace(entra["ClientId"]))
        {
            var scopes = builder.Configuration.GetSection(AgentAccessOptions.SectionName).Get<AgentAccessOptions>()?.Scopes ?? [];
            builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
                .AddMicrosoftIdentityWebApp(entra)
                .EnableTokenAcquisitionToCallDownstreamApi(scopes)
                .AddInMemoryTokenCaches(); // this process's: a restart loses it, and SignInRequiredException sends the user back
            builder.Services.AddScoped<IAgentTokenSource, EntraAgentTokenSource>();
        }
        else if (builder.Environment.IsDevelopment())
        {
            builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                .AddCookie(cookie => cookie.LoginPath = "/dev/sign-in");
            builder.Services.Configure<DevelopmentTokenOptions>(builder.Configuration.GetSection(DevelopmentTokenOptions.SectionName));
            builder.Services.AddSingleton<IAgentTokenSource, DevelopmentAgentTokenSource>();
        }
        else
        {
            throw new InvalidOperationException(
                "Beacon Desk has no sign-in configured. Set AzureAd:TenantId, AzureAd:ClientId and AzureAd:ClientSecret.");
        }

        // A fallback policy covers every endpoint that doesn't say otherwise: pages, the Blazor hub, everything.
        builder.Services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        builder.Services.AddCascadingAuthenticationState();
        return builder;
    }

    /// <summary>
    /// Challenges the signed-in user again with the Desk's own scheme: Microsoft Entra ID, which signs them in (usually
    /// without a prompt) and refills the token cache, or the development sign-in page. Then back to a local page.
    /// </summary>
    public static IEndpointConventionBuilder MapSignInAgain(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet(SignInAgainPath, (string? returnUrl) => Results.Challenge(new AuthenticationProperties
        {
            // Only a path on this site: an absolute or protocol-relative URL would be an open redirect.
            RedirectUri = returnUrl is { Length: > 0 } url && url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal)
                && !url.StartsWith("/\\", StringComparison.Ordinal) ? url : "/",
        }));
}

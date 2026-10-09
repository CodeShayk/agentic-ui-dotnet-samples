using System.Security.Cryptography;

/// <summary>Who may call the agent, and where the settings that decide it are kept.</summary>
internal static class BeaconIdentity
{
    private const string DevelopmentIssuer = "beacon-dev";
    private const string DevelopmentAudience = "beacon-agent";

    /// <summary>
    /// Under <c>aspire run</c>: a fresh signing key for every run of the AppHost. The agent trusts tokens signed
    /// with it, the way Chapter 23's <c>dotnet user-jwts</c> settings work; the Desk and the smoke test sign their
    /// users' tokens with it. Nothing is deployed with it: in publish mode it doesn't exist.
    /// </summary>
    public static void WithDevelopmentTokens(
        this IDistributedApplicationBuilder builder,
        IResourceBuilder<ProjectResource> agent,
        params IResourceBuilder<ProjectResource>[] callers)
    {
        if (!builder.ExecutionContext.IsRunMode)
        {
            return;
        }

        var key = builder.AddParameter("dev-token-key", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), secret: true);

        agent.WithEnvironment("Authentication__Schemes__Bearer__ValidIssuer", DevelopmentIssuer)
            .WithEnvironment("Authentication__Schemes__Bearer__ValidAudiences__0", DevelopmentAudience)
            .WithEnvironment("Authentication__Schemes__Bearer__SigningKeys__0__Issuer", DevelopmentIssuer)
            .WithEnvironment("Authentication__Schemes__Bearer__SigningKeys__0__Value", key);

        foreach (var caller in callers)
        {
            caller.WithEnvironment("BeaconAgent__DevelopmentTokens__SigningKey", key);
        }
    }

    /// <summary>
    /// When the AppHost publishes: Chapter 23's redaction key and bearer-token settings become secret parameters.
    /// Each one is published as a Container Apps secret that the environment variable references, so the values
    /// appear in no image, no source file and no template; the deployment supplies them.
    /// </summary>
    public static IResourceBuilder<ProjectResource> WithAgentSecrets(
        this IResourceBuilder<ProjectResource> agent, IDistributedApplicationBuilder builder)
    {
        if (!builder.ExecutionContext.IsPublishMode)
        {
            return agent;
        }

        return agent
            .WithEnvironment("Beacon__Redaction__HmacKey", builder.AddParameter("redaction-hmac-key", secret: true))
            .WithEnvironment("Authentication__Schemes__Bearer__Authority", builder.AddParameter("agent-token-authority", secret: true))
            .WithEnvironment("Authentication__Schemes__Bearer__ValidAudiences__0", builder.AddParameter("agent-token-audience", secret: true));
    }

    /// <summary>
    /// When the AppHost publishes: the Desk signs dispatchers in with Microsoft Entra ID and asks for the agent's
    /// scope on their behalf. Its client secret is a secret parameter too.
    /// </summary>
    public static IResourceBuilder<ProjectResource> WithDeskSignIn(
        this IResourceBuilder<ProjectResource> desk, IDistributedApplicationBuilder builder)
    {
        if (!builder.ExecutionContext.IsPublishMode)
        {
            return desk;
        }

        return desk
            .WithEnvironment("AzureAd__Instance", "https://login.microsoftonline.com/")
            .WithEnvironment("AzureAd__TenantId", builder.AddParameter("entra-tenant-id"))
            .WithEnvironment("AzureAd__ClientId", builder.AddParameter("desk-client-id"))
            .WithEnvironment("AzureAd__ClientSecret", builder.AddParameter("desk-client-secret", secret: true))
            .WithEnvironment("BeaconAgent__Scopes__0", builder.AddParameter("agent-scope"));
    }
}

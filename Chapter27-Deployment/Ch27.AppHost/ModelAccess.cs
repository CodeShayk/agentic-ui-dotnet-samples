using Azure.Provisioning.CognitiveServices;

/// <summary>How the deployed agent reaches its model.</summary>
internal static class ModelAccess
{
    /// <summary>
    /// Under <c>aspire run</c> the agent reads its own configuration (appsettings.json and User
    /// Secrets, Chapter 5), so nothing changes. When the AppHost publishes or deploys, the container
    /// has no User Secrets: the model's settings become deployment parameters, and the provider,
    /// chosen with <c>Deploy:ModelProvider</c>, decides what gets provisioned.
    /// </summary>
    public static IResourceBuilder<ProjectResource> WithDeployedModel(
        this IResourceBuilder<ProjectResource> agent, IDistributedApplicationBuilder builder)
    {
        if (!builder.ExecutionContext.IsPublishMode)
        {
            return agent;
        }

        switch (builder.Configuration["Deploy:ModelProvider"] ?? "AzureOpenAI")
        {
            case "AzureOpenAI":
                // An existing Azure OpenAI account. The agent's managed identity gets Cognitive Services
                // OpenAI User on it, so there is no key to store, rotate or leak.
                var openai = builder.AddAzureOpenAI("openai")
                    .AsExisting(builder.AddParameter("openai-account"), builder.AddParameter("openai-resource-group"));

                return agent
                    .WithRoleAssignments(openai, CognitiveServicesBuiltInRole.CognitiveServicesOpenAIUser)
                    .WithEnvironment("AI__Provider", "AzureOpenAI")
                    .WithEnvironment("AI__Endpoint", openai.Resource.UriExpression)
                    .WithEnvironment("AI__Model", builder.AddParameter("openai-deployment"));
                // Aspire also sets AZURE_CLIENT_ID (which identity) and AZURE_TOKEN_CREDENTIALS=
                // ManagedIdentityCredential (no probing for developer sign-ins) on the container.

            case "OpenAI":
                // A key is unavoidable here. As a secret parameter it becomes a Container Apps secret,
                // referenced by the environment variable, never written into the image or the template.
                return agent
                    .WithEnvironment("AI__Provider", "OpenAI")
                    .WithEnvironment("AI__Model", builder.AddParameter("openai-model"))
                    .WithEnvironment("AI__ApiKey", builder.AddParameter("openai-api-key", secret: true));

            default:
                // Beacon's scripted model: a deployment you can smoke-test without a model bill.
                return agent.WithEnvironment("AI__Provider", "Scripted");
        }
    }
}

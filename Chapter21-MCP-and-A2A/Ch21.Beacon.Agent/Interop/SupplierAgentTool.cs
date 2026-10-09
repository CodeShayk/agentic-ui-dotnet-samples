using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using A2A;
using Beacon.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Ch21.Beacon.Agent.Interop;

/// <summary>What Beacon asks Northwind's agent, as an A2A data part.</summary>
public sealed record SupplierStockRequest(string Sku, int Quantity);

/// <summary>
/// The fields Beacon reads from Northwind's "availability" artifact. Anything else the supplier
/// sends, such as its prices or a free-text note, is never deserialized.
/// </summary>
public sealed record SupplierQuote(string? Sku, bool Carried, int QuantityAvailable, int? LeadTimeBusinessDays, string? ShipsFrom);

/// <summary>
/// A request Beacon refused before contacting Northwind: the model's arguments were at fault, not the
/// supplier. <see cref="SubagentAttributionChatClient"/> reports its <see cref="Code"/> on <c>SUBAGENT_ERROR</c>.
/// </summary>
public sealed record RefusedRequest(string Error)
{
    public string Code => "invalid_request";
}

/// <summary>What the model, and therefore the AG-UI stream, sees of the supplier's answer.</summary>
public sealed record SupplierAvailability(
    string Source,
    string Sku,
    int QuantityRequested,
    bool Carried,
    int QuantityAvailable,
    bool CanSupply,
    int? LeadTimeBusinessDays,
    string? ShipsFrom);

/// <summary>
/// Northwind Supply's A2A agent, exposed to Beacon's model as one narrow tool. The remote agent
/// stays opaque: Beacon sends a typed request, keeps typed fields from the completed task and
/// turns every failure into a short <see cref="ToolError"/>.
/// </summary>
public sealed partial class SupplierAgentTool(
    IHttpClientFactory httpClientFactory,
    IOptions<SupplierOptions> options,
    ILogger<SupplierAgentTool> logger)
{
    public const string Name = "check_supplier_stock";
    public const string Source = "Northwind Supply (A2A)";

    public AIFunction AsAIFunction() => AIFunctionFactory.Create(
        CheckSupplierStockAsync,
        Name,
        "Asks Northwind Supply's agent whether it can ship a part and how many business days delivery takes. " +
        "Use it when Contoso Climate's own stock is short.",
        InteropJson.Options);

    public async Task<object> CheckSupplierStockAsync(
        [Description("The part SKU, for example EXP-TANK-4.")] string sku,
        [Description("How many are needed, from 1 to 10.")] int quantity = 1,
        CancellationToken cancellationToken = default)
    {
        sku = sku.Trim().ToUpperInvariant();
        if (!SkuFormat().IsMatch(sku) || quantity is < 1 or > 10)
        {
            return new RefusedRequest("Ask about one SKU, such as EXP-TANK-4, and a quantity from 1 to 10.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.TimeoutSeconds));

        SendMessageResponse response;
        try
        {
            var client = new A2AClient(options.Value.Endpoint, httpClientFactory.CreateClient(InteropHttpClients.Supplier));
            response = await client.SendMessageAsync(
                new SendMessageRequest
                {
                    Message = new Message
                    {
                        Role = Role.User,
                        MessageId = Guid.NewGuid().ToString("N"),
                        Parts = [Part.FromData(JsonSerializer.SerializeToElement(
                            new SupplierStockRequest(sku, quantity), InteropJsonContext.Default.SupplierStockRequest))],
                    },
                    Configuration = new SendMessageConfiguration { AcceptedOutputModes = ["application/json"] },
                },
                timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            LogFailed(logger, ex, sku);
            return new ToolError("Northwind Supply's agent didn't answer, so the supplier's stock is unknown.");
        }

        return Read(response, sku, quantity);
    }

    private object Read(SendMessageResponse response, string sku, int quantity)
    {
        IEnumerable<Part> parts;
        if (response.Task is { } task)
        {
            // The task ID and context ID are Northwind's bookkeeping. They go to the log, not the model.
            LogTask(logger, task.Id, task.Status?.State);
            if (task.Status?.State != TaskState.Completed)
            {
                return new ToolError("Northwind Supply's agent couldn't answer that request.");
            }

            parts = (task.Artifacts ?? []).Where(a => a.Name == "availability").SelectMany(a => a.Parts ?? []);
        }
        else
        {
            parts = response.Message?.Parts ?? [];
        }

        SupplierQuote? quote = null;
        if (parts.FirstOrDefault(p => p.ContentCase == PartContentCase.Data)?.Data is { } data)
        {
            try
            {
                quote = data.Deserialize(InteropJsonContext.Default.SupplierQuote);
            }
            catch (JsonException)
            {
            }
        }

        // Typed, range-checked fields only. A quote for another SKU is as bad as no quote.
        if (quote is null
            || !string.Equals(quote.Sku, sku, StringComparison.OrdinalIgnoreCase)
            || quote.QuantityAvailable is < 0 or > 100_000
            || quote.LeadTimeBusinessDays is < 0 or > 365)
        {
            return new ToolError("Northwind Supply's agent answered in a form Beacon doesn't accept.");
        }

        return new SupplierAvailability(
            Source,
            sku,
            quantity,
            quote.Carried,
            quote.QuantityAvailable,
            CanSupply: quote.Carried && quote.QuantityAvailable >= quantity,
            quote.LeadTimeBusinessDays,
            ExternalContent.Place(quote.ShipsFrom)); // the one free-text field: "Albany, NY" or nothing
    }

    [GeneratedRegex(@"^[A-Z]{3}-[A-Z0-9]+(?:-[A-Z0-9]+)?$")]
    private static partial Regex SkuFormat();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Northwind Supply's agent failed for {Sku}.")]
    private static partial void LogFailed(ILogger logger, Exception exception, string sku);

    [LoggerMessage(Level = LogLevel.Information, Message = "Northwind Supply task {TaskId} ended in state {State}.")]
    private static partial void LogTask(ILogger logger, string? taskId, TaskState? state);
}

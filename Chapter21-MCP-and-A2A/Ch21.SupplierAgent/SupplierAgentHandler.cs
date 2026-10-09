using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using A2A;

namespace Ch21.SupplierAgent;

/// <summary>
/// Northwind's agent logic. Each request becomes an A2A task: submitted, working, an "availability"
/// data artifact, then completed (or rejected when the request names no SKU). The SDK's
/// <see cref="A2AServer"/> handles JSON-RPC, task storage and the lifecycle around this class.
/// </summary>
public sealed partial class SupplierAgentHandler(ILogger<SupplierAgentHandler> logger) : IAgentHandler
{
    public async Task ExecuteAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
    {
        var task = new TaskUpdater(eventQueue, context.TaskId, context.ContextId);
        await task.SubmitAsync(cancellationToken);
        await task.StartWorkAsync(cancellationToken: cancellationToken);

        if (ReadRequest(context.Message) is not { } request)
        {
            await task.RejectAsync(Reply(context, "Send a part SKU, for example {\"sku\":\"CAP-45-5\",\"quantity\":1}."), cancellationToken);
            return;
        }

        var quote = SupplierCatalog.Quote(request);
        LogQuote(logger, quote.Sku, request.Quantity, quote.QuantityAvailable);

        await task.AddArtifactAsync(
            [Part.FromData(JsonSerializer.SerializeToElement(quote, SupplierJsonContext.Default.StockQuote))],
            name: "availability",
            cancellationToken: cancellationToken);

        var summary = quote.Carried
            ? string.Create(CultureInfo.InvariantCulture,
                $"{quote.QuantityAvailable} x {quote.Sku} available, ships from {quote.ShipsFrom} in {quote.LeadTimeBusinessDays} business day(s). {quote.Note}")
            : $"Northwind doesn't carry {quote.Sku}.";

        await task.CompleteAsync(Reply(context, summary.Trim()), cancellationToken);
    }

    public Task CancelAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken) =>
        new TaskUpdater(eventQueue, context.TaskId, context.ContextId).CancelAsync(cancellationToken).AsTask();

    // A structured data part is the contract; plain text ("2 x CAP-45-5") is accepted for humans testing with curl.
    private static StockRequest? ReadRequest(Message? message)
    {
        foreach (var part in message?.Parts ?? [])
        {
            if (part.ContentCase == PartContentCase.Data && part.Data is { } data)
            {
                try
                {
                    if (data.Deserialize(SupplierJsonContext.Default.StockRequest) is { Sku.Length: > 0 } request)
                    {
                        return request with { Quantity = Math.Clamp(request.Quantity, 1, 100) };
                    }
                }
                catch (JsonException)
                {
                    return null;
                }
            }

            if (part.ContentCase == PartContentCase.Text && SkuText().Match(part.Text ?? "") is { Success: true } match)
            {
                var quantity = int.TryParse(match.Groups["qty"].Value, out var q) ? q : 1;
                return new StockRequest(match.Groups["sku"].Value, Math.Clamp(quantity, 1, 100));
            }
        }

        return null;
    }

    private static Message Reply(RequestContext context, string text) => new()
    {
        Role = Role.Agent,
        MessageId = Guid.NewGuid().ToString("N"),
        ContextId = context.ContextId,
        TaskId = context.TaskId,
        Parts = [Part.FromText(text)],
    };

    [GeneratedRegex(@"(?:(?<qty>\d+)\s*x\s*)?(?<sku>\b[A-Z]{3}-[A-Z0-9]+(?:-[A-Z0-9]+)?\b)", RegexOptions.IgnoreCase)]
    private static partial Regex SkuText();

    [LoggerMessage(Level = LogLevel.Information, Message = "Quoted {Sku} x {Quantity}: {Available} available.")]
    private static partial void LogQuote(ILogger logger, string sku, int quantity, int available);
}

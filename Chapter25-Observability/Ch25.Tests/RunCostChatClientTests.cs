using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Ch25.Beacon.Agent.Telemetry;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ch25.Tests;

/// <summary>RunCostChatClient on its own: what a run that never finished records.</summary>
public sealed class RunCostChatClientTests
{
    [Fact]
    public async Task A_canceled_run_records_what_it_spent_as_canceled()
    {
        using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meters = services.GetRequiredService<IMeterFactory>();

        List<(double Cost, object? Status)> recorded = [];
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (ReferenceEquals(instrument.Meter.Scope, meters) && instrument.Name == "beacon.run.cost")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>((_, cost, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "beacon.run.status")
                {
                    recorded.Add((cost, tag.Value));
                }
            }
        });
        listener.Start();

        var pricing = new PricingOptions
        {
            Models = [new ModelPrice { Model = "beacon-scripted", InputPerMillionTokens = 0.40m, OutputPerMillionTokens = 1.60m }],
        };
        using var stop = new CancellationTokenSource();
        using IChatClient client = new RunCostChatClient(
            new OneCallThenWait(), pricing, new RunCostMetrics(meters), NullLogger<RunCostChatClient>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Show me the fault history for EQ-501.")], cancellationToken: stop.Token))
            {
                await stop.CancelAsync(); // Stop, after the first model call reported its usage
            }
        });

        // The SDK's agui.run.outcome spells it the same way.
        var (cost, status) = Assert.Single(recorded);
        Assert.Equal("canceled", status);
        Assert.Equal((1000 * 0.40 + 100 * 1.60) / 1_000_000, cost, precision: 12);
    }

    // One model call's usage, then a wait that only cancellation ends.
    private sealed class OneCallThenWait : IChatClient
    {
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(new UsageDetails { InputTokenCount = 1000, OutputTokenCount = 100 })])
            {
                ModelId = "beacon-scripted",
            };

            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

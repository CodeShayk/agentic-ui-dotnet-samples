using Ch26.Tests.Integration.Infrastructure;

namespace Ch26.Tests.Integration.Snapshots;

/// <summary>
/// Section 26.3: golden files. Each scenario's whole stream, normalized, is compared with a reviewed copy
/// in this folder, next to this file, so any change to what a client receives (an event added, moved or reshaped) shows up
/// as a diff in review, whether or not a specific assertion covers it.
/// </summary>
public sealed class EventStreamSnapshotTests
{
    [Theory]
    [MemberData(nameof(Scenarios.All), MemberType = typeof(Scenarios))]
    public async Task The_stream_matches_its_approved_snapshot(string scenario) =>
        StreamSnapshot.Verify(await Scenarios.RunAsync(scenario), scenario);
}

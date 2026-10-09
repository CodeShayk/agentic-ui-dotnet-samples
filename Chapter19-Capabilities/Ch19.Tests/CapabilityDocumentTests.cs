using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Beacon.AI;
using Beacon.Domain;
using Ch19.Beacon.Adaptive.Capabilities;
using Ch19.Beacon.Agent.Capabilities;
using Ch19.Tests.Infrastructure;

namespace Ch19.Tests;

/// <summary>The agent's capabilities document, as a client fetches it, in both profiles.</summary>
public sealed class CapabilityDocumentTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(CapabilityProfile.Full)]
    [InlineData(CapabilityProfile.Reduced)]
    public async Task The_document_validates_with_nothing_unrecognized(CapabilityProfile profile)
    {
        await using var host = new AgentHost(profile);

        using var response = await host.CreateClient().GetAsync("/agent/capabilities", Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoCache);
        Assert.NotNull(response.Headers.ETag);
        var read = CapabilityReader.Read(await response.Content.ReadAsByteArrayAsync(Cancel));
        Assert.True(read.IsUsable);
        Assert.Empty(read.Problems);
        Assert.Empty(read.Unrecognized);
    }

    [Fact]
    public async Task The_full_profile_declares_what_it_accepts_and_lists_only_its_own_tools()
    {
        await using var host = new AgentHost(CapabilityProfile.Full);
        using var document = await GetAsync(host);
        var root = document.RootElement;

        Assert.Equal("full", root.GetProperty("identity").GetProperty("metadata").GetProperty("profile").GetString());
        Assert.Equal(BeaconDeployment.AgentVersion, root.GetProperty("identity").GetProperty("version").GetString());
        Assert.True(root.GetProperty("tools").GetProperty("clientProvided").GetBoolean());
        string[] ownTools =
        [
            BeaconTools.GetWorkOrderName, BeaconTools.ListWorkOrdersName, BeaconTools.SearchPartsName,
            BeaconTools.CheckWarrantyName, BeaconTools.GetFaultHistoryName, BeaconTools.GetScheduleName,
        ];
        Assert.Equal(
            ownTools.Order(StringComparer.Ordinal),
            root.GetProperty("tools").GetProperty("items").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).Order(StringComparer.Ordinal));
        Assert.True(root.GetProperty("reasoning").GetProperty("supported").GetBoolean());
        var input = root.GetProperty("multimodal").GetProperty("input");
        Assert.True(input.GetProperty("image").GetBoolean());
        Assert.True(input.GetProperty("audio").GetBoolean());
        Assert.True(input.GetProperty("pdf").GetBoolean());
        Assert.Equal(8, root.GetProperty("execution").GetProperty("maxIterations").GetInt32());
        var limits = root.GetProperty("custom").GetProperty(AgentProfile.AttachmentLimitsKey);
        Assert.Equal((5L * 1024 * 1024, 8L * 1024 * 1024, 10L * 1024 * 1024),
            (limits.GetProperty("image").GetInt64(), limits.GetProperty("audio").GetInt64(), limits.GetProperty("pdf").GetInt64()));
    }

    [Fact]
    public async Task The_reduced_profile_says_no_explicitly_rather_than_leaving_things_out()
    {
        await using var host = new AgentHost(CapabilityProfile.Reduced);
        using var document = await GetAsync(host);
        var root = document.RootElement;

        // false is a statement; absence would only mean "unknown" to a client.
        Assert.Equal(JsonValueKind.False, root.GetProperty("tools").GetProperty("clientProvided").ValueKind);
        Assert.Equal(JsonValueKind.False, root.GetProperty("reasoning").GetProperty("supported").ValueKind);
        foreach (var kind in new[] { "image", "audio", "video", "pdf", "file" })
        {
            Assert.Equal(JsonValueKind.False, root.GetProperty("multimodal").GetProperty("input").GetProperty(kind).ValueKind);
        }

        var profile = AgentProfile.From(CapabilityReader.Read(document.RootElement.GetRawText()).Capabilities);
        Assert.Equal((Support.Unsupported, Support.Unsupported, Support.Unsupported, Support.Unsupported),
            (profile.ImageInput, profile.AudioInput, profile.Reasoning, profile.ClientTools));
    }

    [Theory]
    [InlineData(CapabilityProfile.Full)]
    [InlineData(CapabilityProfile.Reduced)]
    public async Task Groups_the_agent_has_nothing_to_say_about_are_absent_never_null(CapabilityProfile profile)
    {
        await using var host = new AgentHost(profile);
        using var document = await GetAsync(host);

        foreach (var group in new[] { "state", "output", "multiAgent" })
        {
            Assert.False(document.RootElement.TryGetProperty(group, out _), group);
        }

        // No protocol field is written as null. (The tools' parameter schemas are JSON Schema documents
        // that legitimately say "type": ["string", "null"], so they are left out of this check.)
        foreach (var property in document.RootElement.EnumerateObject().Where(p => p.Name != "tools"))
        {
            Assert.DoesNotContain("null", property.Value.GetRawText(), StringComparison.Ordinal);
        }

        Assert.All(document.RootElement.GetProperty("tools").EnumerateObject(), p => Assert.NotEqual(JsonValueKind.Null, p.Value.ValueKind));
    }

    [Fact]
    public async Task A_client_can_revalidate_its_copy_and_only_GET_is_allowed()
    {
        await using var host = new AgentHost();
        var http = host.CreateClient();
        using var first = await http.GetAsync("/agent/capabilities", Cancel);

        using var revalidate = new HttpRequestMessage(HttpMethod.Get, "/agent/capabilities");
        revalidate.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        using var notModified = await http.SendAsync(revalidate, Cancel);
        using var post = await http.PostAsync("/agent/capabilities", new StringContent("{}", MediaTypeHeaderValue.Parse("application/json")), Cancel);

        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
    }

    [Fact]
    public async Task The_two_profiles_are_different_documents()
    {
        await using var full = new AgentHost(CapabilityProfile.Full);
        await using var reduced = new AgentHost(CapabilityProfile.Reduced);

        using var a = await full.CreateClient().GetAsync("/agent/capabilities", Cancel);
        using var b = await reduced.CreateClient().GetAsync("/agent/capabilities", Cancel);

        Assert.NotEqual(a.Headers.ETag, b.Headers.ETag);
        Assert.NotEmpty(SeedData.WorkOrders); // the agents share Beacon's data; only the declaration differs
    }

    private static async Task<JsonDocument> GetAsync(AgentHost host) =>
        JsonDocument.Parse(await host.CreateClient().GetStringAsync("/agent/capabilities", Cancel));
}

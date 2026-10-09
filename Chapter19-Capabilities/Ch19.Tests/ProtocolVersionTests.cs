using Ch19.Beacon.Adaptive.Versioning;

namespace Ch19.Tests;

/// <summary>Reading and comparing AG-UI protocol version declarations (MAJOR.MINOR, numeric).</summary>
public sealed class ProtocolVersionTests
{
    [Theory]
    [InlineData("1.0", VersionVerdict.Same)]
    [InlineData(null, VersionVerdict.Absent)]
    [InlineData("1.1", VersionVerdict.NewerMinor)]
    [InlineData("1.10", VersionVerdict.NewerMinor)]
    [InlineData("2.0", VersionVerdict.NewerMajor)]
    [InlineData("0.9", VersionVerdict.OlderMajor)]
    [InlineData("1", VersionVerdict.Uninterpretable)]
    [InlineData("1.0.0", VersionVerdict.Uninterpretable)]
    [InlineData("v1.0", VersionVerdict.Uninterpretable)]
    [InlineData(" 1.0", VersionVerdict.Uninterpretable)]
    [InlineData("", VersionVerdict.Uninterpretable)]
    [InlineData("99999999999.0", VersionVerdict.Uninterpretable)]
    public void A_declaration_is_judged_against_1_0(string? declared, VersionVerdict expected) =>
        Assert.Equal(expected, ProtocolVersions.Judge(declared));

    [Fact]
    public void Versions_compare_numerically_component_by_component()
    {
        Assert.True(ProtocolVersion.Parse("1.10").CompareTo(ProtocolVersion.Parse("1.9")) > 0);
        Assert.Equal(VersionVerdict.OlderMinor, ProtocolVersions.Judge("1.0", new ProtocolVersion(1, 2)));
        Assert.Equal("1.0", ProtocolVersion.Current.ToString());
    }

    [Theory]
    [InlineData("1.0", false)]
    [InlineData(null, false)]
    [InlineData("1.3", true)]
    [InlineData("2.0", true)]
    [InlineData("latest", true)]
    public void A_client_warns_only_when_the_producer_may_be_sending_what_it_skips(string? declared, bool warns)
    {
        var check = ProducerVersionCheck.From(declared);

        Assert.Equal(warns, check.IsWarning);
        Assert.False(string.IsNullOrWhiteSpace(check.Summary));
    }
}

using System.Globalization;
using AGUI.Abstractions;

namespace Ch19.Beacon.Adaptive.Versioning;

/// <summary>
/// An AG-UI protocol version identifier: <c>MAJOR.MINOR</c>, compared numerically component by
/// component, so <c>1.10</c> is newer than <c>1.9</c>.
/// </summary>
public readonly record struct ProtocolVersion(int Major, int Minor) : IComparable<ProtocolVersion>
{
    /// <summary>The version this code speaks: the one the SDK's protocol types were generated from.</summary>
    public static ProtocolVersion Current { get; } = Parse(AGUIProtocol.Version);

    /// <summary>
    /// Reads a declaration. Only the published grammar counts: exactly two runs of digits around one
    /// dot. "1", "1.0.0" and "v1" are not versions this specification publishes.
    /// </summary>
    public static bool TryParse(string? value, out ProtocolVersion version)
    {
        version = default;
        var dot = value?.IndexOf('.') ?? -1;
        if (value is null || dot <= 0 || dot == value.Length - 1
            || value.AsSpan(0, dot).ContainsAnyExceptInRange('0', '9')
            || value.AsSpan(dot + 1).ContainsAnyExceptInRange('0', '9')
            || !int.TryParse(value.AsSpan(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(value.AsSpan(dot + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
        {
            return false;
        }

        version = new ProtocolVersion(major, minor);
        return true;
    }

    public static ProtocolVersion Parse(string value) =>
        TryParse(value, out var version) ? version : throw new FormatException($"'{value}' is not a MAJOR.MINOR protocol version.");

    public int CompareTo(ProtocolVersion other) =>
        Major != other.Major ? Major.CompareTo(other.Major) : Minor.CompareTo(other.Minor);

    public override string ToString() => $"{Major}.{Minor}";
}

/// <summary>How a peer's declaration compares with the version this side speaks.</summary>
public enum VersionVerdict
{
    /// <summary>No declaration: a peer from before the protocol carried a version.</summary>
    Absent,

    /// <summary>The same <c>MAJOR.MINOR</c>.</summary>
    Same,

    /// <summary>An older minor of the same major line.</summary>
    OlderMinor,

    /// <summary>A newer minor of the same major line: additions only, so it is serveable by construction.</summary>
    NewerMinor,

    /// <summary>A newer major line.</summary>
    NewerMajor,

    /// <summary>An older major line.</summary>
    OlderMajor,

    /// <summary>Not a <c>MAJOR.MINOR</c> identifier. The rules say to handle it like a newer one.</summary>
    Uninterpretable,
}

public static class ProtocolVersions
{
    /// <summary>Judges a peer's declaration (<c>RunAgentInput.protocolVersion</c> or <c>RUN_STARTED.protocolVersion</c>).</summary>
    public static VersionVerdict Judge(string? declared, ProtocolVersion ours)
    {
        if (declared is null)
        {
            return VersionVerdict.Absent;
        }

        if (!ProtocolVersion.TryParse(declared, out var theirs))
        {
            return VersionVerdict.Uninterpretable;
        }

        return theirs.Major.CompareTo(ours.Major) switch
        {
            > 0 => VersionVerdict.NewerMajor,
            < 0 => VersionVerdict.OlderMajor,
            _ => theirs.Minor.CompareTo(ours.Minor) switch
            {
                > 0 => VersionVerdict.NewerMinor,
                < 0 => VersionVerdict.OlderMinor,
                _ => VersionVerdict.Same,
            },
        };
    }

    public static VersionVerdict Judge(string? declared) => Judge(declared, ProtocolVersion.Current);
}

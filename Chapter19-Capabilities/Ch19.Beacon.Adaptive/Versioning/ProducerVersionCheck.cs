namespace Ch19.Beacon.Adaptive.Versioning;

/// <summary>
/// A client's reading of the producer's <c>RUN_STARTED.protocolVersion</c>. A consumer never refuses a
/// stream over the version: it proceeds under the processing model and warns when the producer is
/// newer or unreadable, because that is when it may be skipping material.
/// </summary>
/// <param name="Verdict">How the declaration compares with this client's version.</param>
/// <param name="Declared">The producer's declaration, as sent.</param>
/// <param name="Summary">One sentence for a diagnostics panel.</param>
/// <param name="IsWarning">True when the developer should hear about it.</param>
public sealed record ProducerVersionCheck(VersionVerdict Verdict, string? Declared, string Summary, bool IsWarning)
{
    public static ProducerVersionCheck From(string? declared)
    {
        var ours = ProtocolVersion.Current;
        var verdict = ProtocolVersions.Judge(declared, ours);
        return verdict switch
        {
            VersionVerdict.Same => new(verdict, declared, $"Speaks AG-UI {declared}, the same version as this client.", false),
            VersionVerdict.OlderMinor => new(verdict, declared, $"Speaks AG-UI {declared}, an older {ours.Major}.x; everything it sends is understood.", false),
            VersionVerdict.NewerMinor => new(verdict, declared,
                $"Speaks AG-UI {declared}, newer than this client's {ours}. Anything this client doesn't recognize is skipped.", true),
            VersionVerdict.NewerMajor => new(verdict, declared,
                $"Speaks AG-UI {declared}, a newer major version than this client's {ours}. This client carries on and skips what it doesn't recognize.", true),
            VersionVerdict.OlderMajor => new(verdict, declared, $"Declares AG-UI {declared}, a line older than {ours.Major}.x.", true),
            VersionVerdict.Uninterpretable => new(verdict, declared, $"Declared \"{declared}\", which isn't a protocol version; treated as newer.", true),
            _ => new(verdict, declared,
                "Didn't declare a protocol version, so it's treated as older than 1.0. (AGUI.Server 1.0.0 leaves it out unless the host adds it.)", false),
        };
    }
}

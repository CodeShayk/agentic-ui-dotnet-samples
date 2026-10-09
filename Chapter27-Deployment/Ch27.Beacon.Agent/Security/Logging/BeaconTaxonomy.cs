// Copied from Chapter 23 (Ch23.Beacon.Security).
using Microsoft.Extensions.Compliance.Classification;

namespace Ch27.Beacon.Agent.Security.Logging;

/// <summary>
/// The kinds of sensitive data Beacon logs. A log parameter carries one of these attributes, and the
/// redaction configuration decides what each kind becomes in the output.
/// </summary>
public static class BeaconTaxonomy
{
    public const string Name = "Beacon";

    /// <summary>Identifies a person: user IDs, names, technician IDs.</summary>
    public static DataClassification PersonalData { get; } = new(Name, nameof(PersonalData));

    /// <summary>What people and models wrote: messages, tool arguments and tool results.</summary>
    public static DataClassification ConversationContent { get; } = new(Name, nameof(ConversationContent));
}

/// <summary>Marks a value that identifies a person. Beacon's logs replace it with a keyed hash.</summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property | AttributeTargets.Field)]
public sealed class PersonalDataAttribute() : DataClassificationAttribute(BeaconTaxonomy.PersonalData);

/// <summary>Marks conversation content. Beacon's logs erase it.</summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property | AttributeTargets.Field)]
public sealed class ConversationContentAttribute() : DataClassificationAttribute(BeaconTaxonomy.ConversationContent);

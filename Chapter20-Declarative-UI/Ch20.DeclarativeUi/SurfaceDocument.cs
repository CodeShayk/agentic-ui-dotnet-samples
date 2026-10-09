using System.Text.Json.Nodes;

namespace Ch20.DeclarativeUi;

/// <summary>What a validator decided about a surface.</summary>
public enum SurfaceOutcome
{
    /// <summary>Everything in it is known and allowed.</summary>
    Accepted,

    /// <summary>It is safe, but some of it can't be drawn here; those parts become placeholders.</summary>
    Degraded,

    /// <summary>It is malformed, too large or unsafe. Nothing of it is drawn.</summary>
    Rejected,
}

/// <summary>
/// One problem, located by a JSON Pointer into the operations array, in the shape of A2UI's
/// <c>VALIDATION_FAILED</c> error (code, path, one-sentence message), so a model can be told exactly what to fix.
/// </summary>
public sealed record SurfaceProblem(string Code, string Path, string Message, bool IsError);

/// <summary>One component of a validated surface. Unknown components keep only what a placeholder needs.</summary>
public sealed record ComponentNode(string Id, string Type, JsonObject Properties, bool IsKnown);

/// <summary>A surface that passed validation: its components by ID and its initial data model.</summary>
public sealed record SurfaceDocument(
    string SurfaceId,
    string CatalogId,
    IReadOnlyDictionary<string, ComponentNode> Components,
    JsonObject Data);

/// <summary>The outcome, every problem found, and the surface unless it was rejected.</summary>
public sealed record SurfaceVerdict(SurfaceOutcome Outcome, IReadOnlyList<SurfaceProblem> Problems, SurfaceDocument? Surface)
{
    public IEnumerable<SurfaceProblem> Errors => Problems.Where(p => p.IsError);

    public IEnumerable<SurfaceProblem> Warnings => Problems.Where(p => !p.IsError);
}

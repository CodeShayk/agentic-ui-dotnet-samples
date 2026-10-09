namespace Ch20.DeclarativeUi;

/// <summary>Which end of the wire is validating, and so what a problem costs.</summary>
public enum ValidationMode
{
    /// <summary>
    /// The agent, before it sends: every problem is an error, because the model that wrote the surface can
    /// still fix it. Unknown components and properties are rejected and every absolute binding must resolve.
    /// </summary>
    Producer,

    /// <summary>
    /// A client, after it receives: a component it can't draw, or a property it doesn't know, degrades to a
    /// placeholder, because the producer can't be asked again. Everything that could hurt is still an error.
    /// </summary>
    Consumer,
}

/// <summary>Hard limits on a surface. Checked before anything is rendered or stored.</summary>
public sealed record SurfaceLimits
{
    /// <summary>The largest operations array, serialized, in UTF-8 bytes.</summary>
    public int MaxBytes { get; init; } = 64 * 1024;

    public int MaxOperations { get; init; } = 8;

    public int MaxComponents { get; init; } = 120;

    /// <summary>The longest path from <c>root</c> to a leaf, counting a template's item as one level.</summary>
    public int MaxDepth { get; init; } = 10;

    public int MaxChildren { get; init; } = 40;

    public int MaxStringLength { get; init; } = 500;

    public int MaxOptions { get; init; } = 20;

    public int MaxChecks { get; init; } = 6;

    public int MaxContextEntries { get; init; } = 12;

    /// <summary>The most nodes a renderer draws once templates are expanded over their data.</summary>
    public int MaxRenderedNodes { get; init; } = 400;
}

/// <summary>Where images and links may point. Only absolute https URLs on an allowed origin pass.</summary>
public sealed class UrlPolicy(IEnumerable<string> allowedOrigins)
{
    private readonly HashSet<string> _origins = new(allowedOrigins.Select(Normalize), StringComparer.OrdinalIgnoreCase);

    /// <summary>A policy that allows no URL at all.</summary>
    public static UrlPolicy None { get; } = new([]);

    public bool IsAllowed(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(uri.UserInfo)
        && _origins.Contains(uri.GetLeftPart(UriPartial.Authority));

    private static string Normalize(string origin) =>
        new Uri(origin, UriKind.Absolute).GetLeftPart(UriPartial.Authority);
}

/// <summary>Everything a validator needs to judge a surface.</summary>
public sealed record SurfacePolicy
{
    public required ComponentCatalog Catalog { get; init; }

    public ValidationMode Mode { get; init; } = ValidationMode.Producer;

    /// <summary>
    /// The components this renderer can draw, when it's fewer than the catalog: an older client meeting a
    /// newer catalog. <see langword="null"/> means all of them.
    /// </summary>
    public IReadOnlySet<string>? Renderable { get; init; }

    public SurfaceLimits Limits { get; init; } = new();

    public UrlPolicy Urls { get; init; } = UrlPolicy.None;

    /// <summary>The policy Beacon's agent applies to what its model writes.</summary>
    public static SurfacePolicy Producer(ComponentCatalog catalog, UrlPolicy urls) =>
        new() { Catalog = catalog, Mode = ValidationMode.Producer, Urls = urls };

    /// <summary>The policy a client applies to what arrives.</summary>
    public static SurfacePolicy Consumer(ComponentCatalog catalog, UrlPolicy urls, IReadOnlySet<string>? renderable = null) =>
        new() { Catalog = catalog, Mode = ValidationMode.Consumer, Urls = urls, Renderable = renderable };
}

/// <summary>Beacon's origins for images and links: the manual library's host and nothing else.</summary>
public static class BeaconUrls
{
    public static UrlPolicy Policy { get; } = new(["https://manuals.contoso-climate.example"]);
}

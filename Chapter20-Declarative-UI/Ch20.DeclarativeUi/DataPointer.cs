namespace Ch20.DeclarativeUi;

/// <summary>
/// A data binding path: a JSON Pointer (RFC 6901) when it starts with <c>/</c>, or, as A2UI extends it, a
/// relative path resolved against the item a template is repeating over.
/// </summary>
public sealed record DataPointer(IReadOnlyList<string> Segments, bool IsAbsolute)
{
    public const int MaxLength = 200;

    public static DataPointer Root { get; } = new([], IsAbsolute: true);

    public static bool TryParse(string? path, out DataPointer pointer)
    {
        pointer = Root;
        if (string.IsNullOrEmpty(path) || path.Length > MaxLength)
        {
            return false;
        }

        if (path == "/")
        {
            return true;
        }

        var isAbsolute = path[0] == '/';
        var parts = (isAbsolute ? path[1..] : path).Split('/');
        List<string> segments = [];
        foreach (var part in parts)
        {
            if (part.Length == 0 || !TryUnescape(part, out var segment))
            {
                return false; // "/a//b", a trailing slash or a bad escape
            }

            segments.Add(segment);
        }

        pointer = new DataPointer(segments, isAbsolute);
        return true;
    }

    /// <summary>Resolves this path in a scope: absolute paths ignore it, relative ones are appended to it.</summary>
    public DataPointer In(DataPointer scope) => IsAbsolute ? this : new DataPointer([.. scope.Segments, .. Segments], IsAbsolute: true);

    public DataPointer Append(string segment) => this with { Segments = [.. Segments, segment] };

    public override string ToString() =>
        (IsAbsolute ? "/" : string.Empty) + string.Join('/', Segments.Select(s => s.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal)));

    private static bool TryUnescape(string part, out string segment)
    {
        segment = part;
        if (!part.Contains('~', StringComparison.Ordinal))
        {
            return true;
        }

        var chars = new System.Text.StringBuilder(part.Length);
        for (var i = 0; i < part.Length; i++)
        {
            if (part[i] != '~')
            {
                chars.Append(part[i]);
                continue;
            }

            if (i + 1 >= part.Length || part[i + 1] is not ('0' or '1'))
            {
                return false;
            }

            chars.Append(part[++i] == '0' ? '~' : '/');
        }

        segment = chars.ToString();
        return true;
    }
}

using System.Globalization;

namespace Ch17.Media;

/// <summary>Sizes as people read them: 812 bytes, 61 KB, 4.2 MB.</summary>
public static class ByteSize
{
    public const long KB = 1024;
    public const long MB = 1024 * KB;

    public static string Format(long bytes) => bytes switch
    {
        < KB => $"{bytes} bytes",
        < MB => string.Create(CultureInfo.InvariantCulture, $"{Math.Ceiling(bytes / (double)KB):0} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)MB:0.0} MB"),
    };
}

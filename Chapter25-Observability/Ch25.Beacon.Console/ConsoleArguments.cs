namespace Ch25.Beacon.Console;

/// <summary>
/// The console's command line: <c>[endpoint] [--ask "question"] [--capture folder] [--replay file.sse]
/// [--otlp endpoint] [--show-spans]</c>. Every setting is an argument, so the same command works in Bash
/// and in PowerShell.
/// </summary>
public sealed record ConsoleArguments(string Endpoint, string? Ask, string? Capture, string? Replay, string? Otlp, bool ShowSpans)
{
    public const string DefaultEndpoint = "http://localhost:5250/agent";

    private static readonly string[] ValueOptions = ["--ask", "--capture", "--replay", "--otlp"];

    /// <param name="args">The command-line arguments.</param>
    /// <param name="environment">Reads an environment variable; <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is the fallback for <c>--otlp</c>.</param>
    public static ConsoleArguments Parse(string[] args, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;

        // The agent's endpoint is the http argument that isn't an option's value.
        var endpoint = args
            .Where((arg, i) => arg.StartsWith("http", StringComparison.OrdinalIgnoreCase) && (i == 0 || !ValueOptions.Contains(args[i - 1])))
            .FirstOrDefault() ?? DefaultEndpoint;

        return new ConsoleArguments(
            endpoint,
            Ask: Option("--ask"),
            Capture: Option("--capture"),
            Replay: Option("--replay"),
            Otlp: Option("--otlp") ?? environment("OTEL_EXPORTER_OTLP_ENDPOINT"),
            ShowSpans: args.Contains("--show-spans"));

        string? Option(string name)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
    }
}

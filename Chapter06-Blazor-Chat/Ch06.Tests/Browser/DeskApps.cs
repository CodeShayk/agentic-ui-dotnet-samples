using System.Diagnostics;
using System.Text;

namespace Ch06.Tests.Browser;

/// <summary>
/// Starts Ch06.Beacon.Agent (ports 5060 and 5063) and Ch06.Beacon.Desk (port 5062) as real processes with
/// <c>dotnet run --no-build</c>, the way the chapter runs them, and stops both process trees afterwards.
/// Child processes inherit this process's environment, so <c>AI__ScriptedChunkDelayMilliseconds=5</c> reaches
/// the agent. Nothing starts unless RUN_E2E_TESTS=true.
/// </summary>
public sealed class DeskApps : IAsyncLifetime
{
    public const string DeskUrl = "http://localhost:5062/";
    public const string AgentUrl = "http://localhost:5060/";

    private readonly List<(Process Process, StringBuilder Output)> _processes = [];

    public static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable("RUN_E2E_TESTS"), "true", StringComparison.OrdinalIgnoreCase);

    public async ValueTask InitializeAsync()
    {
        if (!Enabled)
        {
            return;
        }

        var chapter = ChapterFolder();
        Start(Path.Combine(chapter, "Ch06.Beacon.Agent"));
        Start(Path.Combine(chapter, "Ch06.Beacon.Desk"));

        await WaitUntilServingAsync(AgentUrl);
        await WaitUntilServingAsync(DeskUrl);
    }

    public ValueTask DisposeAsync()
    {
        foreach (var (process, _) in _processes)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true); // dotnet run starts the app as a child process
                process.WaitForExit(TimeSpan.FromSeconds(10));
            }

            process.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private void Start(string projectDirectory)
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";
        var start = new ProcessStartInfo(dotnet)
        {
            ArgumentList = { "run", "--no-build", "--project", projectDirectory, "--launch-profile", "http" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

        var output = new StringBuilder();
        var process = Process.Start(start) ?? throw new InvalidOperationException($"Couldn't start {projectDirectory}.");
        process.OutputDataReceived += (_, e) => { lock (output) { output.AppendLine(e.Data); } };
        process.ErrorDataReceived += (_, e) => { lock (output) { output.AppendLine(e.Data); } };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _processes.Add((process, output));
    }

    private async Task WaitUntilServingAsync(string url)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            if (_processes.FirstOrDefault(p => p.Process.HasExited) is { Process: not null } exited)
            {
                throw new InvalidOperationException($"A Beacon app exited early (is the port in use?):{Environment.NewLine}{exited.Output}");
            }

            try
            {
                using var response = await http.GetAsync(url);
                return; // any HTTP answer means Kestrel is listening
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException) // HttpClient.Timeout: a cancellation, not an HttpRequestException
            {
            }

            await Task.Delay(500);
        }

        throw new TimeoutException($"{url} didn't answer within 90 seconds.");
    }

    // The test binaries live under Ch06.Tests/bin/...; the apps are its siblings.
    private static string ChapterFolder()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Ch06.Beacon.Agent")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Couldn't find the Chapter06-Blazor-Chat folder above the test binaries.");
    }
}

using System.Diagnostics;
using System.Text;

namespace Ch20.Tests.Browser;

/// <summary>
/// Starts Ch20.Beacon.Agent (port 5200) and Ch20.Beacon.Desk (port 5201) as real processes with
/// <c>dotnet run --no-build</c>, as Chapter 6's browser proof does, and stops both process trees afterwards.
/// The apps start without their launch profiles (the Desk's would open a browser window); the URLs and
/// environment are set here instead. Nothing starts unless RUN_E2E_TESTS=true.
/// </summary>
public sealed class DeskApps : IAsyncLifetime
{
    public const string AgentUrl = "http://localhost:5200/";
    public const string DeskUrl = "http://localhost:5201/";

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
        Start(Path.Combine(chapter, "Ch20.Beacon.Agent"), AgentUrl);
        Start(Path.Combine(chapter, "Ch20.Beacon.Desk"), DeskUrl);

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

    private void Start(string projectDirectory, string url)
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";
        var start = new ProcessStartInfo(dotnet)
        {
            ArgumentList = { "run", "--no-build", "--no-launch-profile", "--project", projectDirectory },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["ASPNETCORE_URLS"] = url;
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";

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

    // The test binaries live under Ch20.Tests/bin/...; the apps are its siblings.
    private static string ChapterFolder()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Ch20.Beacon.Agent")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Couldn't find the Chapter20-Declarative-UI folder above the test binaries.");
    }
}

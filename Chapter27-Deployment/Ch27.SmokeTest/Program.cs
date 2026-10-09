using System.Diagnostics;
using Ch27.SmokeTest;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

// Telemetry and service discovery, like every Beacon service. Under the AppHost the smoke run
// appears in the dashboard as one trace with the agent's spans beneath it.
builder.AddServiceDefaults();

// "https+http://beacon-agent" resolves through service discovery; pass a URL to test anything else.
var agentUrl = builder.Configuration["Smoke:AgentUrl"] ?? "https+http://beacon-agent";
builder.Services.AddHttpClient("beacon-agent", http => http.BaseAddress = new Uri(agentUrl))
    .AddAGUIStreamResilience();

using var host = builder.Build();
await host.StartAsync();

using var source = new ActivitySource(builder.Environment.ApplicationName);
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(builder.Configuration.GetValue("Smoke:TimeoutSeconds", 60)));

SmokeReport report;
using (var activity = source.StartActivity("smoke-test"))
{
    var http = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("beacon-agent");
    try
    {
        var token = await SmokeToken.GetAsync(builder.Configuration, timeout.Token);
        report = await StreamSmokeTest.RunAsync(http, token, timeout.Token);
    }
    catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or Azure.Identity.AuthenticationFailedException or InvalidOperationException)
    {
        report = new([new SmokeCheck("reach the agent", false, $"{ex.GetType().Name}: {ex.Message}")]);
    }

    activity?.SetTag("smoke.passed", report.Passed);
}

Console.WriteLine($"Smoke test against {agentUrl}");
foreach (var check in report.Checks)
{
    Console.WriteLine($"  {(check.Passed ? "pass" : "FAIL"),-5} {check.Name,-22} {check.Detail}");
}

Console.WriteLine(report.Passed ? "PASSED" : "FAILED");

await host.StopAsync(); // flushes telemetry before the process exits
return report.Passed ? 0 : 1;

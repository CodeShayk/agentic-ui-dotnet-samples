# AG-UI in .NET — Companion Code

The runnable code for every chapter of *AG-UI in .NET*. One solution, `AGUI-in-DotNet.slnx`, holds a folder per chapter and the shared libraries the chapters build on.

## Requirements

- **.NET 11 SDK** (the book targets `net11.0`). Until .NET 11 reaches general availability, use 11.0 RC1 (`11.0.100-rc.1.26425.128`), which has a go-live license. `global.json` pins it and rolls forward to newer feature bands.
- **.NET MAUI workload** for the Beacon Field chapters: `dotnet workload install maui-windows` (add `maui-android` or `maui-ios` for mobile targets).
- No model or API key is needed to run anything. Every agent defaults to Beacon's scripted model; Chapter 5 shows how to switch to Ollama, Azure OpenAI or OpenAI.

On .NET 10 LTS? Every AG-UI and Agent Framework package also targets `net10.0`, so you can follow along after a few changes: set `<BeaconTargetFramework>net10.0</BeaconTargetFramework>` in `Directory.Build.props`, point `global.json` at a 10.0 SDK, swap the release-candidate package pins for their 10.0 versions, change the MAUI and WPF projects' `net11.0-` targets, add explicit `xmlns` and `xmlns:x` declarations to the root of each MAUI XAML file that lacks them (.NET MAUI 10 has no implicit XAML namespaces), and install the .NET 10 MAUI workload. Appendix C of the book walks through it and lists the known differences; `eng/net10-recipe.py` applies the recipe to a scratch copy.

## Build and test

```bash
dotnet build AGUI-in-DotNet.slnx
dotnet test --solution AGUI-in-DotNet.slnx
```

Tests run on Microsoft.Testing.Platform (configured in `global.json`) and never call a live model unless `RUN_MODEL_TESTS=true`.

## Layout

| Folder | Contents |
|---|---|
| `Shared/Beacon.Domain` | Contoso Climate's records, seed data and in-memory store |
| `Shared/Beacon.AI` | Model provider selection, Beacon's tools and instructions, the scripted model |
| `Shared/Beacon.AGUI.Hosting` | The ASP.NET Core glue that hosts an `IChatClient` as an AG-UI endpoint (Chapter 4) |
| `Shared/Beacon.Testing` | Recorded-stream chat client and AG-UI wire helpers for tests |
| `Shared/Beacon.Desk.Ui` | Beacon Desk's Blazor components: layout, chat panel, safe Markdown rendering (Chapter 6) |
| `Shared/Beacon.Client.Core` | The client-side reducer and conversation service shared by Blazor and .NET MAUI (Chapter 7) |
| `eng/generate-solution.py` | Regenerates `AGUI-in-DotNet.slnx` from the projects on disk |
| `ChapterNN-*/` | Each chapter's checkpoint: the agent, the clients it touches and its tests |

Each chapter's agent listens on `http://localhost:5NN0/agent` (Chapter 12 on 5120; Chapter 4 uses 5044 and Chapter 6 uses 5060/5062/5063, explained in those chapters), and its Blazor Desk app, where there is one, on `5NN1`.

## Configuration

Model settings live in the `AI` section of each agent's configuration:

```json
{
  "AI": {
    "Provider": "Scripted",
    "Endpoint": "https://<your-resource>.openai.azure.com/",
    "Model": "<deployment-or-model>",
    "OllamaModel": "llama3.2"
  }
}
```

`Provider` is one of `Scripted`, `Ollama`, `AzureOpenAI` (Microsoft Entra ID through `DefaultAzureCredential`) or `OpenAI`. Keep `AI:ApiKey` in User Secrets, never in a file.

## License and attribution

`Shared/Beacon.AGUI.Hosting` adapts the ASP.NET Core glue from the AG-UI .NET SDK samples (`sdks/dotnet/samples/AGUI.Samples.Shared` in https://github.com/ag-ui-protocol/ag-ui), Copyright (c) 2025 AG-UI Protocol contributors, used under the MIT License.

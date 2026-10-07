# Overlay render previews

This small executable renders the production overlay with the headless UI fixture and synthetic Claw
descriptors. It does not start the resident shell, connect to Steam, acquire input, or invoke device
actions.

From the repository root:

```powershell
dotnet run --project tools/OverlayPreview/WSGM.OverlayPreview.csproj -c Release -p:SkipNativeArtifacts=true -- TestResults/overlay-preview 1280 720
dotnet run --project tools/OverlayPreview/WSGM.OverlayPreview.csproj -c Release -p:SkipNativeArtifacts=true -- TestResults/overlay-preview 3840 2160 2
```

Arguments are output directory, physical width, physical height, and optional UI scale. Exports
include the main destinations, complete device controls, the power menu and the docked keyboard.
Rendering does not execute the test suite or update visual baselines. Native transparency, touch
promotion, controller capture and hardware behavior still need attended testing.

## How it works

[`Program.cs`](Program.cs) parses the output path, width, height and scale with invariant culture,
initializes `TestApplication.BuildAvaloniaApp().SetupWithoutStarting()`, and invokes
`PreviewExports.Export`. It creates no resident application lifetime and does not discover test
methods. The defaults are `TestResults/overlay-preview`, 1280, 720 and 1.

The project references `tests/WSGM.UiTests` for the production-window fixtures and copies its JSON
publications into the tool's output.
[`PreviewExports.cs`](../../tests/WSGM.UiTests/Visual/PreviewExports.cs) is the export inventory;
[`TestApplication`](../../tests/WSGM.UiTests/Infrastructure/TestApplication.cs) is the headless
composition. Tool output is a review artifact, while checked-in visual baselines belong to the
separate test/update workflow in [UI documentation](../../docs/ui.md).

This is a Windows-targeted .NET executable outside `WSGM.slnx`. `SkipNativeArtifacts` suppresses
native payload staging for the preview build; it does not turn the application into a portable Linux
runtime or validate a distributable installer.

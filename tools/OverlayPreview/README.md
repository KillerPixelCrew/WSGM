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

# Avalonia live backdrop sample

Portable consumer of `src/Avalonia.LiveBackdrop`, separate from WSGM and its running session. Start
`LiveBackdropSample.exe`. The default blur is 8 px; adjust the slider live. The native DLL and all
published files must remain together. The app changes no Windows or Steam settings.

The window is always on top and does not appear in Alt-Tab or the taskbar. Esc or Close exits; F11
toggles fullscreen. The checkbox tests opaque fallback; Recreate releases and rebuilds the renderer.
Hide for 2 seconds releases it, then restores the window and creates it again. Use the dropdown and
text field to check popup stacking and input through the Avalonia surface. State transitions are
written to `LiveBackdrop-<timestamp>.log` next to the executable when writable.

Leave Windows Transparency Effects disabled for the ReviOS test. Open, move, resize, minimize,
restore and close other windows under it. Check Steam and a moving game. Report any black content,
misalignment, recursive images, orphan backdrop, input failure or noticeable frame-time change. The
native spike, this standalone Avalonia sample and the integrated WSGM Overlay passed on the Claw.

Build a self-contained portable directory:

```powershell
dotnet publish tools/LiveBackdropSample/LiveBackdropSample.csproj -c Release -o artifacts/LiveBackdropSample
```

The library README documents API ownership, fallback, support limits and attribution.

## Code and lifetime

[`Program.cs`](Program.cs) contains the standalone Avalonia entry point and `SampleApplication`.
`CreateWindow` configures one transparent topmost window and one `LiveBackdrop` attachment. The
slider updates `BlurRadius`, the checkbox updates `IsEnabled`, Recreate calls `Retry`, and a
one-shot dispatcher timer implements hide/show. Closing the window disposes the attachment through
the library's window-close subscription; the sample also stops its return timer.

[`LiveBackdropSample.csproj`](LiveBackdropSample.csproj) references only the reusable backdrop
project for this integration. The
[library README](../../src/Avalonia.LiveBackdrop/README.md#api-and-source-map) traces the
managed/native ABI, build artifacts and failure behavior. The confirmations above are historical
device evidence; running a headless test or merely rebuilding this sample is not a new Steam/game
compositor pass.

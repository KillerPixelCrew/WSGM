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

For another device, append a host-projected publication JSON path and its display name after the
scale argument. This exports only available Device pages, including individual expanded groups:

```powershell
dotnet run --project tools/OverlayPreview -c Release -p:SkipNativeArtifacts=true -- artifacts/atlas-preview 980 640 1 artifacts/acer-ui-publication.json "Acer Predator Atlas 8"
```

The publication is an offline fixture. Rendering it does not acquire its original hardware or
establish live Steam acceptance.

To render only the removable storage library levels of the Game Library, pass `storage-cases` after
the scale. It exports `importer-storage-home`, `-list`, `-detail` (a staged repair with Apply
focused), `-empty`, `-error` and `importer-entry-removed` from an explicit fictional storage
fixture:

```powershell
dotnet run --project tools/OverlayPreview -c Release -p:SkipNativeArtifacts=true -- artifacts/storage-preview 980 640 1 storage-cases
dotnet run --project tools/OverlayPreview -c Release -p:SkipNativeArtifacts=true -- artifacts/storage-preview 1280 800 1 storage-cases
```

To render only the Game Tools page, pass `game-tools-cases` after the scale. A fake Game Tools owner
publishes each case's state over a fixed library, so nothing is scanned, installed or downloaded.
The busy case then publishes a download tick and checks that only the progress element changed,
saving that frame as `game-tools-busy-tick`, and then the end of the operation, which must redraw
the page.

To render only the Steam Artwork Changer home, the searchable game list it shares with Game Tools,
pass `artwork-home-cases` after the scale. The shared artwork fake answers each library read from
the case's script, and the cases reach their state through the page itself: `artwork-home`
(populated), `-long-name`, `-empty` (an empty library), `-no-match` (a search typed on the overlay
keyboard), `-failed` (a Refresh that fails while the last good list stays) and `-reading` (a read
Steam has not answered):

```powershell
dotnet run --project tools/OverlayPreview -c Release -p:SkipNativeArtifacts=true -- artifacts/game-tools-preview 980 640 1 game-tools-cases
dotnet run --project tools/OverlayPreview -c Release -p:SkipNativeArtifacts=true -- artifacts/artwork-home-preview 980 640 1 artwork-home-cases
```

To render the Emulator Manager's and the Game Library's live progress, pass `live-progress-cases`
after the scale. Explicit fakes publish a progress tick and then the end of the work, and the tool
checks that each tick updates the progress element in place and each end redraws the level:
`emulators-busy`, `-busy-tick` and `-done`; `library-sources-applying`, `-sources-tick`,
`library-home-applying` (Stop inside the progress) and `-home-done`:

```powershell
dotnet run --project tools/OverlayPreview -c Release -p:SkipNativeArtifacts=true -- artifacts/live-progress-preview 980 640 1 live-progress-cases
```

To render only the Overlay card manager, pass `card-manager-cases` after the scale. A fake card
manager owner (`ICardManagerSource`) publishes each case's cards, so no drive is scanned and nothing
is renamed or written to Steam. The cases open the page through Steam > Steam library > Card
Manager: `card-manager-list` (an empty, a half and a nearly full card, each with its capacity bar
inside its row, and a card with no size reading), `-list-focus` (the half-full row focused),
`-list-end` (scrolled to Back), `-long-name` (a wrapping name and a path-like name), `-empty`,
`-scanning` (the first scan has not finished), `-error` (a failed scan with no remembered cards) and
`-editor` (one card's editor). The list cases also publish a change and check that the redrawn list
keeps every meter:

```powershell
dotnet run --project tools/OverlayPreview -c Release -p:SkipNativeArtifacts=true -- artifacts/card-manager-preview 980 640 1 card-manager-cases
dotnet run --project tools/OverlayPreview -c Release -p:SkipNativeArtifacts=true -- artifacts/card-manager-preview 1600 900 1 card-manager-cases
```

These cases are fakes too; they do not show live Steam data or establish Steam acceptance.

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

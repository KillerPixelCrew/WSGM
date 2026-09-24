# WSGM BackdropProbe

## Version 6

Run **BackdropProbe-v6.exe**, select mode **3**, and drag the **Blur** slider. Its range is 0 to 60
pixels of Gaussian standard deviation: 0 is sharp, 18 is the previous default, and larger values
produce stronger blur. The effect updates live without rebuilding the backdrop. The selected value
survives mode switches for this run and resets to 18 on launch. The slider only controls mode 3;
system acrylic in mode 1 retains Windows' own appearance. V5's automatic window tracking and v4's
tool-window flag remain enabled.

The maintainer confirmed live window synchronization and selected 8 px on the Claw. The reusable
Avalonia library now defaults to that value.

## Version 5

Run **BackdropProbe-v5.exe**. The maintainer confirmed v4 works with Steam and games on the Claw,
but newly opened and moved windows required manual refresh. V4 only updated the shared window list
and geometry when the probe itself moved/resized or the mode was reapplied.

V5 listens for top-level window creation, destruction, show/hide, movement, resizing, stacking,
foreground, minimize/restore and cloak changes using out-of-context Windows event hooks. Events
queue a single compositor update after 16 ms without postponing it during continuous dragging. The
existing device, visuals and blur stay alive; no capture stream or idle refresh loop is added.
Successful automatic updates do not write per-event logs. Hooks and the pending timer are released
on exit. The tool-window flag remains enabled by default. The maintainer confirmed that Steam and
game content remained visible during automatic updates.

Close older probes, select mode 3, then open, drag, resize, minimize, restore and close another
window. Also switch between overlapping windows and confirm Steam/game content stays live. Repeat in
mode 2 to inspect alignment without blur. The Claw confirmed this behavior. Desktop shell recreation
and display topology changes are outside this revision; use Reapply after those changes.

[Windows event hook delivery](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwineventhook)

## Version 4

Run **BackdropProbe-v4.exe**. The Claw's v3 test confirmed live windows and desktop through the
shared blur, but Steam web content became black even in a fresh mode 0 and recovered on exit. That
excludes the blur effect as the trigger. Chromium's occlusion tracker ignores tool windows; v4
therefore marks the probe as `WS_EX_TOOLWINDOW` from creation. The maintainer subsequently confirmed
it works with Steam and games on the Claw. **T** toggles that flag for comparison and records it in
the log. The status displays its current value. No Steam settings or processes are changed.

First check Steam in the initial mode 0, then modes 2 and 3. If it works, pressing T off and back on
can check whether the flag explains the difference. Close older probes before this comparison: an
older covering window can still cause occlusion.

[Chromium occlusion filtering](https://chromium.googlesource.com/chromium/src/+/133.0.6943.53/ui/aura/native_window_occlusion_tracker_win.cc)

## Version 3

Run **BackdropProbe-v3.exe** on the shared drive. V2's desktop layer covered the application windows
because the probe reversed DirectComposition's null-reference insertion semantics.
`AddVisual(visual, FALSE, nullptr)` places a new visual above all siblings; TRUE places it below all
siblings. V3 fixes both shell-layer ordering and the placement of shared windows above them. The
rest of the rendering path is unchanged. Retest modes 2 and 3 over an open window and the desktop,
then check motion. Mode 1 remains the system-controlled comparison.

[AddVisual semantics](https://learn.microsoft.com/en-us/windows/win32/api/dcomp/nf-dcomp-idcompositionvisual-addvisual)

## Version 2

The Claw's v1 log reports Windows build 26200, transparency OFF, DWM ON, and successful private API
calls. The maintainer observed partial Explorer blur with a sharp desktop. This is partial visual
evidence, not a complete backdrop pass or a verified performance result.

V2 adds visible Progman/WorkerW desktop layers underneath the shared application windows and applies
Gaussian blur to their common parent visual. It samples the virtual desktop at native size and
offsets the group to the probe's client position. The log now records source coordinates and the
number of desktop layers. Controls use a separate owned window with normal GDI redirection; they no
longer rely on GDI children of the compositor-only window. **H** hides/shows that palette. These are
candidate fixes for the incomplete coverage and missing controls seen in v1.

On the shared drive, run **BackdropProbe-v2.exe**. The original executable and its logs are
retained.

## Purpose

A standalone Windows 11 x64 experiment for issue #183. It compares compositor backdrops with Windows
transparency effects disabled, without an application-managed screen capture stream. This is a
native Win32/DirectComposition probe, not an Avalonia integration or a production renderer.

Run `BackdropProbe.exe` as your normal user. No installation or .NET runtime is required. The app
does not change Windows settings, install services, connect to Steam, or start WSGM. The window
stays on top. **Close** or **Esc** exits; **Fullscreen** or **F11** toggles fullscreen.

## On the Claw

Leave ReviOS's effects settings as they are. Start the probe over Steam, a moving game in borderless
mode, or a playing video. Click **Motion background** for a synthetic moving pattern instead. The
pattern is an ordinary separate window. Its animation costs CPU, so hide it when comparing game
frame times. The first test should be windowed; repeat fullscreen afterward.

Use these buttons (or keys 0 through 3):

| Mode             | Expected purpose                                                                                                |
| ---------------- | --------------------------------------------------------------------------------------------------------------- |
| 0 Clear          | Transparent control case; the underlying content should remain sharp.                                           |
| 1 System acrylic | Windows' public system backdrop; may become solid when effects are disabled. Requires Windows 11 22H2 or later. |
| 2 Shared sharp   | Private DWM shared desktop visual, with no blur. Tests whether the underlying picture is accessible and live.   |
| 3 Shared blur    | The same shared visual with adjustable Gaussian blur applied by DirectComposition; default sigma 18 pixels.     |

In modes 2 and 3, check that moving objects keep moving, positions match the underlying window, and
WSGM's probe does not recursively appear in its own background. Move and resize the probe; then test
fullscreen. Switch to mode 0 to compare sharpness. The native buttons and status panel are
deliberately opaque; inspect the large area beneath them.

**API calls succeeded does not mean blur succeeded.** Report each mode as live blurred, live sharp,
solid/black, frozen, misaligned, recursive, or crashed. Mention whether fullscreen differs and
whether the game visibly stutters. The probe does not measure GPU frame times or establish
production performance. Mode 0 and mode 3 over the same game are the useful performance comparison.

Each run writes `BackdropProbe-<timestamp>-<pid>.log` beside the executable, falling back to the
temp directory if necessary. The status shows the path. The log contains the Windows build, registry
transparency preference, DWM state, selected modes and HRESULTs. **Reapply / retry** recreates the
current method after a settings change or failure. Share the log and your observations.

The private methods are undocumented exports 163 and 164, with the Windows 11 signature. Their
presence and successful return codes do not guarantee support. This probe uses no capture exclusion
flag; it excludes its own window from the shared visual and live preview. Window attributes
disappear when the app exits. It does not alter DWM in another process. A failure is evidence to
record rather than a reason to change ReviOS settings.

## Build

With Visual Studio C++ tools and the Windows SDK installed:

```powershell
./tools/BackdropProbe/build.ps1
```

Output goes to `artifacts/BackdropProbe`. The C++ runtime is statically linked. The probe is
intentionally outside `WSGM.slnx` and the product's release/deployment scripts.

## References

The private API declarations and composition approach follow
[ADeltaX's shared visual example](https://gist.github.com/ADeltaX/aea6aac248604d0cb7d423a61b06e247)
and [Win32-Acrylic-Effect](https://github.com/selastingeorge/Win32-Acrylic-Effect). The latter's MIT
notice is preserved in `REFERENCE-LICENSE.txt`. The diagnostic UI, lifecycle and mode comparison are
specific to this probe.

[Microsoft's host backdrop documentation](https://learn.microsoft.com/en-us/uwp/api/windows.ui.composition.compositor.createhostbackdropbrush)
states that the public host backdrop is controlled by user transparency settings and power policies.
This experiment evaluates the lower-level shared visual alternative; it does not assume that the
setting disables the compositor itself.

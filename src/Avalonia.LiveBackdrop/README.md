# Avalonia.LiveBackdrop

A reusable Windows 11 x64 library for a live, blurred desktop behind an Avalonia overlay window. Its
default Gaussian standard deviation is **8 physical pixels**, selected in the maintainer's
Claw/ReviOS probe test. It uses DWM and DirectComposition but does **not** depend on Windows'
Transparency Effects preference. It does not capture screenshots or run a video capture stream.

## Use

Reference `Avalonia.LiveBackdrop.csproj`. The native DLL and attribution file copy transitively to
the application's build and publish directory. Build on Windows with .NET 10, PowerShell 7, Visual
Studio C++ build tools and the Windows SDK. Consumers need no C++ development tools or separate VC
runtime installation; the backend uses the static runtime. For compile-only previews,
`-p:SkipNativeArtifacts=true` omits the native backend and uses the opaque fallback.

Configure your Avalonia window before showing it:

```csharp
using Avalonia.Controls;
using Avalonia.LiveBackdrop;
using Avalonia.Media;

var window = new Window
{
    Background = Brushes.Transparent,
    TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
    ShowInTaskbar = false,
    Topmost = true
};
var backdrop = LiveBackdrop.Attach(window); // default: 8 px
backdrop.BlurRadius = 12;                    // live adjustment, 0..LiveBackdrop.MaximumBlurRadius (60)
backdrop.StateChanged += (_, _) =>
    Console.WriteLine(backdrop.IsActive ? "Glass active" : backdrop.FailureReason);
window.Show();
```

Keep the attachment in your window/controller. Closing the window disposes it automatically;
`Dispose()` also allows earlier detachment. All calls and events belong on Avalonia's UI thread.
Attaching twice to one window throws. Hide/minimize releases the session; show/restore creates it
again. `IsEnabled = false` releases it and displays the fallback fill. `Retry()` explicitly retries
a failed session. There is no automatic retry loop after a device/API failure. `StateChanged` is
raised only when `IsActive`, `FailureReason` or `IsEnabled` actually changed, and a native failure
that arrives after its session was stopped, hidden or disposed is ignored.

The attachment owns the window's background while attached: its original fill when active, opaque
dark gray when unavailable or disabled, and the original fill again on disposal. Supply a custom
opaque `fallback` brush to `Attach` if needed. Put translucent tint and borders in the normal
Avalonia content. Nested panels share the one blur behind the window; they do not each perform a
blur pass. An opaque root panel will conceal the backdrop. This version blurs the entire rectangular
client area; it does not implement independent panel masks, rounded cutouts or liquid refraction.

## Ownership and rendering

- Managed `LiveBackdrop` owns the Avalonia subscriptions, native session, fallback and failure
  state.
- `Native/Backdrop.cpp` owns a nonactivating, disabled, input-transparent tool window directly
  behind the Avalonia window. Avalonia retains its own HWND compositor target and renderer. The
  companion follows physical client bounds, visibility, minimization, foreground and stacking
  changes.
- One D3D11/DirectComposition session combines shared application visuals with Progman/WorkerW shell
  thumbnails below them and applies one Gaussian effect. Both sources use the host's physical client
  rectangle and the same local coordinates. D3D11 uses the DXGI adapter whose attached output owns
  the host monitor. Window events coalesce for 16 ms; desktop pixels themselves remain
  compositor-driven. There is no refresh timer running while idle.
- Shell identity/bounds and the sampled client rectangle are compared on refresh and thumbnails
  rebuilt when they change. A monitor, adapter or output topology transition retires the old helper
  and compositor resources together before creating the replacement at the new physical bounds.
  During a client move/resize, the helper is repositioned and its new source is committed before it
  becomes visible; unchanged refreshes never hide it or wait for a composition commit. All windows
  of the consumer process are excluded to prevent recursive sampling, including its popup windows
  and other library instances. Companion window events do not trigger other instances' refreshes.
- The host temporarily receives `WS_EX_TOOLWINDOW` and loses `WS_EX_APPWINDOW`. Chromium's occlusion
  tracker otherwise stops Steam web content under a visually transparent covering window. Original
  values of those two bits are restored on detach, while other style bits are preserved. The host
  therefore does not behave like a normal taskbar/Alt-Tab window while attached.
- Hooks, timers, subclass, thumbnails, COM resources and companion HWND are released on the creating
  UI thread. Native failures hide the companion immediately and queue the managed fallback; native
  callbacks never dispose their own session in place.

## Support and evidence

This backend uses **undocumented Windows 11 DWM exports** 147, 163 and 164. Missing exports, native
payload, unsupported platform or initialization/update failure leave an opaque fallback. A
successful API call reports initialization, not a guarantee that a particular Windows/driver build
renders it correctly. `IsActive` indicates an initialized session. Windows updates may require
backend changes. Windows 10, ARM64, exclusive fullscreen, protected content and per-panel clipping
are not supported claims. No Windows or Steam setting is changed.

The maintainer confirmed the **native probe** on the Claw's ReviOS Windows 11 build 26200 with
Transparency Effects off: live Steam and game content, tool-window occlusion fix, and 8 px preferred
blur. The maintainer also confirmed that the standalone Avalonia sample shows live blur over Steam
or a game without black content or misplaced windows.

Run the separate sample:

```powershell
dotnet run --project tools/LiveBackdropSample/LiveBackdropSample.csproj -c Release
```

Check Steam/games, new/moved/resized/closed windows, overlapping window order, input and popups,
hide/show, minimize/restore, F11, closing without an orphan window, bright/dark content and frame
times. Repeat across monitor/DPI changes and an Explorer restart if those scenarios matter to
deployment. Headless tests cannot establish compositor output or game performance.

## License and references

This library remains under the repository's GPL-3.0-or-later license. The private API approach
follows [ADeltaX's example](https://gist.github.com/ADeltaX/aea6aac248604d0cb7d423a61b06e247) and
[Win32-Acrylic-Effect](https://github.com/selastingeorge/Win32-Acrylic-Effect); the latter's MIT
notice is retained in `REFERENCE-LICENSE.txt` and copied with builds.

Relevant platform contracts:
[window event delivery](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwineventhook),
[window positioning](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos),
and
[Chromium's tool-window occlusion filter](https://chromium.googlesource.com/chromium/src/+/133.0.6943.53/ui/aura/native_window_occlusion_tracker_win.cc).

## API and source map

| API                                                                           | Contract                                                                                                                                                                              |
| ----------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `LiveBackdrop.Attach(Window, double blurRadius = 8, IBrush? fallback = null)` | UI-thread attachment, valid before or after opening. Requires a non-null window and one attachment per window.                                                                        |
| `BlurRadius`, `MaximumBlurRadius`                                             | Gaussian standard deviation in physical pixels; finite values from 0 through 60. Invalid values throw rather than clamp. Updating an active session calls the native effect directly. |
| `IsEnabled`                                                                   | Releases/reconciles the native session and updates fallback state; enabling does not clear a recorded failure.                                                                        |
| `IsActive`, `FailureReason`, `StateChanged`                                   | Observe initialized native ownership and its failure state. State events do not report each radius change or prove visible compositor output.                                         |
| `Retry()`                                                                     | Stops the previous session, clears the failure and reconciles again. A hidden/minimized window still waits until it can show.                                                         |
| `Dispose()`                                                                   | Idempotent on the UI thread; unsubscribes, releases the session and restores the original background. Mutating a disposed attachment is refused.                                      |

[`LiveBackdrop.cs`](LiveBackdrop.cs) is the complete managed lifecycle. Reconciliation waits for a
visible, non-minimized window and a transparent native surface before calling
[`NativeMethods.cs`](NativeMethods.cs). The C ABI is `BackdropCreate` (HRESULT plus opaque session),
`BackdropSetBlur` (HRESULT) and `BackdropDestroy`; its failure callback is marshaled back to the
Avalonia dispatcher with the session generation. A callback from a retired session cannot fail its
replacement.

[`Native/Backdrop.cpp`](Native/Backdrop.cpp) contains the session object, helper-window procedure,
owner subclass, window-event hooks, desktop-thumbnail refresh, compositor construction and cleanup.
The creating thread owns every native session resource. `Native/build.ps1` locates the Windows C++
toolchain and builds the native DLL; `Avalonia.LiveBackdrop.csproj` calls it before build and copies
the payload and both license files transitively. Generated output lives in
`obj/native/<configuration>`.

[`AttachmentTests.cs`](../../tests/Avalonia.LiveBackdrop.Tests/AttachmentTests.cs) substitutes the
native create/update/destroy and HWND seams to exercise attachment ownership, hide/show, failure,
retry and stale callbacks. These are managed lifecycle checks. Use the
[standalone sample](../../tools/LiveBackdropSample/README.md) for attended compositor verification,
and follow the repository's manual-first test timing.

[`NativeGeometryTests.cpp`](../../tests/Avalonia.LiveBackdrop.Tests/NativeGeometryTests.cpp) checks
the native physical-pixel crop contract for negative origins, partial shell intersections, a client
spanning outputs and movement between outputs. From an x64 Visual Studio developer prompt, compile
with `cl /nologo /std:c++17 /EHsc /W4 /WX NativeGeometryTests.cpp /link user32.lib` in its
directory, then run the resulting executable after the manual test. These checks do not validate
visible blur, Optimus presentation or actual DPI virtualization on a live window.

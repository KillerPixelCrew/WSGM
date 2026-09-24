# Avalonia.LiveBackdrop

A reusable Windows 11 x64 library for a live, blurred desktop behind an Avalonia overlay window. Its
default Gaussian standard deviation is **8 physical pixels**, selected in the maintainer's
Claw/ReviOS probe test. It uses DWM and DirectComposition but does **not** depend on Windows'
Transparency Effects preference. It does not capture screenshots or run a video capture stream.

## Use

Reference `Avalonia.LiveBackdrop.csproj`. The native DLL and attribution file copy transitively to
the application's build and publish directory. Build on Windows with .NET 10, PowerShell 7, Visual
Studio C++ build tools and the Windows SDK. Consumers need no C++ development tools or separate VC
runtime installation; the backend uses the static runtime.

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
backdrop.BlurRadius = 12;                    // live adjustment, valid range: 0..60
backdrop.StateChanged += (_, _) =>
    Console.WriteLine(backdrop.IsActive ? "Glass active" : backdrop.FailureReason);
window.Show();
```

Keep the attachment in your window/controller. Closing the window disposes it automatically;
`Dispose()` also allows earlier detachment. All calls and events belong on Avalonia's UI thread.
Attaching twice to one window throws. Hide/minimize releases the session; show/restore creates it
again. `IsEnabled = false` releases it and displays the fallback fill. `Retry()` explicitly retries
a failed session. There is no automatic retry loop after a device/API failure.

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
  thumbnails below them and applies one Gaussian effect. Window events coalesce for 16 ms; desktop
  pixels themselves remain compositor-driven. There is no refresh timer running while idle.
- Shell identity/bounds are compared on refresh and thumbnails rebuilt when they change. Display
  changes update virtual-desktop coordinates. All windows of the consumer process are excluded to
  prevent recursive sampling, including its popup windows and other library instances.
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
or a game without black content or misplaced windows. The integrated WSGM Overlay was then confirmed
over Steam and a game, with readable bright/dark content and no noticeable frame-time change. WSGM
exposes the radius in Settings > Quick Access, from 0 to 60 physical pixels.

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

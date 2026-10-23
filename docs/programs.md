# Programs

Tools > Programs manages companion desktop applications. Add an executable with the Overlay's
controller and touch accessible file picker, then edit its name, arguments and optional working
directory. Every entry can be pinned to Quick access. The page and its pins invoke the same
session-owned launch-or-focus service and consume the same running state. Removing an entry or
closing the Overlay never terminates the application.

Definitions live in `AppConfig.Programs`, with stable IDs and `program:{id}` Quick access pins.
Executable selection records its absolute path and SHA-256. Missing or changed executables stay
configured and require explicit replacement selection or approval before another launch. Editing a
name, arguments or options never changes the approved executable or resolved UI identity. Normal
applications use WSGM's least-privilege interactive scheduled-task launcher. Administrator elevation
is an explicit per-program choice; a failed normal launch never silently escalates. A declined UAC
prompt starts nothing and leaves no pending launch behind.

Matching uses the selected exact executable image paths in the current interactive session. File
fingerprints gate another launch; an already-running image can still be focused if its disk file was
removed or replaced. Filenames alone never authorize focus. Eligible windows use the existing
switchable window filter: visible, titled, non-tool, non-cloaked top-level application windows.
Selection prefers the foreground window, then the last active window, then top-level Z order.
Minimized windows are restored before ordinary Windows foreground activation. A refusal is recorded
on the Programs page; WSGM does not inject into games or force a game's window mode.

For a launcher or updater whose real UI uses another executable, launch/open the tool, return to
Programs and choose **Select running UI application**. Explicit confirmation remembers the selected
window's verified executable path and fingerprint. Its identity replaces the bootstrap process for
subsequent running/focus decisions. A changed UI executable requires another selection or explicitly
forgetting that identity. No product-specific process-name rules are used.

A running process without an eligible window gets a short bounded UI wait and an informative result.
An optional **Reinvoke when running without a window** policy permits the application's own
single-instance IPC activation; leave it off unless that behavior is known to be safe. Dispatches
without a resolved UI remain pending, preventing repeated accidental launches. Only explicit UI
selection, observing a matching UI, or **Clear pending launch** releases that protection. Clearing
pending state explains that a previous start may still complete.

One five-second background reconciliation feeds all Programs views and pins. Executable hashes are
cached by file metadata for state presentation and recomputed before launch. Application icons are
extracted once per approved executable identity, rasterized through the existing icon renderer and
held by the session, with a generic application icon when extraction is unavailable.

Activation uses OverlayController's existing deferred close and waits for its Steam Input lease
release before launching or focusing. It suppresses the old game's focus return. Reopening the
sheet, another activation or session shutdown cancels an obsolete pending handoff. The normal
Overlay/QAM invocation remains available after moving to a desktop tool, including after that tool
exits.

Build, visual captures, real Windows focus/integrity behavior, controller/touch input and Game Mode
handoff need the combined delivery's validation and attended manual acceptance. Source inspection
alone does not establish those results.

# Programs

Tools > Programs manages companion applications. **Add program** opens a searchable list of every
application installed on this PC, the same set Windows shows in Start: all-users and per-user Start
Menu shortcuts plus packaged (Store) apps such as Xbox. An alphabet bar beside the scrollbar jumps
to a letter. Picking an app adds it at once and returns to the list. **Browse for a file…** is the
manual choice in the same flow, with the Overlay's controller and touch accessible file picker, and
stays available while the list loads and when it cannot be read. Programs exist only in the Overlay;
Steam has no surface that adds or edits them. Each entry has small edit, pin and remove buttons
beside it; the name, arguments and optional working directory are changed in its editor. Every entry
can be pinned to Quick access. The page and its pins invoke the same session-owned launch-or-focus
service and consume the same running state. Removing an entry or closing the Overlay never
terminates the application.

Definitions live in `AppConfig.Programs`, with stable IDs and `program:{id}` Quick access pins.
Executable selection records its absolute path and SHA-256. Missing or changed executables stay
configured and require explicit replacement selection or approval before another launch. Editing a
name, arguments or options never changes the approved executable or resolved UI identity. Normal
applications use WSGM's least-privilege interactive scheduled-task launcher. Administrator elevation
is an explicit per-program choice; a failed normal launch never silently escalates. A declined UAC
prompt starts nothing and leaves no pending launch behind.

`WindowsDeviceControl.InstalledApplications` reads the Applications folder (`shell:AppsFolder`) on
its own STA thread and `InstalledProgramCatalog` decides what is offered. It de-duplicates by
application id or executable and arguments, and drops a desktop entry that is not an existing
`.exe`, is maintenance tooling (uninstallers, setup, help, readmes) or is Explorer, which WSGM owns
in Game Mode. Icons are read in batches once the list is drawn and released when the flow ends.

A packaged application stores its `AppUserModelId` and has no executable or fingerprint.
Availability is its package family being installed. Activation goes through the same scheduled-task
handoff as every normal program, running `WSGM.PackagedLaunch --activate`, because WSGM runs
elevated and Windows refuses package activation there. A running package is found by the application
id of its windows, including the hosted application of a UWP frame, and Windows activates a running
package instead of starting another, so it needs no pending-launch guard.

Matching uses the selected exact executable image paths in the current interactive session. File
fingerprints gate another launch; an already-running image can still be focused if its disk file was
removed or replaced. Filenames alone never authorize focus. Eligible windows use the existing
switchable window filter: visible, titled, non-tool, non-cloaked top-level application windows.
Selection prefers the foreground window, then the last active window, then top-level Z order.
Minimized windows are restored before ordinary Windows foreground activation. A refusal is recorded
on the Programs page; WSGM does not inject into games or force a game's window mode.

For a launcher or updater whose real UI uses another executable, Programs observes process ancestry
for up to thirty seconds, beginning before dispatch. Only a newly created process with the approved
exact launch path can seed that observation. Its verified descendants can supply the UI, including
children with different filenames. Windows process-start and executable image-load events preserve
short-lived handoffs; kernel creation sequences identify each parent independently of recycled PIDs.
When tracing is unavailable, snapshots retain process handles to prevent parent ID reuse. Once an
eligible window appears, Programs remembers its exact executable path and fingerprint automatically.
Its identity replaces the bootstrap process for subsequent running/focus decisions. No
product-specific process-name rules are used.

An application that hands off through an unrelated service, or a short-lived bootstrap when Windows
refuses tracing permission, may leave no verifiable ancestry. In that case, return to Programs and
choose **Select running UI application**. Explicit confirmation remembers the selected window's
verified executable path and fingerprint. A changed UI executable requires another selection or
explicitly forgetting that identity.

A running process without an eligible window gets a short bounded UI wait and an informative result.
An optional **Reinvoke when running without a window** policy permits the application's own
single-instance IPC activation; leave it off unless that behavior is known to be safe. Dispatches
without a resolved UI remain pending, preventing repeated accidental launches. Only explicit UI
selection, observing a matching UI (including a verified launch descendant), or **Clear pending
launch** releases that protection. Clearing pending state explains that a previous start may still
complete.

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

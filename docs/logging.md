# Logging

`%LOCALAPPDATA%\WSGM\wsgm.log` is the primary remote-diagnosis surface for the resident application.
Game Mode has no Explorer taskbar, so lifecycle changes and failures must reach this file. Setup,
the logon service and the game launchers also keep their own logs, listed below, for work outside
the resident process. A line that is missing costs a diagnosis, and a line that repeats costs every
other line around it.

## Levels

`Log.Debug/Info/Warn/Error`, `PluginTrace.Debug/Info/Warn/Error` in a device plugin, and
`IPluginHost.Trace(level, scope, message)` in a common plugin, which WSGM writes under
`plugin/<id>`. The threshold is `Info` unless verbose diagnostics are on.

| Level   | Write it when                                                                                                       | Not when                                                                                                                 |
| ------- | ------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------ |
| `Debug` | The value only helps while investigating a specific problem: raw coordinates, per-pass detail, a decision's inputs. | It is something a maintainer reading a normal log needs. Debug is off by default, so anything load-bearing disappears.   |
| `Info`  | A state actually changed, or a lifecycle step happened: a cycle activated, a mode switched, a target was created.   | The same state was observed again. That is `Change`, or nothing.                                                         |
| `Warn`  | Behaviour changed as a result: degraded, refused, fell back, retried.                                               | Something merely did not apply because it was already correct, or a normal absence: Steam being closed is not a warning. |
| `Error` | The code could not handle it and something the user cares about is now wrong.                                       | It is recoverable and was recovered.                                                                                     |

Severity is a promise about consequence, not a volume knob. The measured state before this policy
existed was `Warn` outnumbering `Info` 501:412 in the application and 25:3 in the Steam UI toolkit,
because `Warn` had drifted into meaning "something did not happen". Reading a log where most
warnings are routine is the same as reading one with no warnings at all.

## Poll loops use a key

Anything observed repeatedly goes through `Log.Change(key, message, level)`, or
`PluginTrace.Change(scope, key, message, level)` from a device plugin, or
`IPluginHost.TraceChange(level, scope, key, message)` from a common plugin. It writes only when that
key's message differs, and counts what it suppressed so the next line that does change carries
`(previous state held for N more polls)`. A silent drop would be worse than the repetition, because
a stalled timer and a steady state would look identical.

Two measured examples of what this is for, both real:

- One session wrote 43,392 lines of which 22,000 were five messages a timer kept re-stating;
  `Steam CEF: nothing is listening on port 8080` appeared 8,044 times.
- One day's log was 40% `plugin/motion`, 7,619 lines, from two messages alternating either side of a
  freshness threshold about 1.3 times a second.

The second one is the more instructive failure, because `Change` would not have saved it. Two
messages alternating under one key both differ from what came before, so both write every time. The
fix was to stop reporting a crossing that was not news. **Reach for the threshold before the level:
a line that should not exist is not fixed by hiding it at `Debug`,** where it still runs, still
builds its string, and comes back the moment someone turns verbose on to investigate something else.

## Keys

`Log.Change` keys are a namespace of their own, capped at 512. Past that the whole map is dropped
and every key writes once more, which is the correct failure for a diagnostic that must not grow
without bound. Per-subject keys (`tray.rejected.{hwnd}.{uid}`) are why the cap exists.

Use dotted segments, most general first: `steam.ui.discovery`, `running-apps.observation`,
`device-command/{capability}`. Existing keys are in three styles and several are documented
contracts in [device plugin system](device-plugin-system.md) and
[the Steam CEF system](steam-cef-system.md); do not rename those to match, and write new ones in the
dotted style.

The device package's keys are namespaced by the host as `plugin/{scope}/{key}`. Common plugins use
`plugin/{pluginId}/{scope}/{key}`, so two packages can use the same local scope and key. Their
visible lines follow the same prefix with `: message`; blank scopes become `plugin` and blank keys
become `state`. [`PluginLogLine`](../src/WSGM/Shell/PluginLogLine.cs) owns this formatting, after
the host checks that the publishing plugin is still current.

## Verbosity

Off by default. `Settings → System → Diagnostics → Verbose logging` persists the choice as
`AppConfig.LogVerbosity` and takes effect on the next configuration reload without a restart, which
matters because the process being diagnosed is the shell. `--verbose` sets it for one run and wins
over the stored value.

Raising verbosity must not turn the log into the thing `Debug` exists to prevent. If a verbose log
is unreadable, the fix is a `Change` key or a threshold, not a quieter default.

## Files and retention

Paths below use the account running the process. Setup/service logs use ProgramData; they do not
depend on a signed-in user's profile.

| File                                      | Writer and purpose                                                  | Rotation                                                        |
| ----------------------------------------- | ------------------------------------------------------------------- | --------------------------------------------------------------- |
| `%LOCALAPPDATA%\WSGM\wsgm.log`            | Resident session, Settings and runtime maintenance one-shots        | Best-effort rotation above 5 MiB to `wsgm.old.log`; one archive |
| `%LOCALAPPDATA%\WSGM\launch.log`          | `WSGM.Launch`, including lease and de-elevation handoff failures    | 2 MiB threshold, `launch.log.1`, `.2`, `.3`                     |
| `%LOCALAPPDATA%\WSGM\packaged-launch.log` | Packaged and followed game sessions, route choice and recovery      | 2 MiB threshold, `packaged-launch.log.1`, `.2`, `.3`            |
| `%ProgramData%\WSGM\wsgm-service.log`     | Logon admission, token choice, launched PID and watchdog fallback   | 1 MiB threshold, `wsgm-service.log.old`                         |
| `%ProgramData%\WSGM\setup.log`            | Setup detection, per-step result, rollback and component operations | Appended without size rotation in the current implementation    |
| `%ProgramData%\WSGM\update-failed.txt`    | Latest failed quiet update, read by WSGM at startup and in Settings | One failure record; cleared after a successful update           |

[`Core/Log.cs`](../src/WSGM/Core/Log.cs) is process-wide. It formats local timestamps with
milliseconds and a padded five-character level (`[info ]`, `[warn ]`, `[error]`, `[debug]`). Its
process-local lock keeps repeat suppression and appending ordered. Each actual append encodes UTF-8
and opens a `FileStream` with `FileMode.Append` and `FileShare.ReadWrite | FileShare.Delete`; up to
three short retries follow an `IOException`, and a failed write is dropped. Suppressed `Debug` calls
perform no append, although callers still construct their strings. High-rate telemetry and input
paths must therefore avoid logging calls altogether.

`Local\WSGM.LogRotate` serializes rotation attempts across shell, Settings and one-shots, not every
append. Acquiring it never waits. Rotation checks run at initialization and after approximately 256
KiB of rendered-line characters, with the real file size checked under the mutex. Concurrent
appenders or failed rotations can exceed the threshold; it is a retention target rather than a hard
write limit. `Change` also maintains its suppression state for messages below the current level, so
raising verbosity does not force an unchanged keyed message to reappear immediately.

[`Shared/Process/RotatingFileLog.cs`](../src/Shared/Process/RotatingFileLog.cs) is linked into the
launchers and logon service. It uses the same UTF-8 append sharing but checks its size on each
append and has a process-local lock, without the main logger's named rotation mutex or repeat
counter. The launcher adapters add `[pid N]` so overlapping game sessions can be separated.
`PackagedLaunchLog.Change` suppresses equal messages but does not add the main logger's poll-count
suffix. The service uses uppercase levels; setup uses an unbracketed uppercase level. Do not assume
every component has the main logger's verbosity control or rotation implementation.

[`WsgmSteamUiLog`](../src/WSGM/Core/WsgmSteamUiLog.cs) installs the Steam toolkit sink immediately
after the main logger initializes. Plugin diagnostics pass through `PluginLogLine` and inherit the
resident threshold and keyed suppression. The native library, setup and wrapper logs do not become
verbose merely because `AppConfig.LogVerbosity` changes.

For a missing sign-in launch, pair `wsgm-service.log` with the first `Run mode` entry in `wsgm.log`.
For an update, start with `setup.log` and `update-failed.txt`; for a game handoff, use the matching
launcher log and its PID. Environment variable values/blocks, SDDL, window titles, module lists and
token SIDs are excluded from packaged-launch diagnostics. Record outcomes and identities needed to
correlate the failure, without copying game arguments or session credentials into log messages.

`Log` stays uninitialized in tests, and no test may touch `%LOCALAPPDATA%\WSGM`. Test a logging
primitive only with explicit temporary paths. Lifecycle flow and its early recovery paths are in
[boot and shell](boot-and-shell.md); setup and launcher behavior is covered by [setup](setup.md),
[elevation](elevation.md) and [packaged-game launcher](packaged-game-launcher.md).

# Logging

`%LOCALAPPDATA%\WSGM\wsgm.log` is the whole of remote diagnosis. There are no toasts and no taskbar
in shell mode, so a problem that is not in this file did not happen as far as anyone helping is
concerned. That makes both halves of the job real: a line that is missing costs a diagnosis, and a
line that repeats costs every other line around it.

## Levels

`Log.Debug/Info/Warn/Error`, and `PluginTrace.Debug/Info/Warn/Error` on the plugin side. The
threshold is `Info` unless verbose diagnostics are on.

| Level   | Write it when                                                                                                       | Not when                                                                                                                  |
| ------- | ------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------- |
| `Debug` | The value only helps while investigating a specific problem: raw coordinates, per-pass detail, a decision's inputs. | It is something a maintainer reading a normal log needs. Debug is off by default, so anything load-bearing disappears.    |
| `Info`  | A state actually changed, or a lifecycle step happened: a cycle activated, a mode switched, a target was created.   | The same state was observed again. That is `Change`, or nothing.                                                          |
| `Warn`  | Behaviour changed as a result: degraded, refused, fell back, retried.                                               | Something merely did not apply because it was already correct, or a normal absence — Steam being closed is not a warning. |
| `Error` | The code could not handle it and something the user cares about is now wrong.                                       | It is recoverable and was recovered.                                                                                      |

Severity is a promise about consequence, not a volume knob. The measured state before this policy
existed was `Warn` outnumbering `Info` 501:412 in the application and 25:3 in the Steam UI toolkit,
because `Warn` had drifted into meaning "something did not happen". Reading a log where most
warnings are routine is the same as reading one with no warnings at all.

## Poll loops use a key

Anything observed repeatedly goes through `Log.Change(key, message, level)`, or
`PluginTrace.Change(scope, key, message, level)` from a plugin. It writes only when that key's
message differs, and counts what it suppressed so the next line that does change carries
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

`Log.Change` keys are a namespace of their own, capped at 512 — past that the whole map is dropped
and every key writes once more, which is the correct failure for a diagnostic that must not grow
without bound. Per-subject keys (`tray.rejected.{hwnd}.{uid}`) are why the cap exists.

Use dotted segments, most general first: `steam.ui.discovery`, `running-apps.observation`,
`device-command/{capability}`. Existing keys are in three styles and several are documented
contracts in `docs\device-plugin-system.md` and `docs\steam-cef-system.md` — do not rename those to
match; write new ones in the dotted style.

Plugin keys are namespaced by the host as `plugin/{scope}/{key}`, so a plugin only needs a name
unique within its own scope.

## Verbosity

Off by default. `Settings → System → Diagnostics → Verbose logging` persists the choice as
`AppConfig.LogVerbosity` and takes effect on the next configuration reload without a restart, which
matters because the process being diagnosed is the shell. `--verbose` sets it for one run and wins
over the stored value.

Raising verbosity must not turn the log into the thing `Debug` exists to prevent. If a verbose log
is unreadable, the fix is a `Change` key or a threshold, not a quieter default.

## The file

One process-wide static, `File.AppendAllText` per line, so a suppressed line costs no I/O at all. 5
MB cap with one `.old` archive kept, checked every 256 KB of writes rather than per line, and
serialized across processes by a named mutex — the shell, Settings and elevated one-shots all append
to the same file. Rotation failure is survivable and never throws; logging must never be the thing
that breaks a session.

`Log` stays uninitialized in tests, and no test may touch `%LOCALAPPDATA%\WSGM`.

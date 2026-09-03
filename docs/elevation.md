# Elevation, de-elevation and the launch wrapper

How WSGM lowers integrity when it must: for Explorer on a fail-open desktop, for Settings pages, for
individual games through `WSGM.Launch`, and for the whole Steam client on request. Why WSGM is
elevated at all and what that buys is in `docs\decisions.md`. The shell anchor that restores
Explorer on a normal desktop transition is in `docs\boot-and-shell.md`.

Related:

- `docs\boot-and-shell.md` — how Explorer is ended and restored, the shell anchor
- `docs\steam-input.md` — the Steam Input lease the wrapper acquires
- `docs\steam-cef.md` — writing a game's launch configuration into the running client

## De-elevation mechanism

### De-elevation is a one-shot scheduled task, not a linked-token launch

The naive route, `TokenLinkedToken` to a primary token, fails with error 1346 because it needs
`SeTcbPrivilege`. The working mechanism is a one-shot scheduled task whose principal has `LogonType`
`InteractiveToken` and no `RunLevel` element, so Task Scheduler runs it with the user's limited
token (`Core\UnelevatedLauncher.cs`, `WSGM.Launch\ScheduledTaskLauncher.cs`). The task XML must be
written as UTF-16. Do not ship `/NoUACCheck`; EDRs flag it.

Windows 11 Explorer usually de-elevates itself. `ExplorerControl` verifies 5 s after a start and
repairs once through the task on blocking terminal recovery paths.

### The task is recovery, not a normal desktop transition

A process the scheduled task starts inherits the Task Scheduler launch owner's job. Desktop
launchers such as Mod Organizer 2 then fail `CREATE_BREAKAWAY_FROM_JOB` with error 5. Normal
game-to-desktop transitions therefore use the medium, jobless shell anchor and verify that the
resulting taskbar owner is current-session, medium, canonical, jobless and stable. The task route is
fail-open recovery only, always logged and surfaced as degraded, even when the Explorer it produced
turns out to be jobless.

The fallback's budget rules: task creation, `/Run`, deletion and shell-readiness observation share
one absolute deadline. Cancellation or a process-wait fault stops an active `schtasks`. An uncertain
`/Create` stays cleanup-eligible while budget remains, and cleanup never gets a fresh timeout. Once
`/Run` began, a timeout or fault is an unknown dispatch rather than a proven failure, so shell
recovery keeps game-mode surfaces retired while a late Explorer may still appear.

### Settings pages open through a medium one-shot

Modern Settings activation uses the same task to run a narrow WSGM one-shot at medium integrity
before opening `ms-settings:`. From the elevated shell a direct `ShellExecute` only works while an
unelevated Explorer happens to broker it. Do not start Explorer just to open the Bluetooth or Wi-Fi
page.

## The launch wrapper: WSGM.Launch

`WSGM.Launch.exe` is the single launch wrapper for Steam games that reject elevation or need a Steam
Input lease. It replaced `WSGM.Deelevate.exe` and `steam-input-lease.exe`, which the installer
deletes on update; a user who pasted one of the old commands has to re-apply the fix.

```text
"...\WSGM.Launch.exe" [--deelevate] [--input-lease | --input-lease-inject] -- %command%
```

At least one flag is required, and the target command always follows `--`.

| Flag                   | Behaviour                                                         |
| ---------------------- | ----------------------------------------------------------------- |
| `--deelevate`          | run the target at medium integrity                                |
| `--input-lease`        | hold a Steam Input lease through the resident shim; never injects |
| `--input-lease-inject` | hold the lease by injecting; the only shipped route that injects  |

### The two lease flags differ only in delivery and are mutually exclusive

The Tools-tab button picks between them from the Steam Input Management setting at apply time
(`LaunchWrapperCommand.ForCurrentInputMode`), so a game's launch option always names the route it
will take. `ModeFor` matches on token boundaries, because
`"--input-lease-inject".Contains("--input-lease")` is true and a plain `Contains` reports both
behaviours at once. Launch options written before the split say `--input-lease`, which now means
shim-only; with Steam Input Management off those games silently stop blocking, so the toggle logs
the affected appids.

### Lifetime, ordering and waiting

The elevated wrapper stays alive for the target's lifetime, preserves Steam's arguments, environment
and working directory, and stops the target tree if Steam terminates the wrapper. Do not replace it
with a fire-and-forget scheduled task or an Explorer-token shortcut.

The lease is the outer behaviour. Its gate injects into an elevated `steam.exe`, which a medium
process cannot do, so the elevated parent acquires the lease before the de-elevation hand-off and
releases it after the medium child reports the target's exit.

Both paths wait on a job object, never on the process they started. The native wrapper starts the
target suspended and assigns before resume; the de-elevated child (`WSGM.Launch\JobObject.cs`)
assigns right after `Process.Start`. A game behind a launcher exits its root process seconds in, and
waiting on that released the lease mid-session and told Steam the game had stopped. The job is also
what lets stop-on-parent-exit reach orphaned descendants.

### Lease failures and impossible de-elevation fail open

A lease failure logs, tells the user and launches anyway. So does an impossible de-elevation: with
UAC off there is no limited token to hand out, so the child tags that failure and the parent
launches the game as-is.

### The de-elevation fail-open is gated on the parent's own token

The handshake pipe grants the user SID (invariant b below), so any same-user process could connect
first and send the failure tag. The parent therefore trusts only its own token.
`Elevation.HasLinkedLimitedToken()` reads `TOKEN_ELEVATION_TYPE`: `Full` means de-elevation is
possible here, so the tag is refused and the wrapper returns 1 instead of launching the game
elevated. Only `Default` (UAC off, built-in Administrator, standard user) and an unqueryable token
fail open, which is the device case the fail-open exists for. Reading the peer's token instead would
race the genuine child, which exits milliseconds after writing. Accepted narrowing: if the medium
child's own token query fails on a UAC-enabled machine, the game does not start at all, where it
used to start elevated. The refusal line names the observed state so a pasted log tells the two
apart.

### A failed release handshake is reported, never retried

An error out of `run_wrapped` means the target never started, because that is what the caller acts
on. A release handshake that fails after the game exited returns the exit code instead; otherwise
the wrapper would start a finished game a second time. The failure is still reported through
`WrappedRun.release` (ABI 3 added the `release` output). Blocking is lifted either way, but Steam
was never asked to rediscover controllers, so `WSGM.Launch` writes
`Steam Input lease released, but Steam controller recovery did not run …` to `launch.log`, the only
surface that failure shows on.

## Four device-verified invariants when Steam is elevated

Each one was a separate real failure (2026-08-12).

### a. The wrapper is a console executable

`<OutputType>Exe</OutputType>`, with a visible CLI window. Steam treats a windowless `WinExe` as a
game and hooks Steam Input into it, and the wrapper dies before it logs. Never make the wrapper
WinExe.

### b. The handshake pipe grants the user SID explicitly

The elevated parent creates its pipe with `NamedPipeServerStreamAcl` and `WindowsIdentity.User`, not
`PipeOptions.CurrentUserOnly`. An elevated server's CurrentUserOnly grants the token owner,
`BUILTIN\Administrators`, which is deny-only in the child's filtered token, so the medium child's
connect fails with "Access is denied". Never reintroduce `CurrentUserOnly` on an elevated-to-medium
pipe.

### c. The medium child launches with `__COMPAT_LAYER=RunAsInvoker`

Without it a target with a RUNASADMIN flag or an admin manifest fails a medium `CreateProcess` with
`ERROR_ELEVATION_REQUIRED` (740).

### d. Non-Steam shortcuts take the wrapper as Target

For a non-Steam (custom) shortcut Steam ignores an exe-replacing `%command%` launch option and runs
the original target anyway. The wrapper goes in the shortcut's Target and the real program in its
Launch Arguments. `Core\SteamLaunchConfig.cs` writes this into the running client so the user never
has to; the mechanism is in `docs\steam-cef.md`.

## Steam client launch integrity

WSGM starts Steam at its own integrity by default, which means elevated in a normal shell session.
WSGM drives the running client over CEF and sends it window messages, and a mismatched pair loses
those messages to UIPI. The cost is that every game Steam launches inherits the elevation.

`AppConfig.SteamLaunchUnelevated` is the user-owned choice between the two. When it is set and WSGM
is elevated, the cold start goes through the same de-elevating scheduled task Explorer uses
(`UnelevatedLauncher`), so the whole client, not an individual game, runs at medium integrity. From
an unelevated WSGM the setting changes nothing, because the ordinary launch already produces a
medium-integrity Steam.

Both the cold start and the auto-relaunch after Steam exits pass through
`SessionModes.StartBigPicture`, so the choice cannot apply to one and not the other. Every launch
logs `Steam launch integrity: …`, including the case where de-elevation was requested but
unavailable, so a pasted log settles which one happened. The scheduled-task route returns no process
handle, so the Steam Input shim startup-trace line is only logged on the integrity-matched path that
has one.

`WSGM.Launch` is unaffected and keeps de-elevating individual games independently.

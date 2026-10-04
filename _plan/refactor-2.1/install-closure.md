# B024 install and U04B source closure

Status: **partial review, not closed**. Baseline: `master` at `0afe97fb`.
This is a read-only source review. No source was edited, no test or build ran, and no Windows
startup, service, shell, installer or hardware action was invoked during this review.

## Coverage and findings

Implementation follow-up: **B180 is implemented.** Exact-filename matching, the task-restore write
and unused UAC field deletion are applied. All 40 autostart cases passed with fakes after
formatting; the full Release solution build had zero warnings/errors. Rider cleanup, Prettier,
guidance and diff checks passed. No live startup/task/UAC action ran. The observations below remain
the baseline review evidence; these three fixes are now applied, but B024's broader review stays open.

The full source bodies of these Core files were reviewed against the baseline:

| File | Disposition |
| --- | --- |
| `src/WSGM/Core/SteamAutostart.cs` | New functional finding INSTALL-C-002 below. |
| `src/WSGM/Core/SteamAutostartTakeover.cs` | UNCOVERED-002 remains unfixed; B016 did not cover its restore branch. |
| `src/WSGM/Core/SteamAutostartService.cs` | Recovery recording is fixed by B016; strict restore loading is already assigned to B095 with B039's config read contract. |
| `src/WSGM/Core/KnownStartupApps.cs` | No change: filename deduplication and root priority are deliberate; empty roots are excluded and this only offers suggestions. |
| `src/WSGM/Core/WindowsPolicyOperation.cs` | No change: acquisition failure disposes the handle, abandoned acquisition owns the mutex, and the two callers use synchronous scopes. No evidence of double disposal or a cross-thread release was found. |
| `src/WSGM/Core/DesktopAppProcessBackend.cs` | Existing MainModule/process identity and integrity failures remain assigned to SESSION-035/CRIT-004 in B112; this review does not close that implementation. |

Supplemental source checks covered `AutostartSystem.IsTaskEnabled`/`ReadTaskEnabled` and
`UacSettings.UacState`. A C# and PowerShell reference search under src, tests and tools found no
consumer of `EnableLua`; the two UAC consumers use `PromptsDisabled`.

### INSTALL-C-002: Steam's unknown-path fallback matches other executable names

- Severity: low, functional startup matching.
- Evidence: `src/WSGM/Core/SteamAutostart.cs:204` accepts an executable whose text ends in
  `steam.exe`; line 212 accepts it immediately when the known Steam path is absent.
  The existing branch therefore classifies `C:\Tools\notsteam.exe -start` as Steam when
  `steamExePath` is null. This is a source-derived case, not an executed reproduction.
- Effect: the takeover can disable an unrelated startup entry.
- Fix: require the executable's filename to equal `steam.exe`, ignoring case, then retain the
  existing full-path comparison when Steam's path is known. Preserve command-line parsing and
  surface isolation.
- Acceptance: fake Run, shortcut and task sources with suffix-only names are excluded; exact
  `steam.exe` still matches with an unknown path; known-path matching remains unchanged.
- Owner: B180, placed before B030.

### UNCOVERED-002: existing finding omitted from B016's applied work

`findings/crosscutting.md` already describes this defect. The current
`SteamAutostartTakeover.Restore` branch at lines 139-144 treats `IsTaskEnabled` as proof that the
user re-enabled the task and drops the record. `AutostartSystem.cs:119` maps an unreadable task
state to true. B016 fixed recording and confirming reads after disable, but did not remove this
restore pre-read.

The existing remedy is unchanged: attempt `SetTaskEnabled(entry.Location, true)` directly, drop
the record only on accepted dispatch, and keep it on failure. No retry, new recovery state or
readback is needed. Fake tests must show that an unreadable/throwing task query is never consulted
and a refused enabling write keeps the record. Owner: B180 as a B016 follow-up.

### U04B-LFA-041: unused UAC enablement snapshot

`UacSettings.cs:23,45,146,159` retains a registry read, constructor argument and property for
`EnableLua`, with no consumer in the searched source. Delete those declarations and adjust the
three local constructor calls; preserve consent/secure-desktop values and `PromptsDisabled`.
This is the concrete disposition requested by the surviving cross-reference. Owner: B180.

## Unwritten identifiers

The consolidated install findings establish that the following identifiers have no surviving
body or known subject. Each is retired for that reason, not declared to be a proven non-defect:

| Identifier | Disposition |
| --- | --- |
| INSTALL-004 | Retired: no surviving body or subject. |
| INSTALL-011 | Retired: no surviving body or subject. |
| INSTALL-012 | Retired: no surviving body or subject. |
| INSTALL-013 | Retired: no surviving body or subject. |
| INSTALL-014 | Retired: no surviving body or subject. |
| INSTALL-018 | Retired: no surviving body or subject. |
| INSTALL-019 | Retired: no surviving body or subject. |
| INSTALL-021 | Retired: no surviving body or subject. |
| INSTALL-022 | Retired: no surviving body or subject. |
| INSTALL-023 | Retired: no surviving body or subject. |
| INSTALL-024 | Retired: no surviving body or subject. |
| INSTALL-025 | Retired: no surviving body or subject. |
| INSTALL-028 | Retired: no surviving body or subject. |
| INSTALL-029 | Retired: no surviving body or subject. |
| INSTALL-030 | Retired: no surviving body or subject. |
| INSTALL-031 | Retired: no surviving body or subject. |
| INSTALL-034 | Retired: no surviving body or subject. |
| INSTALL-035 | Retired: no surviving body or subject. |
| INSTALL-036 | Retired: no surviving body or subject. |
| INSTALL-037 | Retired: no surviving body or subject. |
| INSTALL-038 | Retired: no surviving body or subject. |
| INSTALL-039 | Retired: no surviving body or subject. |
| INSTALL-040 | Retired: no surviving body or subject. |
| INSTALL-041 | Retired: no surviving body or subject. |
| INSTALL-042 | Retired: no surviving body or subject. |
| INSTALL-043 | Retired: no surviving body or subject. |
| INSTALL-044 | Retired: no surviving body or subject. |
| INSTALL-045 | Retired: no surviving body or subject. |
| INSTALL-046 | Retired: no surviving body or subject. |

U04B-LFA-013 through 049 also lack saved bodies. Their surviving cross-references remain mapped
in `findings/ledger-u04.md`; this partial review confirms the Core startup concerns above and
gives 041 a concrete disposition. It does not yet close the remaining per-id source coverage.

## Remaining B024 work

- Finish the five-project pass over the 73 tracked C# files in WSGM.Install, WSGM.Launch,
  WSGM.LogonService, WSGM.PackagedLaunch and WSGM.Setup, including their linked contract sources.
  Selected WSGM.Install files were read in this turn, but no whole-project completion is claimed.
- Finish the individual U04B-LFA-013 through 049 dispositions against that pass and the existing
  session/install findings. Keep missing-body uncertainty explicit.
- Reconcile any further actual defect into a bounded batch before B030. Security-only concerns
  remain no-change under DECISIONS.md; the decided identity refusal, stop flag and task-XML
  placement are not reopened.
- B024 stays open until that coverage is complete. B180's Core fixes have verified inputs and
  may proceed independently; B030 still waits for the full closure.

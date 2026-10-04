# WSGM 2.1.0 refactor findings

## 1. What this is

This folder preserves the whole-codebase review at `master` 1329813f, including verified corrections and
missed findings. The defects and evidence remain useful. The proposed solutions are historical recommendations,
not mandatory architecture: named owners, interfaces, file splits and test machinery must justify themselves
against a simpler direct fix.

The simplified [plan](../refactor-plan-v2.md) governs implementation. Current maintainer instructions win,
then [DECISIONS.md](../DECISIONS.md), then that plan. [batches.json](../batches.json) is an index of all 188
existing item IDs, not a serial task graph. [requirements.md](../requirements.md) preserves earlier requirements;
current direct-work and end-only-validation instructions supersede its older process permissions.

Every section names its batch on a `**Plan v2:**` line, so `rg -n "B0NN" _plan/refactor-2.1/findings` finds the work of one
batch. Line numbers come from the reviews and drift; anchor every edit by symbol.

[`ALL-FINDINGS.md`](ALL-FINDINGS.md) in this folder is the concatenation of every area file in the order of the table
below. Edit the area files and regenerate it; never edit it directly.

Older inputs the findings cite by name (the Codex `refactor-plan.md`, `planning-corrections*.md`,
`claude-findings-disposition.md` and `audit/` folders) were not copied. They stay in the local, gitignored
`.codex/plans/e5b4a35dcad6414dbb52e0115d05a815/` folder. Everything needed to implement is in this folder:
`../review/` holds the raw domain reviews and their verifications.

## 2. Files

Counts are the `- **Severity:**` lines in each file (one per written-up finding). No-change counts the entries each
file lists under "Refuted or no-change"; the parenthesis names entries that are not whole ids.

| File | Area | Critical | High | Medium | Low | Nit | Total | No-change |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| [session.md](session.md) | Startup and exit, `ShellSession`, Game Mode transitions, Explorer, tray, splash | 1 | 5 | 16 | 30 | 12 | 64 | 5 |
| [config.md](config.md) | Configuration store, recovery snapshots, sidecars, logging | 0 | 2 | 10 | 25 | 12 | 49 | 5 |
| [device.md](device.md) | Device coordinator, capability router, AutoTDP, power ports | 0 | 3 | 8 | 30 | 6 | 47 | 0 (7 refuted sub-claims) |
| [input.md](input.md) | Controller pipeline, HidHide, VIIPER backend, chords, recorders | 0 | 2 | 15 | 17 | 7 | 41 | 4 |
| [steamhost.md](steamhost.md) | Steam UI host, NativeQam services, themes, animations, asset catalog | 0 | 0 | 17 | 31 | 10 | 58 | 1 |
| [library.md](library.md) | Game library, sources, import, artwork, launch commands | 0 | 1 | 9 | 22 | 11 | 43 | 4 |
| [winsvc.md](winsvc.md) | Windows services (radios, storage, power, policies) on the WSGM side | 0 | 0 | 11 | 32 | 9 | 52 | 4 |
| [overlay.md](overlay.md) | Overlay windows, navigation, Quick Access sheet | 0 | 2 | 21 | 23 | 5 | 51 | 4 |
| [settings.md](settings.md) | Settings UI, themes, LiveBackdrop | 0 | 1 | 10 | 23 | 11 | 45 | 5 |
| [wdc.md](wdc.md) | WindowsDeviceControl child and its consumers | 0 | 0 | 5 | 16 | 11 | 32 | 4 (+5 refuted sub-claims) |
| [toolkitcs.md](toolkitcs.md) | SteamUiToolkit C# | 0 | 2 | 17 | 40 | 14 | 73 | 2 |
| [toolkitjs.md](toolkitjs.md) | SteamUiToolkit injected script, build and check scripts | 0 | 0 | 8 | 25 | 10 | 43 | 4 |
| [sdk.md](sdk.md) | Device SDK, Plugin SDK, plugin host, packers | 0 | 1 | 16 | 21 | 11 | 49 | 7 |
| [packages.md](packages.md) | Device packages (MSI Claw, ROG Ally) | 0 | 2 | 14 | 19 | 3 | 38 | 8 (+1 refuted plan claim) |
| [gpuir.md](gpuir.md) | GPU plugins, shared GPU runtime, IR plugin and firmware | 0 | 2 | 13 | 21 | 3 | 39 | 9 |
| [labui.md](labui.md) | Device Lab GUI, wizard, evidence authoring | 0 | 2 | 9 | 15 | 7 | 33 | 7 |
| [labcore.md](labcore.md) | Device Lab core and worker | 0 | 1 | 10 | 22 | 4 | 37 | 9 |
| [build.md](build.md) | Project graph, eng scripts, CI, tools, test infrastructure | 0 | 0 | 9 | 19 | 7 | 35 | 8 |
| [install.md](install.md) | Setup, WSGM.Launch, logon service, packaged launch | 0 | 0 | 5 | 10 | 4 | 19 | 7 (+29 unwritten ids) |
| [crosscutting.md](crosscutting.md) | USER-001, critic CRIT items, 27 conflicts, GAP items, uncovered sources | 0 | 9 | 23 | 20 | 6 | 58 | 1 (+10 partial) |
| [ledger-u01.md](ledger-u01.md) | First audit ledger U01 (WDC) | 0 | 0 | 9 | 35 | 38 | 82 | 4 |
| [ledger-u02.md](ledger-u02.md) | First audit ledger U02A/U02B (toolkit C#) | 0 | 0 | 12 | 44 | 42 | 98 | 11 |
| [ledger-u03.md](ledger-u03.md) | First audit ledger U03A/U03B (toolkit scripts and surfaces) | 0 | 0 | 12 | 32 | 27 | 71 | 8 |
| [ledger-u04.md](ledger-u04.md) | First audit ledger U04A/U04B (lifecycle) | 0 | 1 | 13 | 24 | 6 | 44 | 4 (+37 missing bodies) |
| [ledger-u05.md](ledger-u05.md) | First audit ledger U05 (shell session lifecycle) | 0 | 4 | 7 | 14 | 4 | 29 | 1 |
| [ledger-codex.md](ledger-codex.md) | Codex audits A01, A02, A02S01 | 0 | 3 | 21 | 15 | 0 | 39 | 3 |
| **Total** | | **1** | **43** | **320** | **625** | **280** | **1269** | **129** |

The 906 sections in the domain files and `crosscutting.md` are the distinct work. Most of the 363 ledger sections are
short pointers to the domain finding that carries the solution; only the ledger sections without a covering domain
finding carry a full fix. Treat the totals as sections, not as 1269 separate defects. Some no-change ids appear in two
files (the `wdc.md` U01 ids also sit in `ledger-u01.md`), so the no-change total counts list entries, not distinct ids.

## 3. How to work

- Work directly on master in coherent groups of related fixes. Respect actual code dependencies, not the old
  blanket batch chains. Read only the relevant findings and verify corrections; anchor edits by symbol.
- Fix existing code first. Extract only to remove real duplication or confused ownership. Proposed class names
  and ports are optional; a long file alone does not require a redesign.
- Keep every finding accounted for. Group small corrections into the affected work; do not create another
  plan/reviewer/fixture/report cycle for each one. B026 remains no-change by maintainer decision.
- Update [the tracker](../../implementation-todo.md) and plan statuses as source changes land. Source applied,
  automated validation and manual/hardware acceptance are separate claims.
- The latest task instruction defers builds, publishes, tests, Rider cleanup and gates until implementation
  is finished. A running application blocks file replacement. Prettier formatting runs
  before committing its files. Pushes wait too because they start CI. Publish children before parent gitlinks
  at final delivery. No extra agents, live actions or releases are authorized by these review files.

## 4. Decisions (all answered 2026-10-03)

The maintainer answered every open question; [`../DECISIONS.md`](../DECISIONS.md) is binding and overrides plan v2's
former recommended answers, the review solutions and any batch spec written before it. The area files, plan v2
section 4 and `batches.json` already carry the answers. Nothing is open.

### D1 to D14

| # | Question | Decided | Batches | Findings |
| --- | --- | --- | --- | --- |
| D1 | Shutdown design | Safety-first ordered steps under one deadline; the B3 percentage cutoffs and preliminary drain are dropped | B140 | SESSION-010, -019, -030, -033, CONFLICT-01, DEVICE-003, STEAMHOST-001, CONFIG-018, -019 |
| D2 | Byte bounds on untrusted input | Exactly the plan v2 list stays and refuses, never truncates; every other count or length cap is removed | B138, B147, every cap removal | SDK-007 to -010, SDK-012, CRIT-002, LIBRARY-016, -018, -021, -032, GPUIR-031, LABUI-014 |
| D3 | MIT Device Lab compiles GPL interop files | **Device Lab becomes GPL**: licence file, csproj metadata, README, AGENTS and notices change; the interop files are neither relicensed nor duplicated; the SDKs stay MIT | B172, B173, B177 | A02-F022, LABCORE-039, -040, LIBRARY-025, BUILD-019, INPUT-032, -033, CONFLICT-22 |
| D4 | Guidance (AGENTS.md) edits | Approved; each diff is shown with its batch and applied | B022, B023, B064, B072, B143, B145, B173, B175, B176, B177 | PACKAGES-001, -006, -008, -011, -017, WDC-022, -026, U01-015, U01-016, BUILD-028, -030, A02-F010 |
| D5 | Title resolving to a shortcut another title owns | Offered as a second game shortcut with its own Add and its own Steam shortcut | B106 | LIBRARY-002, LIBRARY-V-001 |
| D6 | PL2 home when a layer holds both values | **Keep the device-stored PL2** and drop `BoostWatts`; the device entry is the only home of PL2 | B089 | DEVICE-008, UNCOVERED-010 |
| D7 | Ally motion sensor order | **WinRT first, like HC** (today's order) | B023 | PACKAGES-011 (now no-change) |
| D8 | Ally power write order | **SPL, then SPPT and FPPT, as HC does**; the SPL <= SPPT <= FPPT stepping rule goes; write-through, no gating | B022, B177 | PACKAGES-005, -007, PACKAGES-V-003, SDK-V-002 |
| D9 | Ally controller entry re-arm | **No readback machinery for any vendor except the Claw.** Unresolved or uncertain entries, re-arm rules and recovery entries that wait on a readback leave every non-Claw package and every host path. A write either dispatched (published as written) or failed to dispatch | B016, B017, B022, B023, B067, B069, B072, B099, B143, B152, B177 | PACKAGES-001, -008, A02-F010, CONFLICT-19, SDK-017, SDK-V-002 |
| D10 | Steam-side cleanup at exit, uninstall and upgrade | Restore the files (boot-movie override and `.wsgm-original`, the `themes_custom` junction) **and hand back Steam's startup-movie choice** at WSGM exit when no WSGM movie is chosen, and at uninstall | B138, B140 | STEAMHOST-015, SETTINGS-042, U02B-SUTC-011 |
| D11 | IR catalogs over the 32 KiB frame | **Catalog paging now**: chunked `remotes` replies with a protocol version bump on firmware and host; no build-time size refusal | B156 | GPUIR-031, GPUIR-004, A02S01-F001 to F014, DEVICE-042 |
| D12 | tools/DeckSpike and tools/SteamReceiver | Delete both, plus `InternalsVisibleTo("DeckSpike")` | B174 | BUILD-003, BUILD-017, BUILD-V-005 |
| D13 | Library badge before the first publication | **The badge shows the library's name**; the toolkit holds no product label and the host supplies the text | B057 | TOOLKITCS-047, TOOLKITJS-025, U03B-SUTS-016, STEAMHOST-V-010 |
| D14 | Setup single-file native extraction to temp | Dropped as security theater; no change | none | INSTALL-V-001, GAP-03 |

### Security hardening: all dropped

Every security-hardening finding is security theater by maintainer decision ("It's a launcher, it has to launch").
Such findings sit under "Refuted or no-change" with the reason "Dropped by maintainer decision (security theater,
DECISIONS.md)": the elevated `boot.json` launch (INSTALL-001), installers run from the user's temp (INSTALL-002, B026
removed), the `%ProgramData%\WSGM` DACL (INSTALL-003), task XML staging (INSTALL-008, U04B-LFA-002), single-file
extraction (INSTALL-V-001), the overlay broker's forwarded access (INSTALL-010), the tray relay refusal (SESSION-048,
U05-LFB-016), bridge `hostId`, nonce, sender, ownership or integrity checks (CONFLICT-06, U02A-SUTC-004, -016, -024),
DLL search-path attributes (TOOLKITCS-010, LIBRARY-033, GPUIR-034, U02A-SUTC-052), reparse refusals (U02A-SUTC-061,
LABCORE-018), the dev-deploy swap script (BUILD-035) and pinned CI action tags (BUILD-009). Only functional fixes stay:
the clean service stop and token seam (B025), the updater's partial-file cleanup with the download left where it is
(B027), answers applied last and truthful partial-change reporting (B028), the task cleanup (B029) and exact component
matching (B030).

### Other answers

| Topic | Decided | Batches | Findings |
| --- | --- | --- | --- |
| Tray relay in Game Mode | Today's relay stays unchanged | B114 | SESSION-048, U05-LFB-016 |
| `config.json` from a newer WSGM or with unknown recovery enums | Loads best effort as today; no read-only mode | B039, B068, B133 | config area |
| Overlay display-mode selector | Targets the display the overlay sheet is shown on, not `paths[0]` | B130 | OVERLAY-009 |
| Add Steam Library and Replace launch action pickers | Native Windows pickers stay, navigation suspended while open | B108 | overlay Q2 |
| Application-profile Name and Process fields | Controller-reachable press-to-edit rows | B129 | OVERLAY-031 |
| Escape and the 3 s timeout during shortcut capture | Keep the existing binding; only an explicit Clear clears it | B080, B135 | INPUT-025 |
| SD-card library marker watcher | Keeps watching every ready drive; the comment is fixed | B094 | WINSVC-016 |
| Plugin Steam UI modules | Register when the plugin becomes ready, leave when it stops | B054, B086 | STEAMHOST-006, CONFLICT-05 |
| Overlay sheet idle-timeout and Windows-policy reads | Off the UI thread; values fill in a frame later | B130 | OVERLAY-043 |
| RTSS | Starts with WSGM and is kept alive (restart on exit, no cooldown), not launched when RTSS integration is off, never killed | B005 | USER-001 |
| Modern Standby wake-device line | The 16-device cap goes; the row may wrap | B135 | SETTINGS-019 |
| File picker | No paging | B059 | M01-31 |

## 5. Known gaps

- **U04B-LFA-013 to U04B-LFA-049 (37 ids)** have no saved title, location or claim; only their severity bands and a few
  cross-references survive (`ledger-u04.md`, "Missing bodies"). Do not invent claims. B024 re-reviews the U04B files
  read-only and records security-only items as dropped by maintainer decision. Group any real functional correction
  with the affected implementation; current closure progress is in the tracker.
- **29 install review ids** (INSTALL-004, 011 to 014, 018, 019, 021 to 025, 028 to 031, 034 to 046) were assigned but
  never written, and nothing names their subject. They are retired; B024 records new findings as INSTALL-C-002
  onward. `install.md` was written by the solution checker from the review, its verification and DECISIONS.md, not by
  the domain reviewer.
- **Historical solutions may be oversized.** The simplified plan and JSON index replace the old executable specs.
  Keep the demonstrated defect and verification corrections, then choose the smallest maintainable fix.
- **Residual notes, not open questions.** `install.md` still lists the INSTALL-005 interactive-user refusal (B030) for
  the maintainer under its remaining concerns; plan v2 treats it as decided, so it proceeds. PACKAGES-032 (B145) keeps
  the reference unit's row as the fallback for an unreadable Claw MCU revision. SETTINGS-V-005 (B135) opens About links
  from the elevated process through the `--open-link=` one-shot.
- **Line numbers** in the reviews were often wrong; several files say so explicitly. Anchor by symbol.
- **Attended evidence, not decisions** (plan v2 section 4): the config mutex ACL check (CONFIG-V-004), one real Store
  catalog answer (LIBRARY-V-004), the store-title icon cache (LIBRARY-034), the toolkit fingerprint live check (B061),
  and a log of a lingering `steamwebhelper` before any `Steam.IsRunning` change (SESSION-054). The Ally package still
  awaits Device Lab evidence, and the IR firmware fixes and catalog paging (B156) need a maintainer flash before M01-37
  and M01-38.
- **Counts are sections.** Ledger sections mostly point to a covering domain finding, so the distinct defects number
  fewer than 1269.

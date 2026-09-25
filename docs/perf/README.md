# Performance

What WSGM costs while it runs next to a game on a handheld, measured rather than guessed. This page
holds the scenarios, the recorded numbers, where the idle cost goes, and the budgets. The recording
and analysis tool is [tools/PerfLab](../../tools/PerfLab/README.md); the tracking issue is #121.

Every number here is dated evidence from one machine and one build. Re-record before trusting a
figure after a change to the controller path, the device plugin runtime, VIIPER or the overlay.

## Scenarios

Each scenario is recorded on battery and on AC, thirty seconds at idle and sixty seconds in game.
The Claw is the reference machine.

| Scenario                  | Setup                                                             | Recorded          |
| ------------------------- | ----------------------------------------------------------------- | ----------------- |
| `idle-desktop`            | Desktop Mode, Steam running, overlay closed, controller untouched | 2026-09-26 (AC)   |
| `idle-game-mode`          | Game Mode, Steam Big Picture at the library, nothing launched     | pending, attended |
| `in-game-overlay-closed`  | A game running, overlay closed                                    | pending, attended |
| `in-game-overlay-open`    | The same game, Quick Access sheet open                            | pending, attended |
| `controller-active`       | In game, sticks and gyro moving                                   | pending, attended |
| `controller-motion-still` | In game, controller held still                                    | pending, attended |
| `standby-resume`          | Enter standby, resume, thirty seconds after resume                | pending, attended |

## Baseline: idle desktop, 2026-09-26

Build 2.0.0.1354 on the Claw 8 AI+ A2VM, Windows 10.0.26200, on AC at 72 %, WSGM up 34 minutes,
device integration and controller management on, Steam in Desktop Mode, overlay closed, no game.
Recorded with `perf-capture.ps1 -Scenario idle-desktop -Seconds 30`; the report is in
[captures/20260926-001944-idle-desktop](captures/20260926-001944-idle-desktop/report.md).

CPU is the share of one core from ETW sampling over the 53.9 s sample span. Wakeups are context
switches into the process's threads per second. Memory and handles come from the process counters.

| Process              | CPU % of one core | Wakeups/s | Private MB | Handles | Threads |
| -------------------- | ----------------: | --------: | ---------: | ------: | ------: |
| WSGM.exe             |              12.3 |     2,454 |         97 |   1,388 |      64 |
| WUDFHost.exe         |               5.6 |     1,185 |            |         |         |
| steam.exe            |               1.0 |       146 |        129 |   1,046 |      39 |
| steamwebhelper (all) |               1.8 |       100 |         48 |   1,386 |      34 |
| WmiPrvSE.exe         |               1.7 |        70 |            |         |         |
| LHMDataProvider.exe  |               0.3 |        25 |         17 |     317 |       4 |
| WSGM.LogonService    |               0.1 |         2 |          5 |     229 |       6 |
| RTSS.exe             |               0.0 |        11 |          3 |     385 |       5 |

WSGM was the busiest process on the machine apart from the recording itself, ahead of the idle loop.
The WUDFHost.exe row is the Intel sensor driver host: its whole cost is WSGM's motion source polling
it. The WmiPrvSE row is the provider answering WSGM's queries; this run's capture script still
polled WMI once a second with a per-thread query, which put a second provider host at a third of a
core and is why the script now samples every five seconds without that query. A hand-recorded trace
an hour earlier, without that load, put WmiPrvSE at 0.8 % of a core across three hosts and WSGM at
11.6 % with 2,231 wakeups/s, so the WSGM figures are stable.

WSGM.exe runtime counters over the same window: 488 thread-pool work items per second, 555 KB/s
allocated, a gen0 collection every 15 s, no gen1 or gen2, 13 active timers, 0.13 s of CPU per second
of which three quarters is kernel time, and one `WaitHandleCannotBeOpenedException` thrown every
second.

### Where WSGM's idle cost goes

Ranked by measured share of WSGM.exe's 6.6 s of CPU and 132,000 wakeups over the 53.9 s sample span.

1. **VIIPER, about 40 % of the CPU and 55 % of the wakeups.** `libviiper.dll` is a Go c-shared
   library, so its threads mostly have no walkable stack; the frames that do resolve are the Go
   runtime parking and unparking goroutines (`WaitForSingleObject`, `GetQueuedCompletionStatusEx`,
   `SetWaitableTimer`). One thread alone took 35,800 wakeups. The work behind it is the USB/IP
   server completing interrupt-IN URBs for the Steam Deck controller endpoint over TCP loopback:
   every 6 ms (`bInterval`) it replays the last report when no fresh input arrived, and every
   physical pad report (about 125 Hz) completes one more. In Desktop Mode with nothing consuming the
   virtual controller this is pure overhead.
2. **The managed per-sample pipeline, about 35 % of the CPU and 25 % of the wakeups.** Each pad
   report travels HID overlapped read, I/O completion port, plugin publish, `ControllerManager`
   batch and drain worker, router, `SubmitUnderGate`. That is three or four thread-pool hops per
   sample, 488 work items per second, spread over five pool workers at 130 to 180 wakeups/s each.
   8,600 of the wakeups are the pool's own spin-then-sleep in `LowLevelLifoSemaphore.Wait`.
3. **Motion polling, about 12 % of WSGM's CPU plus the whole WUDFHost.exe row.** The Claw motion
   source polls the Sensor API every 2 ms on a long-running task thread (761 ms) and each `GetData`
   is a device I/O control into the driver host (3,040 ms there). The gyrometer reports at 100 Hz,
   so four of every five polls return a duplicate. Nothing consumes the stream on the desktop.
4. **Device enumeration bursts on a native pool thread, about 17 % of the CPU.** A Windows
   thread-pool thread ran 1,104 ms in 134 stretches with no walkable stack; the frames that did
   resolve are cfgmgr32 RPC completions for `Windows.Devices.Enumeration` `FindAllAsync`.
   `WindowsRadio.ConnectedBluetoothCount` (76 ms per call) and
   `AudioProfileService.ReadPlaybackCapabilities` are the managed callers seen in the trace. The
   audio manager's one-second timer runs from shell start, and the system status and radio timers
   run from the overlay window's creation on, whether the sheet is visible or not.
5. **WMI, about 1 % in process plus the WmiPrvSE rows.** The identity reads seen in the trace
   (`WindowsClawIdentityReader`, `MsiWmiPlatform`) belong to capability commands: every desktop
   foreground change resolved the same power preset again and replayed it as a full command
   sequence, identity read, scenario and watt writes with readbacks, Windows power mode set. The
   plugin's own ten-second observation cycle reads only power, charge, fans and telemetry.
6. **One exception per second.** `WaitHandleCannotBeOpenedException` is thrown every second and
   swallowed by `LhmSensorReader.TryReadXml`, which opened the provider's
   `Global\Access_LHMDPSharedMemory` mutex on every read although the installed provider never
   creates it.

Memory: 97 MB private, 240 to 300 MB working set, 14 MB in the large object heap. Not yet broken
down.

## Budgets

Proposed for the maintainer's decision; not agreed yet.

| Scenario                 | WSGM.exe CPU | WSGM.exe wakeups/s | Induced (WUDFHost, WmiPrvSE) |
| ------------------------ | -----------: | -----------------: | ---------------------------: |
| `idle-desktop`           |        ≤ 1 % |              ≤ 200 |                      ≤ 0.2 % |
| `idle-game-mode`         |        ≤ 2 % |              ≤ 400 |                      ≤ 0.2 % |
| `in-game-overlay-closed` |        ≤ 3 % |              ≤ 600 |                      ≤ 0.5 % |

The in-game budget excludes the virtual controller's own report completions while a game reads it;
those are the product working.

## Findings and their status

| Finding                                                  | Cost at idle                                         | Status                                                                                                                                                                                                       |
| -------------------------------------------------------- | ---------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| VIIPER keepalive replay and completions with no consumer | 40 % CPU, 55 % wakeups                               | partly fixed: an unchanged frame is no longer submitted, so an untouched pad costs no cgo call or Go wakeup per report; the 6 ms replay needs a VIIPER pprof and an attended check of NAK-idle against Steam |
| Per-sample thread-pool hops                              | 35 % CPU, 25 % wakeups                               | open; the pool's spin-then-sleep is off since the runtimeconfig change, the hops remain                                                                                                                      |
| Power preset replayed on every foreground change         | WMI writes and readbacks per window switch, WmiPrvSE | fixed: the same assignment resolving for another application is not re-applied while the device still shows it                                                                                               |
| Steam running-application poll every 2 s over CEF        | part of the steamwebhelper 1.8 %                     | open: `RunningApplicationTarget` polls the client; Steam's app lifetime notifications would make it event-driven                                                                                             |
| Motion polled at 2 ms with nobody reading it             | 12 % CPU + WUDFHost 5 % of a core                    | fixed on the desktop: the motion demand signal stops the source (see device-integration.md); the 2 ms poll in game is still open                                                                             |
| WinRT Bluetooth enumeration every 2 s at idle            | 17 % CPU                                             | fixed: the radio timer stops when the overlay closes; the audio manager's one-second poll is still open                                                                                                      |
| Identity and AC state over WMI every 10 s                | 1 % CPU + WmiPrvSE                                   | open                                                                                                                                                                                                         |
| `WaitHandleCannotBeOpenedException` every second         | negligible CPU, one throw/s                          | fixed: `LhmSensorReader` opened the provider's missing mutex on every read; it now tries once per mapping                                                                                                    |
| Every publication re-read on each 10 s network poll      | about 1 % CPU                                        | open: the toolkit's publish loop reads every module's state whenever any module signals                                                                                                                      |

Fixed means changed in code and not yet re-measured; a row moves to done when a new capture shows
the cost gone.

## Recording a run

    .\tools\PerfLab\perf-capture.ps1 -Scenario idle-desktop -Seconds 30

Copy `report.md`, `counters-summary.txt` and `system.json` from the run directory into
`captures/<stamp>-<scenario>/` and add the row above. The trace itself is a gigabyte and stays
local.

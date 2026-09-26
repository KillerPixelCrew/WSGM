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
   run from the overlay window's creation on, whether the sheet is visible or not. Every later
   capture kept an unnamed thread of the same shape, about 900 ms per 45 s with one wakeup a second,
   after the radio timer was gone: that one is SDL's `SDL_joystick` thread polling the XInput slots
   every 300 ms. After a resume on 2026-09-26 the same thread looped at a full core, Task Manager's
   13 %, until the overlay opened and WSGM pumped SDL on the UI thread. WSGM now starts SDL without
   the joystick thread.
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

## After the first fix round: idle desktop, 2026-09-26

The branch's first eight commits, deployed and recorded the same way eight minutes after a restart,
with Task Manager and a remote-desktop session open on the machine and the motion setting at its
default, so the sensor stream still ran. Report in
[captures/20260926-012326-idle-desktop](captures/20260926-012326-idle-desktop/report.md).

| Process      | Before: CPU % | Before: wakeups/s | After: CPU % | After: wakeups/s |
| ------------ | ------------: | ----------------: | -----------: | ---------------: |
| WSGM.exe     |          12.3 |             2,454 |         10.3 |            1,838 |
| WUDFHost.exe |           5.6 |             1,185 |          2.6 |              496 |

Thread-pool work items fell from 488 to 377 per second. The remaining cost is what the log says it
is: with the stream on, every frame carries sensor noise, so no frame equals the last and each one
still reaches VIIPER; the VIIPER thread alone still takes 700 wakeups/s. A run with the stream off
could not be taken that night: the "only in game" mode never switched it off because the profile
layers report the foreground desktop window as the running application. That mode has since been
replaced by "Motion only on request", which follows the consumer's own IMU-mode write to the Steam
Deck target (see device-integration.md), so an idle desktop with no gyro layout has the stream off
by default. That run, and the in-game scenarios, are the next captures.

## Inside VIIPER: what one controller report costs

The trace above could only say that `libviiper.dll` was 40 % of WSGM's idle CPU and 55 % of its
wakeups. It is a Go c-shared library with no PDB, so its threads show as "(no stack)" and the frames
that do resolve are the Go scheduler. Measuring inside it needed its own harness:
`internal/server/usb/urbcycle_bench_test.go` in the submodule runs the real USB/IP server with the
Steam Deck device and drives it from a second process that behaves like usbip-win2, then reports
process cycles, thread context switches and heap allocations per completed URB.

    cd external\viiper
    $env:VIIPER_URB_BENCH = "1"; go test .\internal\server\usb -run TestURBCycleCost -v

The cost was the emulation's own heartbeat. A Steam Deck controller endpoint declares a 6 ms
`bInterval`, and every 6 ms with no fresh input the server built two contexts, called into the
device, let it allocate a report from unchanged state, copied that into a replay cache and wrote it
to the loopback socket. Nothing consumed any of it while the pad sat untouched on a desk.

Measured on the Claw, an idle Steam Deck endpoint with no input at all:

| Configuration                             | Completions/s | Mcycles/s | Context switches/s | Allocations per completion |
| ----------------------------------------- | ------------: | --------: | -----------------: | -------------------------: |
| Before: 4 Ps, repeat every 6 ms           |           146 |       193 |              1,548 |                         23 |
| Allocation-free completions, 1 P          |           146 |       107 |                678 |                          1 |
| Plus a 64 ms idle repeat                  |          15.5 |        14 |                 78 |                          1 |
| **Shipping: allocation-free, 4 Ps, 6 ms** |       **146** |   **107** |            **678** |                      **1** |

Only the allocation-free completion path shipped. **The 64 ms idle repeat made the gyro stutter on
left and right movement** — reported on a ROG Ally, reproduced on the Claw, and pinned by a
device-level A/B against live Steam: 6 ms smooth, 64 ms stuttering, nothing else changed
(2026-09-26). It now defaults to 0, and the single-P pin went with it after the same A/B cleared
`GOMAXPROCS`. `VIIPER_IDLE_KEEPALIVE_INTERVAL` still buys the idle saving for anyone who measures it
against the consumer that reads the reports.

These are figures for the VIIPER process in isolation, on one machine, against a test client. A WSGM
capture that shows the same reduction in `WSGM.exe` has not been recorded yet.

**The lesson is about the measurement, not the setting.** A harness driving the real server with a
100 Hz input stream saw the same completion cadence at both repeat intervals and at every
`GOMAXPROCS` — 67 completions and a 10.5 ms longest gap per 400 ms — and it was wrong. Per-URB
allocation and cycle counts against a test client say nothing about what a real host and Steam do
with a slowed report stream. Anything a consumer could notice gets an A/B on the device.

## The gyro microstutter

Reverting the idle repeat did not end the stutter. It was measured the same day by reading the
virtual Deck's interrupt reports next to Steam with a raw HID handle, which is what Steam receives,
timestamping each one, and moving the Claw slowly left and right with a gyro layout active.

**VIIPER completed polls when input arrived, not on the endpoint's grid.** The worker paced each URB
to the 6 ms `bInterval` and then waited up to another 6 ms for fresh input, so with the Claw's gyro
arriving every 8 ms the stream ran on the sensor's cadence: 60 % of the gaps were 8 ms, and every
third sample the host got a held report followed by a fresh one 0.1 to 0.5 ms later, the held one
carrying the packet number it had already seen. A quarter of all 4,999 reports in 30 s arrived under
3 ms after the previous one, and 1,256 repeated the previous packet number. Before the perf work the
pad's unconditional 125 Hz submissions kept a signal pending at nearly every poll, so the
completions happened to land on the grid and the design flaw stayed hidden; skipping unchanged
frames exposed it, and the 64 ms idle repeat had widened the same gaps. Steam integrates the Deck's
gyro per report (SDL's Deck driver does the same, advancing its sensor clock a fixed step per
packet), so an uneven report stream is uneven motion. A paced endpoint now completes at its poll
time with whatever state it has, the way a host reads real hardware, and the Deck device advances
its packet number once per report sent, as the firmware does. The regression test is
`TestCompletionsStayOnTheEndpointGrid` in `internal/server/usb`.

The same capture on the fixed build, 30 s of slow panning: 5,000 reports, 97 % of the gaps between
5.5 and 6.5 ms, none under 3 ms, no report repeating a packet number, and 125 distinct gyro values a
second. The maintainer reported the panning a lot better before seeing the numbers.

**The Claw accelerometer was checked and cleared.** The stutter showed on panning and not on
vertical movement, and Steam's default Player Space gyro weights yaw and roll by a gravity vector it
takes from the accelerometer while pitch uses none, so the accelerometer's pairing with the
gyrometer's 10 ms interval (instead of its 2 ms driver minimum) was the suspect. The captures say
otherwise: the accelerometer value changes about 80 times a second at either request, in alternating
8 and 16 ms steps. Asking for 2 ms was worse: its five hundred callbacks a second cost the
gyrometer, whose distinct values fell from 125 to 76 a second on that build. The pairing stays. Why
the cadence defect read as horizontal-only is not explained; the defect is gone.

**The remaining ripple was the sample cadence against the poll cadence.** "A lot better, not
perfect": with the grid in place, 1,261 of 5,000 reports still carried the same gyro sample as the
one before, because a 125 Hz gyro on a 6 ms endpoint puts every fourth sample into two reports.
Steam integrates per report with a fixed step, so that is a 25 % velocity ripple at 42 Hz. Handheld
Companion does not have it because its whole target ticks at 125 Hz, the same rate as the sensor.
VIIPER's Deck device now reports the mean gyro rate since the previous report, holding each sample
until the next one arrives, so the sum of the reported rates equals the rotation the samples
described for any sensor and poll cadence (`device/steamdeck/gyroresampler.go`, VIIPER wsgm).

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

| Finding                                                  | Cost at idle                                          | Status                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                   |
| -------------------------------------------------------- | ----------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| VIIPER keepalive replay and completions with no consumer | 40 % CPU, 55 % wakeups                                | partly fixed: an unchanged frame is no longer submitted, and the replay of a report the host already has now costs a timer and a socket write instead of a context pair, a device call and a report allocation (see "Inside VIIPER" below); the 64 ms idle repeat that went with it was reverted because it made the gyro stutter, so the replay is still one write every 6 ms, and the completion now sits on the 6 ms grid instead of following the input (see "The gyro microstutter" below); a WSGM capture has not been re-recorded |
| Per-sample thread-pool hops                              | 35 % CPU, 25 % wakeups                                | partly fixed: the pool's spin-then-sleep is off and an unchanged frame no longer reaches VIIPER; routing a sample on its publishing thread was tried and reverted, because a route runs WSGM's own observers and one that blocks would stall the plugin's HID reader; the drain hop and the HID completion-port hop remain                                                                                                                                                                                                               |
| Power preset replayed on every foreground change         | WMI writes and readbacks per window switch, WmiPrvSE  | fixed: the same assignment resolving for another application is not re-applied while the device still shows it                                                                                                                                                                                                                                                                                                                                                                                                                           |
| Steam running-application poll every 2 s over CEF        | part of the steamwebhelper 1.8 %                      | open: `RunningApplicationTarget` polls the client; Steam's app lifetime notifications would make it event-driven                                                                                                                                                                                                                                                                                                                                                                                                                         |
| Motion polled at 2 ms with nobody reading it             | 12 % CPU + WUDFHost 5 % of a core                     | fixed: the motion demand signal stops the source while no consumer has asked the Steam Deck target for motion (see device-integration.md), and while one has, the Sensor API delivers reports by event instead of the 2 ms poll, with polling as the fallback                                                                                                                                                                                                                                                                            |
| WinRT Bluetooth enumeration every 2 s at idle            | 17 % CPU                                              | fixed: the radio timer stops when the overlay closes                                                                                                                                                                                                                                                                                                                                                                                                                                                                                     |
| SDL joystick thread polling XInput, looping after resume | 2 to 3 % of a core always, a full core after a resume | fixed: SDL starts without its joystick thread; device changes are picked up while WSGM polls, which is when it uses a pad                                                                                                                                                                                                                                                                                                                                                                                                                |
| Audio volume and endpoints polled every second           | two COM round trips per second, enumeration every 5 s | fixed: Core Audio change watches (windows-device-control `StartVolumeWatch`, `StartEndpointWatch`) replace the poll; a ten-second safety poll remains                                                                                                                                                                                                                                                                                                                                                                                    |
| Identity and AC state over WMI every 10 s                | 1 % CPU + WmiPrvSE                                    | not a finding: the identity reads belonged to the preset replays above; the observation cycle reads only the capabilities it publishes                                                                                                                                                                                                                                                                                                                                                                                                   |
| `WaitHandleCannotBeOpenedException` every second         | negligible CPU, one throw/s                           | fixed: `LhmSensorReader` opened the provider's missing mutex on every read; it now tries once per mapping                                                                                                                                                                                                                                                                                                                                                                                                                                |
| Every publication re-read on each 10 s network poll      | about 1 % CPU                                         | deferred: the toolkit's publish loop reads every module's state whenever any module signals; scoping it needs a per-patch `QueuePublication(patchId)` in the toolkit and each of the session host's twenty signal sites to name its patch                                                                                                                                                                                                                                                                                                |

Fixed means changed in code and not yet re-measured; a row moves to done when a new capture shows
the cost gone.

## Recording a run

    .\tools\PerfLab\perf-capture.ps1 -Scenario idle-desktop -Seconds 30

Copy `report.md`, `counters-summary.txt` and `system.json` from the run directory into
`captures/<stamp>-<scenario>/` and add the row above. The trace itself is a gigabyte and stays
local.

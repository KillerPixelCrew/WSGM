# PerfLab

Records and summarizes the performance scenarios tracked in [docs/perf](../../docs/perf/README.md):
what every WSGM process costs in CPU, wakeups and memory, and which code spends it. It is a
development tool outside `WSGM.slnx` and never ships.

## Recording a scenario

Set the machine up first (the game running, the overlay open, on battery), then:

    .\tools\PerfLab\perf-capture.ps1 -Scenario idle-desktop -Seconds 30
    .\tools\PerfLab\perf-capture.ps1 -Scenario in-game-overlay-closed -Seconds 60 -Countdown 15

The script elevates itself for Windows Performance Recorder and writes one run directory under
`artifacts\perf\<stamp>-<scenario>` under the repository:

| File                   | Contents                                                                                             |
| ---------------------- | ---------------------------------------------------------------------------------------------------- |
| `trace.etl`            | CPU sampling, context switches with stacks, .NET rundown and power events; about 1 GB/30 s           |
| `counters.csv`         | Every five seconds, per process: CPU %, private bytes, working set, handles, threads, discharge rate |
| `counters-summary.txt` | The per-process averages of `counters.csv`                                                           |
| `runtime-counters.csv` | `dotnet-counters` for WSGM.exe: allocation rate, GC, thread pool work items, exceptions              |
| `system.json`          | Build version, power source, battery level, processes present                                        |
| `report.md`            | The analyzer's summary of the trace                                                                  |

Keep a run by copying `report.md`, `counters-summary.txt` and `system.json` into
`docs/perf/captures/<stamp>-<scenario>/`. The trace itself is never committed.

`dotnet-counters` is optional: `dotnet tool install -g dotnet-counters`. Without it the runtime
counters are skipped and the script says so.

## Reading a trace

    dotnet run --project tools\PerfLab -c Release -- <trace.etl> --out report.md [--top 20] [--focus image.exe]... [--no-symbols]

The report lists every process by CPU time and context switches, then for each focus process (WSGM,
its launchers and service, Steam, RTSS and the LibreHardwareMonitor provider by default) the
threads, the CPU by first WSGM frame, by leaf function and by whole stack, the wait sites that
produced the wakeups, the .NET exceptions thrown, and a section per hot thread with what it ran and
what it waited on.

Symbols come from the trace's .NET rundown for managed code and from the Microsoft symbol server for
Windows binaries, cached under `%TEMP%\SymCache`; the first run downloads for several minutes.
`libviiper.dll` is a Go c-shared library and has no PDB, so its frames stay as offsets and its
threads usually have no walkable stack at all. Profile VIIPER with its own hook instead: set
`VIIPER_CPUPROFILE=<path>` in WSGM's environment and it writes a pprof file on every init and
shutdown cycle. To measure what one virtual controller report costs, without WSGM in the picture,
use the submodule's own harness:

    cd external\viiper
    $env:VIIPER_URB_BENCH = "1"; go test .\internal\server\usb -run TestURBCycleCost -v

It drives the real USB/IP server from a second process and reports cycles, context switches and
allocations per completed URB. The numbers it produced are in
[docs/perf](../../docs/perf/README.md#inside-viiper-what-one-controller-report-costs).

An offset in a `libviiper.dll` frame resolves against the staged library, whose exact source commit
is in `src\WSGM\Native\Viiper\libviiper.revision`:

    go tool nm -n src\WSGM\Native\Viiper\libviiper.dll

Those addresses already include the image's preferred base, which `dumpbin /headers` reports as
`ImageBase`, so a `libviiper.dll!0x12411` in a report is the last symbol at or below
`ImageBase + 0x12411`.

## What the numbers mean

- CPU % of one core is sampled CPU time over the sample span, so a process at 12 % costs an eighth
  of one core continuously. Task Manager divides by the core count.
- Context switches are switch-ins of the process's threads. Every one is a wakeup that costs
  scheduler time and, on battery, a package idle-state exit.
- Recording with stacks is not free: kernel stack walking shows up in the sampled processes as
  `RtlpLookupFunctionEntryForStackWalks` and friends. Compare runs against each other, not against
  an unrecorded machine.

## Code map and analysis limits

[`perf-capture.ps1`](perf-capture.ps1) owns the attended recording session, elevation, run-directory
creation, Windows recorder lifetime and optional runtime counters. [`Program.cs`](Program.cs) is the
independent offline analyzer: it opens an ETL with TraceProcessor, requests CPU sampling,
context-switch, generic-event and symbol data, processes the trace, then optionally resolves symbols
before writing Markdown to stdout or `--out`.

The analyzer's `DefaultFocus` list selects the processes for detailed breakdowns. Supplying one or
more `--focus` arguments replaces that default list; the all-process totals are still reported.
`OwnImages` selects which frame counts as WSGM-owned attribution, including the toolkit, Windows
control library, VIIPER, input lease and backdrop. `--top` defaults to 15; `--no-symbols` avoids
symbol resolution, with less useful native frame names. Duration is derived from the sampling span,
and lost ETW events are tolerated, so the output is evidence of the captured interval rather than an
exact accounting of every scheduler event. The tool does not change running WSGM configuration or
apply performance tuning.

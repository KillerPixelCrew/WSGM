# AutoTDP controller design

Status: design for issue 181, written 2026-09-26 from the four baseline traces captured that day.
Until it lands, the shipping controller is the one described under "AutoTDP policy" in
`docs\rtss.md`. When the implementation is committed this document becomes the description of
`AutoTdpController` and that section is reduced to a pointer.

## What the traces established

Four traces of the current controller in Cult of the Lamb on the Claw (2026-09-26, 8 W start, a 33
minute hold with loading cycles, and two 37 W starts) show:

- One decision per RTSS window of about 1016 ms and 61 frames. No per-frame writes. The reported 4
  to 5 W jumps are chains of 1 W raises 5 s apart.
- Loading stalls (windows of 1 to 44 frames, GPU load 2 to 36 %) count as sustained misses and raise
  power. Power-limited misses look different: 52 to 58 FPS for many consecutive windows with GPU
  load 85 to 100 %.
- A downward probe is rejected by the first missed window after settling. 8 of 9 rejections were
  single windows, 4 of them stalls with GPU load under 60 %.
- A rejected probe writes a failed-probe floor that never expires. 1245 of 2408 tick rows hold with
  reason `below-learned-floor`. That floor, not the capped-probe rule, is why the limit never comes
  back down.
- Starting control in a known context replaces the observed limit with the learned floor without
  writing it. With 37 W on the hardware the controller believed 28 W, and its first "raise" to 29 W
  cut the limit by 8 W in one write.
- 38 windows were the previous RTSS window read again. One raise was decided on a repeated window.

The full analysis is in issue 181. The CSVs are the replay inputs for the new controller.

## Scope

The controller stays what it is today: a pure, single-threaded policy in `Core\AutoTdp.cs` whose
inputs are arguments and whose outputs are decisions, replayable from a trace. `AutoTdpService`
keeps ownership of application identity, capability lookup, paired limits, one write in flight,
manual pause, restore on stop and the trace. The device package keeps the limit range and the
sustained/boost relationship. Nothing here changes QAM or overlay ownership.

There is no learning. No floor, learned or failed, is kept for a context or across sessions. Control
starts from the limit the hardware reports and every conclusion is re-tested from fresh evidence.
The one piece of memory is a probe backoff that lasts for the current operating point and is bounded
in time, described below.

## Signals

Each tick the service reads what it reads today and passes it in one sample:

| Field                                   | Source                                                            | Use                                                                    |
| --------------------------------------- | ----------------------------------------------------------------- | ---------------------------------------------------------------------- |
| Window identity                         | RTSS `dwTime0`, `dwTime1`                                         | Dedupe. A window already judged is skipped entirely.                   |
| Window duration, frames, mean frametime | RTSS window                                                       | Ratio and severity.                                                    |
| Target frametime                        | Verified RTSS frame limit                                         | Deadline. Part of the context key.                                     |
| Capped                                  | Mean within 0.97 to 1.05 of target                                | Headroom while the limiter holds the game.                             |
| GPU load, CPU load                      | `RtssOsdMetricsSource`, when the OSD's sensor provider is running | Tertiary evidence. Absent values disable only the rules that use them. |
| Limits                                  | Plugin descriptor                                                 | Minimum, maximum, step.                                                |

Time is measured from RTSS window timestamps, not from tick count, so tick jitter and missed ticks
neither speed up nor slow down a dwell. Every dwell below is a sum of fresh window durations.

RTSS publishes a mean and a frame count per window and the last frame's time in microseconds. There
is no per-frame history without polling faster than the frame rate, which this design does not do.
Severity is therefore judged from the mean, the frame count and the window's length: RTSS closes a
window on a frame boundary after one second, so a window much longer than one second ends with one
long frame.

### Window classes

For a fresh window with ratio `r = mean / target`:

| Class       | Condition                                                                  | Meaning                                              |
| ----------- | -------------------------------------------------------------------------- | ---------------------------------------------------- |
| Severe      | `r >= 1.5`, or duration `>= 1.4 x` nominal (1000 ms plus one target frame) | A stall, hiatus or loading interval. Contaminated.   |
| Missed      | `r > 1.05` and not severe                                                  | Delivery below target. Evidence, once sustained.     |
| On target   | `1.0 < r <= 1.05` and not capped                                           | Meeting the deadline without headroom.               |
| Comfortable | `r <= 0.92`, or capped and not missed                                      | Headroom, or the limiter holding the game at target. |

A repeated window, or a tick with no window, is none of these and touches no counter. Three seconds
without a fresh window resets the miss and comfort streaks, since the game may have been paused or
minimized.

### Utilization rules

GPU and CPU load never command a wattage. Each rule below names the one place a value is consulted,
and every rule is skipped when the value is absent:

- Stall support: a severe window with GPU load under 40 % is treated as a loading interval for the
  persistent-stall escape below. A severe window with GPU load at or above 60 %, or with no GPU
  value, can escape into escalation.
- Raise deferral: a sustained miss whose three windows all show GPU load under 50 % is deferred for
  up to six further windows. If the misses persist past that, power is raised anyway. Utilization
  delays a raise; it never vetoes one.
- Escalation support: while raising, a step whose judged windows show GPU load at or above 85 %
  counts as responsive regardless of the frametime response.
- Probe classification: a failed probe whose missed windows show GPU load under 50 % and not more
  than 15 points above the pre-probe level is inconclusive rather than confirmed.

Aggregate CPU load is recorded but not used by any rule: one saturated render thread hides in a low
total, and loading screens can show high CPU. Per-thread evidence is not available from RTSS.

## States

```
Settling ──► Tracking ──► Probing ──► Tracking
   ▲            │  ▲          │
   │            │  │          └── severe ──► Quarantine
   │            ▼  │                             │
   └──── Raising ──┴──── Unresponsive ◄──────────┘ (persistent stall with power-bound support)
                                 │
                                 └──► Tracking (delivery recovered or hold expired)
```

Every write is followed by Settling. Paused (manual change) and Released (stop) are unchanged from
today and sit outside this loop.

### Settling

Entered after every write. Holds until two fresh windows and at least 2 s of window time have
passed. Nothing is judged. Then returns to the state that requested the write: Tracking after a
raise or restore, Probing after a probe.

### Tracking

The steady state. Counts fresh windows toward one of three exits:

- Raise: three consecutive missed windows (severe windows break the streak and go to Quarantine).
  Subject to the GPU deferral above. Enters Raising with the median ratio of those three windows as
  the baseline.
- Probe: the stability dwell is satisfied. Enters Probing.
- Quarantine: a severe window.

Stability dwell: over the most recent dwell period of fresh windows, no severe window, at most one
missed window, no two consecutive missed windows, and every other window comfortable or capped. The
base dwell is 10 s, multiplied by the probe backoff. A single hitch no longer resets the dwell to
zero; it is one tolerated miss inside it.

At the device maximum with sustained misses the controller stays in Tracking and reports
`cant-reach`, as today. At the minimum it reports `at-minimum`.

### Raising

One step up per write, then Settling, then three fresh windows are judged against the baseline:

- Resolved: the median ratio is at or below 1.05, or the game is capped again. Back to Tracking with
  streaks cleared.
- Responsive: the median ratio improved by at least 0.04 against the baseline, or GPU load is at or
  above 85 %. Misses still sustained, so raise again with the new median as the baseline.
- Not improved: neither of the above. Counted. A third consecutive step without improvement ends the
  chain in Unresponsive. Until then the chain continues, because a scene that is getting heavier
  while power rises looks the same as one that does not respond, and the third step settles it.

So a genuinely power-limited scene climbs at one step per 5 s, as today, for as long as each step
helps. A loading screen or a render-thread stall gets at most three steps, and only if it lasts
longer than the 15 s those steps take.

### Unresponsive

Power went up and delivery did not follow. Hold. Leave when a window is on target, comfortable or
capped (Tracking; the probe path then recovers the steps), or after 20 s of window time (Tracking
with the raise baseline cleared, so a later sustained miss is judged from scratch). A severe window
here goes to Quarantine.

### Quarantine

Entered on a severe window from any judging state. A probe in flight is restored first and recorded
as inconclusive; a raise chain is abandoned. While in Quarantine nothing is learned, no probe is
judged and no step is taken from the stall itself.

- Recovery: three consecutive non-severe fresh windows end the quarantine. Their misses do not count
  toward a raise; Tracking starts with empty streaks. That is the fresh window the issue asks for
  before reacting to post-loading frames.
- Persistent stall: four consecutive severe windows. With GPU load under 40 % the quarantine simply
  continues; a loading screen is not a power request however long it takes. With GPU load at or
  above 60 %, or no GPU value, the windows are treated as a sustained miss and the controller enters
  Raising, whose response test bounds the damage at three steps if the stall was not power-bound
  after all.

Quarantine never freezes control: recovery needs only three ordinary windows.

The trace records quarantine entry and exit with the window that caused each, the probe id it
interrupted, and whether the persistent-stall escape fired.

### Probing

One step down per write, then Settling, then up to six fresh windows are judged:

- Accepted: six judged windows without a failure below. The lower limit is the new operating point.
  Tracking starts its dwell immediately, so descent continues at roughly one step per 18 s while
  headroom lasts.
- Failed: two missed windows among the last three judged. Restore the previous limit. Then the
  utilization classification decides:
  - Confirmed: the backoff for this operating point doubles.
  - Inconclusive: the backoff is unchanged.
- Interrupted: a severe window. Restore, inconclusive, Quarantine.

A single missed window inside a probe is tolerated because a healthy capped game in the traces
missed about 4 % of windows, always singly. Two of three catches a genuinely load-bearing step
within three seconds, since a power-limited miss repeats every window.

### Probe backoff

The only memory. It belongs to the current operating point and multiplies the stability dwell: 1x,
2x, 4x, 8x, capped at 12x (120 s). It resets to 1x when the limit rises, when the context changes,
and when a quarantine ends, since a loading interval usually means a new area whose need is unknown.
It is not persisted and there is no floor: a probe is always eventually retried.

The cost in a static power-bound scene is one failed probe per backoff period, about 4 s at a few
FPS below target. The cap trades that against how quickly a lighter scene is discovered. 120 s is
the proposed value; it is a constant, and replay will show what the traces prefer.

## Cadence and bounds

| Bound                           | Value                                                                            |
| ------------------------------- | -------------------------------------------------------------------------------- |
| Judgement                       | Once per fresh RTSS window, about 1 s.                                           |
| Settle after any write          | 2 fresh windows and at least 2 s.                                                |
| Raise evidence                  | 3 consecutive fresh missed windows, at least 3 s.                                |
| Raise chain                     | 1 step per write, re-judged over 3 windows; at most 3 steps without improvement. |
| Probe evidence                  | 6 fresh windows to accept; 2 misses in 3 to fail.                                |
| Stability dwell before a probe  | 10 s, times the backoff, at most 120 s.                                          |
| Step size                       | The device step. Always one step per write.                                      |
| Minimum interval between writes | 2 s.                                                                             |

Missed ticks, repeated windows and delayed writes cannot shorten any of these, because all of them
count fresh windows and window time.

## Start and re-basing

Control starts from the limit the hardware reports, or the last written value on a device without
readback, or the ceiling. No stored value replaces it. An unapplied write re-bases the same way, as
today.

## What the service changes

- Passes the window identity, duration, frames and the metrics sample into the controller sample.
  Metrics are read before the decision, at most once per second as the source already caches.
- Removes the learned-floor start and the two floor dictionaries.
- Extends the trace with the new state, the window class, the raise baseline and step count, the
  probe failure count and backoff, quarantine entry and exit, and which utilization rule fired.
  Columns are appended; the replay reads by name.
- Does not start the OSD sensor provider. AutoTDP uses GPU load when the OSD already publishes it.
  Whether AutoTDP should start the provider on its own is a separate decision.

## Verification

Replay first, hardware second:

- The four 2026-09-26 traces replayed through the new controller must show: no raise on a window
  with GPU load under 40 %, no floor holds, start at the observed 37 W, and a descent from 37 W to
  the capped operating point in the two high-start traces.
- Unit tests per rule: repeated window skipped, three-second gap resets streaks, severe window
  interrupts a probe without confirming it, two-of-three failure, single-miss tolerance, backoff
  doubling and its three resets, raise chain stops after three unimproved steps, GPU deferral
  expires after six windows, persistent stall with low GPU never raises, persistent stall with no
  GPU value raises at most three steps, quarantine recovery discards its misses, capped windows
  still probe, at-maximum and at-minimum holds, manual pause and stop unchanged.
- Then the issue's live checks on the Claw with the trace on: Cult of the Lamb from a high start
  through loading screens, a sustained power-limited scene, a second target frame rate, and the
  manual reference run.

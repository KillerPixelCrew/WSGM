# AutoTDP controller design

This is what `AutoTdpController` does and why, written 2026-09-26 from four baseline traces captured
that day for issue 181, with step sizing added on 2026-09-27 after the first live test.
`docs\rtss.md` summarises it beside the rest of the RTSS integration.

## What the first live test showed

Cult of the Lamb on the Claw, 2026-09-27, 60 FPS cap, starting at 8 W. The controller behaved
correctly and far too slowly. Climbing to the 31 W the late-game village needed took 23 one-watt
raises over five minutes. Entering the chapel, a small area that runs at 11 W, took 16 probes at one
per 18 s to reach 15 W. Through that descent the game held 60 FPS and GPU load stayed near 60 %
while package power followed the limit exactly, so neither frame time nor power draw shows headroom
in a capped game. Load does: the village ran at 80 to 85 % CPU and failed at 27 W, the chapel at 50
to 60 %.

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
- A present hiatus is visible while it happens. RTSS closes a window on a frame boundary after one
  second, so a blocked render thread leaves the previous window in place with a growing age.
  Ordinary tick phase drift reads 984 to 1032 ms; every repeat with an age above 1300 ms preceded a
  stall window (1406 ms before a 2218 ms window, 1359 and 1500 ms inside the 5.6 s loading stall,
  1578 ms before a 1734 ms window of 43 frames). The last-frame field showed 818 ms inside a stall,
  while healthy capped play at GPU 65 to 80 % produced last frames of 95 to 185 ms.

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

| Field                                   | Source                                               | Use                                                                    |
| --------------------------------------- | ---------------------------------------------------- | ---------------------------------------------------------------------- |
| Window identity                         | RTSS `dwTime0`, `dwTime1`                            | Dedupe. A window already judged is skipped entirely.                   |
| Window duration, frames, mean frametime | RTSS window                                          | Ratio and severity.                                                    |
| Target frametime                        | Verified RTSS frame limit                            | Deadline. Part of the context key.                                     |
| Last frame                              | RTSS `dwFrameTime`, microseconds                     | A hiatus that ended inside the window.                                 |
| Sample age                              | RTSS window                                          | Time since the last present, for a hiatus still in progress.           |
| GPU load, CPU load                      | `RtssOsdMetricsSource`, shared with the OSD renderer | Tertiary evidence. Absent values disable only the rules that use them. |
| Limits                                  | Plugin descriptor                                    | Minimum, maximum, step.                                                |

Time is measured from RTSS window timestamps, not from tick count, so tick jitter and missed ticks
neither speed up nor slow down a dwell. Every dwell below is a sum of fresh window durations.

RTSS publishes a mean and a frame count per window and the last frame's time in microseconds. There
is no per-frame history without polling faster than the frame rate, which this design does not do.
What it does have is elapsed time since the last present, which is the stall signal the issue asks
for, measured in target frames.

### Hiatus detection

A hiatus is a gap in presents of at least `HiatusFrames` target frames, 15 by default and never
under 250 ms, so a 60 FPS target quarantines at 250 ms and a 30 FPS target at 500 ms. Ordinary
hitches of 95 to 185 ms stay in the normal statistics.

**In progress** is measured on one clock: the time since the last frame was presented. Every read
that carries a window says when that was, as the read's timestamp minus the window's age, and the
controller keeps the latest such answer. Past the nominal window length plus the hiatus threshold —
1250 ms at a 60 FPS target — no frame has been presented for long enough to quarantine, and the
controller does so on that tick, before the stall's own window has closed.

The single clock matters because the telemetry itself disappears mid-stall: RTSS drops an
application's entry once it is two seconds stale, so a long stall is first a window whose age keeps
growing and then no window at all. Measuring the gap rather than reading it off the sample carries
the detection across that boundary. Ordinary tick phase drift reads 984 to 1032 ms and is not a
hiatus.

**Completed** is the last-frame time: a fresh window whose final frame took at least the threshold
contained a hiatus that ended inside it, and the window is severe.

Persistence comes from the quarantine rules, not from the detector: leaving takes three ordinary
windows, and a stall that keeps going is judged by the persistent-stall rule. A hiatus is detected
at most one tick late, because the tick is the sampling rate.

### Window classes

For a fresh window with ratio `r = mean / target`:

| Class       | Condition                                                                                                                  | Meaning                                              |
| ----------- | -------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------- |
| Severe      | last frame at or above the hiatus threshold, or `r >= 1.5`, or duration `>= 1.4 x` nominal (1000 ms plus one target frame) | A stall, hiatus or loading interval. Contaminated.   |
| Missed      | `r > 1.05` and not severe                                                                                                  | Delivery below target. Evidence, once sustained.     |
| On target   | `0.92 < r < 0.97`                                                                                                          | Beating the deadline, but with nothing to give away. |
| Comfortable | `r <= 0.92`, or `0.97 <= r <= 1.05`                                                                                        | Headroom, or the limiter holding the game at target. |

The ratio and duration conditions are the backstop for a stall the last-frame field did not happen
to hold, such as a loading interval of many slow frames rather than one long one. A repeated window,
or a tick with no window, is none of these and touches no counter, except that a repeat carrying a
hiatus in progress enters Quarantine as above. Three seconds without a fresh window resets the miss
and comfort streaks, since the game may have been paused or minimized.

### Utilization rules

GPU and CPU load never keep a limit that frames rejected. Each rule below names the one place a
value is consulted, and every rule is skipped when the value is absent:

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
- Probe sizing: a downward probe gives up the headroom the settled windows showed. The load is the
  mean over those windows of the busier of GPU and total CPU, and the probe aims for that load to
  reach 85 %: `target = watts x load / 85`, at least one step and at most a third of the limit. The
  windows are the dwell before the first probe and the previous probe's own windows after it.

Aggregate CPU load is used only in probe sizing, where it can only make a probe smaller: one
saturated render thread hides in a low total, so a CPU-bound scene can look like headroom, and the
probe's frame judgement is what catches that. Per-thread evidence is not available from RTSS.

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
- Probe: the stability dwell is satisfied, or a descent is in progress. Enters Probing.
- Quarantine: a severe window.

Stability dwell: over the most recent dwell period of fresh windows, no severe window, at most one
missed window, no two consecutive missed windows, and every other window comfortable or capped. The
base dwell is 10 s, multiplied by the probe backoff. A single hitch no longer resets the dwell to
zero; it is one tolerated miss inside it.

At the device maximum with sustained misses the controller stays in Tracking and reports
`cant-reach`, as today. At the minimum it reports `at-minimum`.

### Raising

A raise is sized to the deficit it answers: `watts x (ratio - 1)`, rounded up to whole steps, at
least one step and never more than doubling the limit. Frames 30 % late ask for 30 % more power.
Frame rate grows more slowly than power, so this undershoots rather than overshoots, and a descent
takes back whatever it did overshoot. The ratio is the median of the three missed windows for the
first raise, and the median of the judged windows for each raise after it. Every raise is followed
by Settling, then three fresh windows are judged against the baseline:

- Resolved: the median ratio is at or below 1.05, or the game is capped again. Back to Tracking with
  streaks cleared.
- Responsive: the median ratio improved by at least 0.04 against the baseline, or GPU load is at or
  above 85 %. Misses still sustained, so raise again, sized to the new median, which becomes the
  baseline.
- Not improved: neither of the above. Counted, and the next raise is a single step, because power
  that is not turning into frames is not sized up. A third consecutive step without improvement ends
  the chain in Unresponsive. Until then the chain continues, because a scene that is getting heavier
  while power rises looks the same as one that does not respond, and the third step settles it.

So a power-limited scene 40 % short of its target gets most of the way in one write instead of one
step per 5 s. A loading screen or a render-thread stall gets at most one sized raise followed by
single steps.

### Unresponsive

Power went up and delivery did not follow. Hold. Leave when a window is on target, comfortable or
capped (Tracking; the probe path then recovers the steps), or after 20 s of window time (Tracking
with the raise baseline cleared, so a later sustained miss is judged from scratch). A severe window
here goes to Quarantine.

### Quarantine

Entered on a hiatus in progress or a severe window, from any judging state. A probe in flight is
restored first and recorded as inconclusive; a raise chain is abandoned. While in Quarantine nothing
is learned, no probe is judged and no step is taken from the stall itself.

- Recovery: three consecutive non-severe fresh windows end the quarantine. Their misses do not count
  toward a raise; Tracking starts with empty streaks. That is the fresh window the issue asks for
  before reacting to post-loading frames.
- Persistent stall: four consecutive severe windows, or a hiatus in progress for four ticks. With
  GPU load under 40 % the quarantine simply continues; a loading screen is not a power request
  however long it takes. With GPU load at or above 60 %, or no GPU value, the windows are treated as
  a sustained miss and the controller enters Raising with a raise sized to the last window's ratio,
  at most doubling the limit. The response test then limits further raises to single steps if the
  stall was not power-bound after all.

Quarantine never freezes control: recovery needs only three ordinary windows.

The trace records quarantine entry and exit with the window or hiatus that caused each, the gap in
target frames, the probe id it interrupted, and whether the persistent-stall escape fired.

### Probing

A probe is sized by the probe-sizing rule above. Without a load reading it follows a doubling
schedule instead: one step, then two, then four for each accepted probe in a descent. Each probe is
followed by Settling, then up to four fresh windows are judged:

- Accepted: four judged windows without a failure below. The lower limit is the new operating point,
  and the descent continues: the next comfortable window starts the next probe without another
  dwell, sized from this probe's windows. A probe costs about seven seconds, so a scene with a lot
  of headroom comes down in a few probes rather than at one step per 18 s.
- Failed: two missed windows among the last three judged. Restore the previous limit.
  - A probe of more than one step was too deep, which says nothing about a smaller one. The descent
    continues, and until it ends no probe reaches further than half the distance to the limit that
    failed; one step short of it, the descent ends.
  - A single-step probe ends the descent, and the utilization classification decides. Confirmed: the
    backoff for this operating point doubles. Inconclusive: the backoff is unchanged.
- Interrupted: a severe window. Restore, inconclusive, Quarantine.

A descent ends on any missed window in Tracking, an on-target window, a raise, a quarantine, a
context change, or a failed single step. The next probe then waits for an ordinary dwell, and the
limit that failed inside the descent is forgotten.

A single missed window inside a probe is tolerated because a healthy capped game in the traces
missed about 4 % of windows, always singly. Two of three catches a genuinely load-bearing step
within three seconds, since a power-limited miss repeats every window.

### Probe backoff

The only memory. It belongs to the current operating point and doubles the stability dwell on a
confirmed failure: 10, 20, 40, then 60 seconds. It resets whenever the limit changes at all, when
the context changes, and when a quarantine ends, since a loading interval usually means a new area
whose need is unknown. It is not persisted and there is no floor: a probe is always eventually
retried.

The cost in a static power-bound scene is one failed probe per backoff period, about 4 s at a few
FPS below target. The cap trades that against how quickly a lighter scene is discovered. A minute
was chosen with the maintainer on 2026-09-26.

## Cadence and bounds

| Bound                           | Value                                                                             |
| ------------------------------- | --------------------------------------------------------------------------------- |
| Judgement                       | Once per fresh RTSS window, about 1 s.                                            |
| Settle after any write          | 2 fresh windows and at least 2 s.                                                 |
| Raise evidence                  | 3 consecutive fresh missed windows, at least 3 s.                                 |
| Raise chain                     | Re-judged over 3 windows; at most 3 unanswered raises.                            |
| Raise size                      | `watts x (ratio - 1)`, at most doubling; one step after an unanswered raise.      |
| Probe evidence                  | 4 fresh windows to accept; 2 misses in 3 to fail.                                 |
| Probe size                      | Load-sized, at most a third of the limit; else 1, 2, 4 steps through a descent.   |
| Stability dwell before a probe  | 10 s, doubling on a confirmed probe failure, at most 60 s. None inside a descent. |
| Minimum interval between writes | 2 s.                                                                              |

Missed ticks, repeated windows and delayed writes cannot shorten any of these, because all of them
count fresh windows and window time.

## Start and re-basing

Control starts from the limit the hardware reports, or the last written value on a device without
readback, or the ceiling. No stored value replaces it. An unapplied write re-bases the same way, as
today.

## What the service owns

- It hands the controller the raw window — its identity, duration, frames, mean, last frame and age
  — rather than a classification, so every judgement belongs to the policy and a replayed file needs
  only the recorded inputs.
- One clock per tick, shared by the controller and the trace, so `elapsed_ms` is the exact value the
  controller was given and a replay reproduces a timing decision.
- Sensors are read once, before the decision, and the same sample goes on the trace row. The source
  is the one the OSD renderer already owns, reached through `IRtssAdapter.SampleSensors`, so there
  is a single mapping handle, a single cached read per second, and a single attempt to start RTSS's
  sensor provider. AutoTDP requires RTSS regardless, so starting the provider is in scope.
- Every tick reaches the controller once control has started, including ticks with no renderer at
  all. A quarantine has to outlive the telemetry that triggered it.
- The context key carries the deadline as well as the application.

## Verification

Replay first, hardware second:

- Fixtures in `tests\WSGM.Tests\Fixtures\AutoTdp` drive the controller through the recorded shapes:
  steady capped play descends, late frames on a saturated GPU climb a step at a time, and a loading
  stall on an idle GPU changes nothing. Their provenance is in the README beside them.
- Unit tests per rule: repeated window skipped, a repeat inside the nominal length is phase drift, a
  gap past the hiatus threshold quarantines on that tick, a renderer that disappears stays
  quarantined, a last frame at the threshold is severe and one below it is not, a window far past
  its deadline is severe even when its last frame was fast, one hitch inside the dwell is tolerated
  and two are not, severe window interrupts a probe without holding it against the step,
  two-of-three probe failure, single-miss tolerance, backoff doubling and its reset on a raise, the
  raise chain stopping after three unanswered steps and leaving the hold when delivery returns, GPU
  deferral and its expiry, persistent stall with low GPU never raising, persistent stall with no GPU
  value raising, quarantine recovery discarding its misses, capped windows still probing, descent to
  the minimum, at-maximum and at-minimum holds, a frame-cap change starting over, manual pause and
  stop unchanged.
- Then the issue's live checks on the Claw with the trace on: Cult of the Lamb from a high start
  through loading screens, a sustained power-limited scene, a second target frame rate, and the
  manual reference run. The four 2026-09-26 captures replay too, and are the attended check that the
  reported session no longer holds its limit.

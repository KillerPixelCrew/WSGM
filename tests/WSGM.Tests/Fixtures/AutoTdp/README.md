# AutoTDP replay fixtures

Hand-authored AutoTDP traces, in the CSV shape `AutoTdpTraceCsv` writes. `AutoTdpTraceReplay` reads
columns by name, so these carry only the controller's inputs: the RTSS window and its identity, the
deadline, the device bounds and the sensor sample. They have no `action` or `reason` column, because
nothing recorded them; the replayed decision series is what the test asserts on.

The shapes come from four traces captured on the reference Claw on 2026-09-26, playing Cult of the
Lamb at a 60 FPS cap while diagnosing issue 181. Those captures hold the maintainer's install paths
and are attached to the issue rather than committed here; these fixtures reproduce the signal
sequences that mattered, at the same window length (1016 ms) and frame counts the captures showed.

| File                      | What it reproduces                                                                       |
| ------------------------- | ---------------------------------------------------------------------------------------- |
| `capped-descent.csv`      | Steady play held at the cap from a limit with headroom. The limit must come down.        |
| `power-limited-climb.csv` | Frames late on a saturated GPU. The limit must go up, one step at a time.                |
| `loading-stall.csv`       | A loading stall on an idle GPU between two stretches of capped play. Nothing may change. |

To add one, keep `rtss_time0`/`rtss_time1` contiguous and advancing: those bounds are the
measurement's identity, and a repeated pair means the same window read twice.

# Native Editor performance pilot: 26 October 2026

Capture: `AppPerformance_20261026T225511Z_542a15ae`.
Archive SHA-256: `805b900e8e3923f195e5b608396f14770c7aba4a5baa63d1ca2b07e7ff1c0a55`.
Unity Editor 6000.3.15f1, Harokopio scene, Ryzen 9 5950X, RTX 3060,
32,677 MB system RAM, recorded view 1124 x 673, VSync 0, target frame rate -1.

## Integrity and analyzer correction

All output hashes and all 52 captured C# source hashes matched the collector/runtime
before the timing-metadata correction. The run completed all seven phases,
restored the source state, and preserved the original save SHA-256
`8561969f94218c20cc2f8849fd2f25367f0c34c07f391f271d5f570d1384e962`.
The recorded camera position and orientation stayed fixed in every phase.

The original analyzer rejected phase 5 as incomplete. Its event span is
17.999265300000047 s, but its last frame records 18.001499600000102 s on the
collector phase clock. The collector sets this clock before constructing and
writing the phase-start event; the two intervals therefore have different
origins. This is an analysis error, not evidence of user interruption.

The corrected analyzer checks completion against recorded frame phase time
(at least 18 s), requires exactly one start/end event in order, and checks the
end event's observed-frame count against all recorded phase rows. It retains
both durations in its output. New captures additionally record the actual phase
clock origin for an independent timestamp consistency check. The 3 s warm-up,
15 s measurement interval, workloads and raw archive are unchanged. Truly short
phases, missing end events and frame-count mismatches remain rejected.

Corrected outcome: **COMPLETE_DESCRIPTIVE_EDITOR_CAPTURE**, without an operational
frame-budget pass. This is a retrospective analyzer correction and is disclosed
explicitly; the original failed analysis and archive must remain available.

## Measurements

The original scene has four source features. Extra fixtures use 80% points,
10% three-vertex lines and 10% four-corner polygons. The reconstruction and source
features remain baseline workload. Warm-up and post-window settling frames are
excluded from the distributions below. Times are milliseconds; nearest-rank
p95/p99, one fixed-order pilot with 15 s per measured phase, not independent trials.

| Additional features | Condition | Measured frames | Median frame | p95 frame | p99 frame | Max frame | Median save call |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 0 | Idle baseline | 1,727 | 8.627 | 9.145 | 9.519 | 28.235 | — |
| 10 | Idle | 1,699 | 8.758 | 9.584 | 9.948 | 14.158 | — |
| 10 | Forced save | 1,700 | 8.785 | 9.504 | 10.005 | 12.450 | 1.459 |
| 100 | Idle | 1,598 | 9.371 | 10.329 | 10.917 | 15.087 | — |
| 100 | Forced save | 1,601 | 9.342 | 10.234 | 14.427 | 32.580 | 12.356 |
| 1,000 | Idle | 1,345 | 11.008 | 12.461 | 15.619 | 22.555 | — |
| 1,000 | Forced save | 1,144 | 10.930 | 12.599 | 175.122 | 187.382 | 169.232 |

Each save phase issued 14 requests, 42 total. Saved fixture features exactly
match their original inputs at all three scales, with empty final outboxes and
zero unintended outbox operations at every save request. Peak measured allocated
memory across phases was 1,160,980,555 bytes (about 1,107.2 MiB). This is total
observed allocated memory including the existing scene, not per-feature memory.

The 1,000-feature forced-save phase has large frame spikes alongside long
main-thread SaveToJson calls (max 176.306 ms). These data motivate profiling the
save path; they do not isolate a particular method as the bottleneck. Save-call
time is not background disk completion or HTTP delivery latency. Editor and
collector overhead are included, with no standalone headset or GPU-only claim.
Repeated runs, controlled order and target-device measurements remain separate.

## Validation of the correction

Fourteen analyzer tests pass, including delayed legacy events, explicit clock
origins and rejection of short/missing/count-inconsistent phases. The original
native archive was reanalyzed successfully, without modifying any captured file.
The C# source change only adds the existing phase-clock origin to event metadata;
a C# compiler/Unity runner was unavailable in the current analysis environment,
so that metadata addition still needs compilation by the next native Unity run.

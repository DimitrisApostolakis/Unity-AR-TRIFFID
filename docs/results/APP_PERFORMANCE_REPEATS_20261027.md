# Verified Harokopio Editor performance captures

Three descriptive native Editor captures are available. Both new captures pass
`COMPLETE_DESCRIPTIVE_EDITOR_CAPTURE`, including output integrity, seven-phase
coverage, timing clocks, fixture shape/counts, exact final saved features and
empty fixture outboxes. All 53 recorded C# sources in each new capture match the
published source. The first capture has the previously disclosed phase-start
metadata/analyzer correction; runtime application sources are unchanged.

| Run | Capture | ZIP SHA-256 |
| --- | --- | --- |
| 1 | `AppPerformance_20261026T225511Z_542a15ae` | `805b900e8e3923f195e5b608396f14770c7aba4a5baa63d1ca2b07e7ff1c0a55` |
| 2 | `AppPerformance_20261027T145649Z_b9805543` | `03a22bfa116a98550e997a2c88565186cf5d28d9e922a4d318d70a9ecdc310be` |
| 3 | `AppPerformance_20261027T150039Z_7bc145f2` | `e70fd5f2a33f1a8a5eaf57a8671a74558fa4d53eb6b30cedb83fb975d85d2b79` |

## Comparability and interpretation

All runs report Unity 6000.3.15f1, Harokopio, Ryzen 9 5950X, RTX 3060,
32,677 MB system RAM, 1124 x 673 view, VSync 0, target frame rate -1, four
source features, Fire Prefab, identical alignment and fixture centres. All six
non-baseline fixture input files are byte-identical across all three runs.
Original source save hashes are identical before/after every run and across runs.

Every phase records one constant camera pose with zero within-phase displacement
or rotation. Runs 2 and 3 share position `(0,0,-0.058799993246793747)` and identity
orientation. Run 1 has position `(0,0,0.1424005925655365)` and identity orientation:
a 0.201201-world-unit camera difference. The three captures therefore do not
fulfil strict identical-view repetition. Report per-run results rather than one
pooled mean or a single claimed three-repeat identical-view result.

Even runs 2 and 3 have substantial frame-time variation at their matching
recorded camera/settings. Its cause is not isolated. The collector does not
record application focus, background workload, GPU timings or an asset hash of
the Gaussian reconstruction. Matching scene names/settings alone cannot prove
all rendering conditions were identical. No extra run is required to report the
present descriptive evidence with these limits; stronger controlled-performance
claims would require additional controls/measurements.

## Frame and save results

Times are milliseconds. Every phase excludes 3 s warm-up and retains a 15 s
measurement window. These are correlated frames in fixed-order Editor workloads,
not independent scientific repetitions. Editor and collector overhead is included.

| Run | Extra features | Condition | Measured frames | Median frame | p95 frame | p99 frame | Maximum frame | Median main-thread save call |
| ---: | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 0 | idle | 1,727 | 8.627 | 9.145 | 9.519 | 28.235 | — |
| 1 | 10 | idle | 1,699 | 8.758 | 9.584 | 9.948 | 14.158 | — |
| 1 | 10 | save | 1,700 | 8.785 | 9.504 | 10.005 | 12.450 | 1.459 |
| 1 | 100 | idle | 1,598 | 9.371 | 10.329 | 10.917 | 15.087 | — |
| 1 | 100 | save | 1,601 | 9.342 | 10.234 | 14.427 | 32.580 | 12.356 |
| 1 | 1000 | idle | 1,345 | 11.008 | 12.461 | 15.619 | 22.555 | — |
| 1 | 1000 | save | 1,144 | 10.930 | 12.599 | 175.122 | 187.382 | 169.232 |
| 2 | 0 | idle | 1,787 | 8.283 | 8.926 | 8.963 | 9.759 | — |
| 2 | 10 | idle | 1,788 | 8.293 | 8.937 | 8.976 | 10.626 | — |
| 2 | 10 | save | 1,776 | 8.320 | 8.973 | 10.001 | 11.469 | 1.407 |
| 2 | 100 | idle | 1,635 | 9.405 | 10.005 | 10.120 | 12.783 | — |
| 2 | 100 | save | 1,259 | 9.555 | 23.161 | 24.426 | 33.918 | 12.796 |
| 2 | 1000 | idle | 793 | 23.222 | 25.819 | 27.305 | 28.387 | — |
| 2 | 1000 | save | 685 | 22.851 | 26.896 | 181.453 | 256.420 | 169.055 |
| 3 | 0 | idle | 1,777 | 8.317 | 8.971 | 9.016 | 9.497 | — |
| 3 | 10 | idle | 948 | 20.686 | 21.520 | 21.941 | 48.728 | — |
| 3 | 10 | save | 939 | 21.021 | 22.009 | 23.802 | 25.435 | 1.536 |
| 3 | 100 | idle | 906 | 20.956 | 23.600 | 23.821 | 24.081 | — |
| 3 | 100 | save | 884 | 20.976 | 23.807 | 25.209 | 28.041 | 12.856 |
| 3 | 1000 | idle | 775 | 23.725 | 25.644 | 27.104 | 32.078 | — |
| 3 | 1000 | save | 664 | 23.711 | 26.446 | 169.742 | 259.715 | 147.597 |

All three runs issue 14 save requests at each feature count: 42 per run, 126 total.
Every final fixture feature collection exactly matches its corresponding input;
all save events report zero unintended outbox operations and final outboxes are
empty. Peak allocated memory per run is approximately 1107.20, 1112.54 and
1120.75 MiB; these totals include the existing scene and Editor environment.

At 1,000 extra features, median main-thread save-call times are 169.232,
169.055 and 147.597 ms, with p99 frame times 175.122, 181.453 and 169.742 ms.
The save-related stalls recur in every capture. Their exact internal cause is
not isolated here; save-call time is not background disk completion or upload
latency. A high median FPS must not hide these tail-frame stalls.

The Editor-only performance collection is complete for the agreed descriptive
scope. Standalone/headset performance and additional sites are excluded by user
scope. Manual controller/drawing checks remain before the UI study; geographic
accuracy remains pending SRT/COLMAP/reference evidence. No new product behavior
or numerical tolerance was changed during verification.

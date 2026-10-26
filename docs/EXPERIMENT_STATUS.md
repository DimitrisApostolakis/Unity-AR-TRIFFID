# Paper experiment status (26 October 2026)

Results below are pilot evidence for the stated site/runtime. The evaluation
protocol remains a draft; final domains/tolerances and study design need locking.

| Experiment | Evidence collected | Remaining |
| --- | --- | --- |
| E1 numerical conversions | Production GeoUtils in headless Mono and native Unity Editor 6000.3.7f1, 22,719 samples in each direction, independent PROJ comparisons | Final supported domain/tolerance lock; additional sites/devices if claimed |
| E2 map manipulation / persistence | Existing regressions; native scene collector and prepared pilot procedure | Run the collector locally, inspect deviations/outbox operations, extended cycles, manual replay and actual network no-op trial |
| E3 surface-relative height | Synthetic native regression checks and collider-state regression coverage | Batch metrics across poses, slope/misses/disabled colliders/multilevel cases and reference measured heights |
| E4 geographic registration / representation | Calibration fit statistics only (not held-out accuracy) | Independent checkpoints, reference uncertainty/datum and mesh-versus-splat comparison |
| E5 synchronization faults | One guided real offline delete/create/reconnect/restart scenario passed; core/outbox/coordinator regressions | Two-client fault matrix, explicit final-state oracles, repeated trials, latency, duplicates/lost edits/conflicts and race outcomes |
| E6 polygon validation | Implemented validator and regressions | Publish fixture pass/fail table and exercise draft/UI rejection behavior |
| E7 user placement / workload / usability | No participant experiment collected | Matched baseline/tasks, study protocol, sample-size justification, completion time/error, NASA-TLX and SUS |
| E8 scale / performance | Splat-count metadata and mesh extent known; previous hitch/save-stage diagnostics | Native build/device frame-time captures and scaling against feature/vertex count; sync-time scaling |

## Completed E1 pilot

Harokopio inputs: supplied alignment, OBJ and splat asset metadata. The sample
set includes all 12,469 OBJ positions, mesh and asset bounding-box grids and
five fixed random seeds. Asset bounding-box frame equivalence is an unverified
stress assumption. Overlapping sample groups are not independent checkpoints.

| Native Unity round trip | p95 3D (mm) | Maximum 3D (mm) | Nonfinite | Above 10 mm |
| --- | ---: | ---: | ---: | ---: |
| Geographic -> reconstruction -> geographic | 0.0924383 | 0.218664 | 0 | 0 |
| Reconstruction -> geographic -> reconstruction | 0.0884888 | 0.200227 | 0 | 0 |

All 22,719 samples per direction passed the provisional 10 mm numerical budget.
Independent one-way conversion comparisons also passed. Raw/source/input hashes,
row identity and counts were checked. The uploaded native capture SHA-256 is
`5b768e715d49a82629effabb9366fba0fc218ecf59be04432f7298f3a9da21cb`.
The full evidence archive is named `harokopio_numerical_unity_20261026.zip`.

Headless results differ numerically (maxima 0.430420 and 0.450511 mm), so runtime
profiles remain separate. The cause was not isolated. Repeated development runs
and paired runtime configurations are not independent scientific repetitions.

This establishes sampled numerical conversion consistency. It does not measure
physical geographic accuracy, scene manipulation, load/save drift, height,
network reliability, frame rate or user performance.

Next: [run the native scene consistency pilot](SCENE_CONSISTENCY_EXPERIMENT.md).
The full design remains in [PAPER_EVALUATION_PROTOCOL.md](PAPER_EVALUATION_PROTOCOL.md).

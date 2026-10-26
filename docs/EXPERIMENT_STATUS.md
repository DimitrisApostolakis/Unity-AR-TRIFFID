# Paper experiment status (26 October 2026)

Results below are pilot evidence for the stated site/runtime. The evaluation
protocol remains a draft; final domains/tolerances and study design need locking.

| Experiment | Evidence collected | Remaining |
| --- | --- | --- |
| E1 numerical conversions | Production GeoUtils in headless Mono and native Unity Editor 6000.3.7f1, 22,719 samples in each direction, independent PROJ comparisons | Final supported domain/tolerance lock; additional sites/devices if claimed |
| E2 map manipulation / persistence | Native 100- and 1,000-cycle captures passed normal-position and exact geographic-persistence checks; failed original baseline retained | Manual XR replay and active-network no-op trial; far-origin stress display errors remain a measured limitation |
| E3 surface-relative height | Synthetic native regression checks and collider-state regression coverage | Batch metrics across poses, slope/misses/disabled colliders/multilevel cases and reference measured heights |
| E4 geographic registration / representation | Calibration fit statistics only (not held-out accuracy) | Independent checkpoints, reference uncertainty/datum and mesh-versus-splat comparison |
| E5 synchronization faults | Existing offline pilot/regressions; isolated active-sync no-op + one-point-edit collector ready | Run the new native pilot, then create/delete/recovery/restart and the repeated two-client fault matrix; measure latency separately |
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

The E2 capture `Capture_20261026T032049Z_f1ec03c8.zip` completed with restored
source state but failed the no-op criteria. It produced 4,474 new outbox operations
overall, including 4,364 in normal measured poses/cycles after warmup. The archive
remains the failed comparison baseline. The fix retains geographic feature data
on load/save and commits only explicitly edited vertices.

## Completed E2 persistence extension

| Native capture | Cycles per condition | Unedited saves | Coordinate observations | Stored-coordinate changes | New unintended operations | Max normal world-position error |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `Capture_20261026T183318Z_f2cf7904` | 100 | 449 | 14,198 | 0 | 0 | 1.224 mm |
| `Capture_20261026T183906Z_49d86a10` | 1,000 | 4,049 | 125,798 | 0 | 0 | 1.156 mm |

Each run used four persistence conditions and 44 planned map poses. All primary
normal-condition measurements passed the unchanged provisional 10 mm budget.
The extended run retained all input feature data at all 20 checkpoints, with
zero cycle-to-cycle drift. Deterministic point, line-vertex and polygon-closure
edits saved/reloaded successfully with no extra operations on repeated saves.
Source restoration and hashes were verified. Captured parent poses differ
between runs; their absolute transform errors are not strictly paired values.

The far-origin miniature stress condition still reaches approximately 0.882 m
scene/world error. Storage remains exact in those cases. Stress failures remain
reported separately. These disconnected fixtures do not measure actual HTTP
uploads, human XR interaction, physical geographic accuracy or surface height.

Extended capture archive SHA-256:
`466d2cbd4bd4faba671b57bd705769b6abad693992664e9bc9bcb6870c9f0629`.

Next: [run the active-sync correctness pilot](SYNC_PILOT_EXPERIMENT.md).
The full design remains in [PAPER_EVALUATION_PROTOCOL.md](PAPER_EVALUATION_PROTOCOL.md).


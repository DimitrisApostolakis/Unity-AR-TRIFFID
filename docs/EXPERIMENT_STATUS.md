# Paper experiment status (26 October 2026)

Results below are pilot evidence for the stated site/runtime. The evaluation
protocol remains a draft; final domains/tolerances and study design need locking.

| Experiment | Evidence collected | Remaining |
| --- | --- | --- |
| E1 numerical conversions | Production GeoUtils in headless Mono and native Unity Editor 6000.3.7f1, 22,719 samples in each direction, independent PROJ comparisons | Final supported domain/tolerance lock; additional sites/devices if claimed |
| E2 map manipulation / persistence | Native 100- and 1,000-cycle captures passed normal-position and exact geographic-persistence checks; active-network no-op capture verified; failed original baseline retained | Manual XR replay; far-origin stress display errors remain a measured limitation |
| E3 surface-relative height | Synthetic native regression checks; new 2,160-observation in-app collector/analyzer ready | Collect/verify the batch across poses, slopes, misses, disabled colliders and intended floors; independent measured heights remain separate |
| E4 geographic registration / representation | Calibration fit statistics only (not held-out accuracy) | Independent checkpoints, reference uncertainty/datum and mesh-versus-splat comparison |
| E5 synchronization faults | Native online no-op + one-point-edit capture verified; new isolated offline-update/recovery observer ready | Collect recovery pilot, then create/delete/restart, lost-response and repeated two-client fault matrix; instrument callback latency separately |
| E6 polygon validation | Validator/regressions; native eleven-fixture table/export ready | Collect/verify fixture table and exercise draft/UI rejection behavior |
| E7 user placement / workload / usability | No participant experiment collected; studies deferred by user | Resume participant protocol/design later |
| E8 scale / performance | New native Editor frame/save collector ready with baseline and 10/100/1,000 mixed-geometry workloads | Collect/verify Editor pilot; standalone build/device frame-time captures, manipulation and sync-time scaling remain separate |

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

## Verified native online synchronization pilot

Capture `Capture_20261026T195817Z_e7777ed9.zip` passed integrity/source, native
action, unchanged-save, HTTP/payload and final data/outbox checks. All 48 recorded
runtime/observer source hashes matched the corrected source. Across 23 features,
startup and unchanged saving produced zero feature uploads and zero new no-op
outbox operations. Exactly one target PATCH returned HTTP 200. Only
`e2_point_00` changed; the other 22 features, including the foreign-class probe,
remained exact. Final local/server/payload data agreed and the outbox was empty.
The server did not gain `confidence: null`. Confirmation lasted 13.25 seconds;
that window is not delivery latency. Height status was `no_surface`, so no
surface-height accuracy is inferred. This remains one normal-online correctness
pilot, not the repeated fault/restart/two-client experiment.

Archive SHA-256:
`e7de7577a6459fe05f184bdc2ee6c76bc5da8893cd7f6db3073010e1899a65e4`.

Next: [run the in-app technical captures](APP_NUMERICAL_EXPERIMENTS.md).
The full design remains in [PAPER_EVALUATION_PROTOCOL.md](PAPER_EVALUATION_PROTOCOL.md).


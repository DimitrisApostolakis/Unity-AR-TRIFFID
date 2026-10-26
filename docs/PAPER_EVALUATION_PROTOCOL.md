# Paper evaluation protocol

Status: DRAFT for pilot preparation, not a preregistration or experimental result.
Runtime baseline inspected: experiments commit 60baf10f0cb23fdc860975d8dc55bbc1c399793f.
No application behavior is changed by this document. Final thresholds, scene
ranges, participant sample size and study baseline remain to be locked before
confirmatory data collection. Pilots must be identified separately from final runs.

## Questions and separation of measurements

1. Does the numerical conversion preserve locations in both directions?
2. Do map manipulation and unedited load/save cycles preserve coordinates and
   signed surface-relative height without generating uploads?
3. Does synchronization reach a scenario-specific expected state under faults?
4. How much observed positional error comes from registration, disagreement
   between mesh and visible splats, and human placement?
5. How do tasks, workload, usability and performance compare with a suitable baseline?

Round-trip consistency cannot establish absolute calibration accuracy. Shared
forward/inverse errors can cancel. The display registration must be checked
against independent checkpoints, and conversion tests should also include known
analytic cases or an independent double-precision geodesy reference.

RMSE remains meaningful when the ideal error is zero, but it is secondary for
invariance. Maxima, distributions, threshold exceedances and categorical failures
are primary. Do not replace observed failures with a favorable average.

## Decisions to lock before final runs

| Item | Required decision |
| --- | --- |
| Dataset | Scene, site extent in metric ENU, vertical range, splat/mesh assets and hashes |
| Alignment | Actual JSON, origin/datum, metric scale, rotation and translation; fitting method and control locations |
| Sample locations | Fixed IDs, site-wide distribution including corners/edges, geometry/vertex identity and independent or diagnostic status |
| Supported display domain | Tabletop and full-size views, hierarchy, reflections, scale limits and origin offsets |
| Numerical tolerance | Separate horizontal, vertical and height budgets; justified against intended use and reference uncertainty |
| Sync setup | Actual Inspector polling/debounce/retry/timeout values, server semantics, two-client arrangement and fault schedule |
| Sync time limit | Fixed convergence deadline from health-confirmed recovery; report all failures/timeouts |
| Human study | Baseline, tasks, participant population, primary outcome, meaningful effect and sample-size justification |
| Performance | Build, headset/refresh target, hardware, rendering settings, views, warm-up and capture duration |

Do not choose these after inspecting final results. A 0.01 m numerical threshold
can be used as an explicitly provisional pilot engineering target; it is not an
established mission tolerance. Final tolerances must be fixed independently of
final errors. Being smaller than an uncertain reference alone is insufficient.

## Common metrics and reporting

Calculate differences in double precision in one stated metric frame, using
fixed axis order East, North, Up. Do not subtract longitude/latitude degrees
and label the result metres. Do not round values through the information panel.

For signed residuals dE, dN, dU, report:

- horizontal magnitude eH = sqrt(dE^2 + dN^2);
- vertical absolute error eV = abs(dU), with signed vertical bias mean(dU);
- 3D magnitude e3 = sqrt(dE^2 + dN^2 + dU^2);
- horizontal bias vector (mean(dE), mean(dN));
- horizontal, vertical and 3D RMSE where relevant;
- maximum, median and empirical 95th percentile, with the quantile convention stated.

R95 means the empirical 95th percentile of horizontal distance from the true
reference location; CEP50 is the corresponding 50th percentile. State the centre
explicitly: do not subtract the bias and then present the radius as total accuracy.
Avoid Gaussian conversion factors unless their assumptions are justified.
With few independent checkpoints, R95 is imprecise. Report its interval and sample
size, and describe insufficient tail information rather than presenting false precision.

Use log-scale error distributions/ECDFs for numerical tests. Show the number of
exact zeros separately; any plot-only floor must be disclosed and must not alter
raw data or threshold decisions. Preserve per-location/per-sequence observations.
Repeated vertices, poses and frames are not independent checkpoints or participants.
Use run-, site-, checkpoint- or participant-level intervals as appropriate, retaining
that dependence; do not pool thousands of correlated samples as independent trials.

## E1. Numerical round trip (no scene required)

Production code: Runtime/Geospatial/Coordinates/GeoUtils.cs.
Compile and execute that C# code; do not substitute a Python reimplementation of
the system under test. A minimal float Vector3 carrier can support a headless
arithmetic run, but must be disclosed. It does not validate native Unity matrices,
physics or rendering. Confirm the arithmetic results in the native Unity runtime.

Inputs: actual calibration JSON and site bounds. Check origin, positive metric
scale, finite coefficients, determinant and R-transpose-R residuals before the
benchmark. Reject or report an invalid calibration; do not silently repair it.
Record the float-rounded coefficients actually used by the application.

Procedure:

1. Use fixed grid/boundary/corner locations plus reproducible random interior
   samples across the actual site and height range. Include supplied POI and
   annotation-vertex locations with resolved altitude.
2. Run WGS84 -> reconstruction -> WGS84. Measure return error against the input
   using independent double-precision metric residual calculations.
3. Separately run reconstruction -> WGS84 -> reconstruction. Convert the
   reconstruction residual to metric ENU using the stated similarity transform.
4. Keep both directions and valid/invalid-input results separate. Record seed,
   bounds, calibration hash, runtime/compiler and individual input/output values.
5. Include independently known analytic points so a mutually consistent but
   wrong forward/inverse pair is not treated as proof of geographic correctness.

Primary metrics: max and p95 horizontal/vertical/3D error, distributions,
nonfinite/failed conversion count and threshold exceedance count. RMSE is secondary.
Criterion: every valid sample within the predeclared budgets; zero nonfinite
outputs; invalid inputs rejected according to their declared contract. This is a
numerical criterion, not geographic validation. Current invalid-input helpers may
return a zero vector, so the measurement harness must validate inputs explicitly
and must not score an invalid case as a successful location at the origin.

## E2. Coordinate invariance and unedited persistence

Use actual scene objects and the production world-to-map, save and reload paths.
The arithmetic round-trip runner alone cannot perform this experiment.

Suggested pilot: 30 distinct locations across the entire site, including edges,
POIs, line vertices and polygon vertices; five fixed transformation sequences.
Increase coverage based on the intended operating domain, not on favorable results.

Conditions to record explicitly:

- initial display, tabletop view and a metrically calibrated full-size view;
- translations and rotations separately, then combined, followed by return;
- minimum/maximum supported uniform scales and supported hierarchy/reflection cases;
- near-origin and far-origin displays; proposed stress offsets 0, 100, 1000 and
  10000 world units, reported separately when outside normal usage;
- repeated map manipulations and 0, 1, 10, 100 and 1000 unedited load/save cycles.

Do not equate Transform.localScale == 1 with full metric scale. The reconstruction
scale and complete hierarchy determine ground metres per world unit. Record that
mapping. Curvature/calibration accuracy requires E4 even if E2 passes everywhere.

Freeze sync and telemetry for geometric measurements. Then run a separate
instrumented no-op save trial with sync active to count outgoing feature writes.
Initialize a clean acknowledged baseline and record first-load behavior too.
Loading must not persist surface/provenance changes or generate feature uploads;
do not hide such changes by repeatedly settling/acknowledging them. Compare
feature semantics without volatile timestamps/outbox
bookkeeping, but log the actual payloads and real network writes as well.

For every pose/cycle, measure recomputed scene-derived coordinates against both
the initial input and initial scene baseline. For an unedited feature, do not
merely compare its untouched stored coordinates. Record signed per-cycle increments,
cumulative drift, feature/vertex identity and every outgoing feature operation.
Replay a small set with a person using the supported map interaction controls.

Primary metrics: max/distribution/p95 location deviation, cumulative drift versus
cycle count, per-cycle drift, spurious outbox operations and spurious uploads.
Criterion: no location exceeds the locked budgets; zero spurious feature uploads
in settled unedited trials. Averages do not excuse an exceeding location. No-op
metadata changes must not be counted as intentional edits.

## E3. Height consistency, height accuracy and surface selection

Separate three questions: stable numerical height under manipulation; agreement
with a known surface/height; and whether the chosen surface is the intended floor.

Fixtures: analytic plane with exact signed offsets (below, on, above), sloped
surface, bounded surface with misses, enabled/disabled collider, and a two-level
or roofed fixture. For the multi-level case, label the intended surface before
running and record which surface the ray actually selects.

Run E2 pose sequences for fixed marker/surface locations, then test real measured
heights with stated measurement uncertainty and vertical datum. Treat estimated,
retained, mesh-derived and unavailable outcomes separately. A status indicating
an estimate is not an independent mesh measurement. Small visual marker lifts
must be recorded when defining an expected height.

Metrics: signed bias, MAE, max/p95 height error, deviation from the baseline H,
query failure rate, intended-surface selection rate, and status/provenance confusion
counts. Report errors on valid hits together with coverage/failures, never only
on successful hits. Misses must preserve coordinates and show unavailable status;
disabled-collider state must be restored after hits, misses and exceptions.

Criterion: fixed numeric budgets for plane/slope and consistency cases, correct
status on every controlled miss, restored collider state, and intended-surface
agreement for cases advertised as supported. First-hit failures in multi-level
scenes are measured limitations, not passes simply because a ray hit something.

## E4. Geographic and representation accuracy

### E4a. Registration

Use held-out checkpoints never used to estimate or tune the similarity transform.
State RTK/total-station/survey method, reference uncertainty, horizontal/vertical
datum and any geoid conversion. SRT/GNSS camera metadata used in fitting is not
an independent high-accuracy checkpoint source. Without reliable height datum,
report horizontal results and leave absolute vertical accuracy unassessed.

Prefer a target substantially more accurate than the expected system error and
report both uncertainties. Do not claim ASPRS compliance just for using RMSE or
having 30 samples. Fit/tune only on controls, and reserve independent checkpoints.

Establish each checkpoint's reconstruction-space feature correspondence without
participant pointing. Report observer/identification uncertainty separately.
Evaluate full-pipeline display projection at the declared map scales, accounting
for E2 numerical error. Report error versus distance to nearest control point,
position within the site, and map scale. Repeated scales do not add independent
checkpoints. A spatially wrong calibration may pass every round-trip test.

Metrics: horizontal bias vector, vertical bias, horizontal/vertical/3D errors,
RMSE, median, max and R95 with intervals and independent checkpoint counts.
Criterion: locked application tolerances and stated uncertainty; until those are
available report a descriptive accuracy assessment, not an unsupported mission pass.

### E4b. Mesh versus visible splats

Use fixed calibrated views and corresponding identifiable landmarks/surfaces.
Compare the collider hit with the visible feature, separating this from survey
registration. State how a splat reference surface is obtained: splats do not
inherently provide a unique ground-truth mesh surface. Use a documented depth
estimator or an independent 3D reference if claiming metric surface distances.
Otherwise report calibrated image-space displacement in pixels, views and
annotation uncertainty; do not relabel it as 3D metres.

Metrics: per-view landmark/reprojection disagreement and, where independently
supported, signed/absolute 3D surface disagreement. Plot spatial distribution and
report missing/ambiguous correspondences. Criterion: separately predeclared visible
placement/representation tolerance, without subtracting unexplained errors to
claim causal decomposition.

### E4c. User placement

Ask participants to place/correct known targets and annotate matched routes/areas
at the declared scales. Log intended geographic target and actual selected surface
hit. Total error includes registration, representation and pointing. Report
pointing-relative-to-model error separately when model target coordinates are known.
Do not subtract RMSEs to infer a pointing contribution without an error model.
This condition can be embedded in E7, rather than recruiting another study.

## E5. Synchronization with explicit oracles

Client A: actual Unity app and its durable local outbox. Client B: a separate HTTP
actor representing the front end, or a second application when testing multiple
AR clients. A single Unity client plus an otherwise passive server is insufficient.
Keep transport/proxy logs for both clients, with a shared monotonic timebase or
stated clock synchronization and observation resolution.

Current local server: PATCH/DELETE operations are individually protected by a
server lock, but there is no atomic version precondition tying a preceding GET
to a later write. The C# client sends no If-Match/version condition. Production API
concurrency semantics need independent confirmation. An Idempotency-Key header is
not proof that the server enforces idempotency, and it is not version checking.

For each trial, define initial feature IDs, contents, local/remote revisions,
operation order, fault trigger, expected server state, expected local state and
expected pending-state category before execution. Define a convergence deadline
and require a fixed quiet confirmation window after convergence. Use isolated
copies of data, not the working demo database.

Suggested pilot/final matrix target: 20 independently reset repetitions per
scenario/schedule, including jittered polling phase. This is not a guarantee of
reliability. Report successes/attempts and an exact binomial confidence interval.
For 20/20, the two-sided 95% exact interval is approximately 83.2%-100%.

| ID | Injection / actors | Expected state or oracle |
| --- | --- | --- |
| S01 | Online create/update/delete; A then B | Exactly the specified surviving IDs and final contents; relevant outboxes acknowledged |
| S02 | Request dropped before reaching server | No server effect before retry; pending work durable; one intended logical effect after recovery |
| S03 | Response lost after create/update/delete committed | Reconcile actual committed outcome; no duplicated create or rollback of a newer local revision |
| S04 | HTTP 500/503, timeout, invalid JSON, invalid/partial collection | Preserve local backup on failed fetch; do not interpret malformed data as a valid empty collection; pending work retained/retried or explicitly failed |
| S05 | Valid empty server, no pending edits | Remove previously synchronized/legacy features; resulting collection empty |
| S06 | Valid empty server with unsent create | Retain/deliver the new create; do not recreate other server-deleted records |
| S07 | Valid empty server with pending modification/deletion | Server absence supersedes modification; deletion acknowledged as already absent; uncertain creates remain distinguishable from unsent creates |
| S08 | Force-kill A during temp write/replacement and restart | Recover an intact completed snapshot/backup according to commit point; no truncated JSON; no loss of already committed pending work. An edit killed before durable commit is outside the durable guarantee |
| S09 | A edits offline; B edits same record before A comparison | B's server version wins; A does not upload its stale modification; record whether A user is notified |
| S10 | A modifies, B deletes; and A deletes, B modifies before comparison | Server deletion wins the first schedule; observed server modification supersedes pending local deletion in the second |
| S11 | B changes/deletes after A GET but before A PATCH/DELETE | Enumerate schedules. Measure overwrite/conflict/surface notification; unconditional last-applied request can overwrite B or delete its changed record. Do not claim race-free server priority |
| S12 | New local edit while earlier A request/ack is delayed | Earlier acknowledgement cannot clear newer revision; newest allowed intent eventually delivered |
| S13 | App restarts with in-flight/ambiguous operation | Restore pending state and reconcile outcome; no blind duplicated create |
| S14 | Idle polling and unedited load/save cycles with both actors | Zero spurious feature uploads/false conflicts; observer payloads follow recorded role convention |

For S11 the safety goal and current capability are separate: if protecting B's
intervening write is a requirement, an observed overwrite is a safety failure.
Converging to the last server state is not evidence that the lost-update race was
prevented. Strong protection needs a server-supported conditional write/version
check or an equivalent atomic protocol, and would be a separate implementation.

Safety and liveness are separate: retaining an uncertain create without a
duplicate can satisfy safety while failing automatic delivery. A pending or
reconciliation state must not count as delivered convergence unless that
scenario explicitly calls for an unresolved/user-confirmation terminal state.
Declare that terminal state before the run, and report any manual intervention.

Inject transport faults with a controllable proxy or explicit isolated server
hooks, not by assuming a refused TCP connection reproduces a lost response.
Killing a coroutine is not a process-kill-mid-write test. Keep request-drop and
response-loss trials distinct. Confirm what actually reached disk/server.

Timing:

- Normal outgoing: durable local save -> Unity observes successful server
  acknowledgement. Separately log first server acceptance and ambiguous outcomes.
- Incoming: confirmed B server commit -> A applies/visibly presents the update;
  label data-applied and marker-visible timings separately if both are measured.
- Recovery: independent health-confirmed API availability -> all expected
  server/client/outbox conditions first hold. The quiet confirmation window is
  reported separately and is not added to convergence latency.

Report median, p95, observed maximum, failures and timeout count by scenario;
the observed maximum is not a theoretical worst-case bound. State polling,
debounce, retry/backoff, callback timeout and server delay. Log actual API requests,
feature writes, observer PATCHs, retries and counts per idle minute.

Metrics: exact oracle agreement; duplicates; unexpected loss of edits that the
oracle says should survive; false-conflict counts under noncompeting histories;
spurious uploads without an intentional semantic edit; and user-visible notification
when server priority supersedes an edit. General debug logs are not evidence of
an in-headset notification. All invalid/missing/timeout trials remain in the denominator.

Criterion: all deterministic supported oracle cases meet state/durability
requirements within the locked deadline, with zero unexpected loss, duplicates,
false conflicts and spurious writes. Report observed success counts/intervals.
Conflicting schedules have explicit expected outcomes; S11 and unsupported server
semantics must not be disguised as successful protection.

## E6. Polygon validation

Publish a compact input/expected/actual/pass table: self-intersection, overlapping
edges, repeated vertices, zero area, invalid/nonfinite coordinates, insufficient
vertices, exterior CCW/hole CW, closure, and metadata reordering. Invalid input
must leave the draft editable or cancellable. Existing unit tests cover several
of these; report which execute the validator alone and which execute the Unity UI.
Criterion: exact expected accept/reject, orientation and metadata outcome for every
fixture. These are correctness checks, not participant placement-accuracy evidence.

## E7. Task-based user study

Primary interaction: controllers, matching the draft's drawing/editing workflow.
Optional hand inspection must be treated separately if evaluated; do not claim a
full hand-based editing comparison that the app does not support.

Pilot suggestion: 4-6 participants to test tasks and estimate paired variability.
Choose final recruitment and sample size using the primary paired outcome, a
predeclared meaningful effect, variability estimate and feasibility; do not claim
that 20 participants automatically supplies sufficient power. Record relevant
responder/GIS/XR experience, recruitment and exclusions. Nonresponders do not
establish operational responder effectiveness.

Within-subject conditions: AR versus a specified 2D baseline capable of the same
measured operations. Supply the same information, geographic targets, data age,
editing affordances and target metadata. If the baseline cannot draw polygons,
there is no fair polygon-editing comparison. Counterbalance condition order and
use matched alternative targets to reduce learning and carry-over. Provide training
and a practice task before scored tasks.

Tasks: find/inspect a detection; correct a displaced POI; place a new location;
trace a route; mark an area. Define start/end, success, acceptable target tolerance,
error recovery, time limit and required assistance before collection. Include
several map scales for the placement condition without creating an underpowered
large factorial design; decide the primary contrasts in advance.

Outcomes: completion/timeouts, time, placement error, task-specific route/area
quality if scored, mistakes/assistance, NASA-TLX and SUS. Specify weighted TLX or
Raw TLX rather than interchanging them; administer/score the chosen instrument
consistently. Use the original SUS scoring and a stated translation. SUS is a
0-100 usability score, not percent task success. These questionnaires do not
measure operational safety or real-world rescue performance.

Analyze participant-paired condition differences with effect sizes and intervals,
a declared primary analysis, treatment of incomplete tasks and secondary comparisons.
Criterion: the declared comparative hypotheses/analysis, not an invented universal
SUS cutoff or a requirement that AR must win every measure. Null or adverse
results remain reportable.

## E8. Site scale and performance

Report site extent in metres, splat count, mesh vertices/triangles, POI count,
line/polygon count and total annotation vertices. Keep these distinct. The title's
large-scale claim must be supported by measured datasets rather than inferred
from the presence of a 3DGS renderer.

Use the same standalone build, hardware, headset/refresh target, quality settings,
viewpoints and warm-up/capture intervals. Measure actual rendering; headless
arithmetic results are not a frame-rate benchmark. Report Editor captures separately.

Increase a declared feature/vertex workload (suggested pilot 10, 100, 1000 features)
with fixed geometry mix, geographic distribution and visible fraction. Keep splat
count/view constant when measuring annotation scaling. If multiple reconstruction
sizes are available, report that as a separate factor rather than conflating them.

Measure idle, manipulation, save and synchronization separately. Record frame-time
median/p95/p99/max, FPS summaries, main-thread/save allocations and stalls where
available, memory and save-to-ack/convergence time. Correlate save/network events
with frame spikes. State batch size, poll phase and request counts. Report stalls,
failures and limits rather than omitting overloaded trials.

Criterion: predeclared frame-time budget for the chosen refresh target and
application-specific synchronization deadline at the advertised workload. Until
these are locked, report scaling curves rather than claiming an operational pass.

## Existing evidence versus missing measurement tools

| Area | Existing evidence | Still needed |
| --- | --- | --- |
| Arithmetic | Production GeoUtils; E1 native/headless collector and Harokopio pilot, PROJ reference and raw-output metadata | Final tolerance/domain lock; additional claimed devices/sites |
| Manipulation/height | Native synthetic surface checks, engine-double regressions and E2 scene/persistence collector | Local E2 capture, multilevel/height fixtures, actual network no-op counts and manual replay |
| Sync | Core/outbox/coordinator regressions; guided real local HTTP outage test | Two actors, programmable fault injection, process-kill trials, scenario oracle files and timing/notification logs |
| Polygons | Validator orientation/intersection/degeneracy/nonfinite/metadata regressions | Published fixture table and draft/UI rejection checks |
| Accuracy | Coordinate workflow and calibration input | Independent survey checkpoints, uncertainty/datum and representation correspondences |
| Users/performance | Functional application | Chosen baseline, study protocol and measured rendering/workload captures |

The current guided outage test's reconnection_seconds includes startup and its
settling window. Do not reuse it directly as publication-quality recovery latency.
An existing PASS output is a correctness observation, not a completed paper benchmark.

## Output contract

Keep immutable raw inputs, configuration hashes, commit/build/runtime/hardware,
seed/sample/sequence IDs, metric axis definitions and thresholds with every run.
Export individual coordinate/height observations; per-cycle drift; timestamped
client/server/proxy operations; expected-versus-observed state; notification events;
summary CSV/JSON; failures and plots. Keep pilots separate from final data.
Exclude secrets/credentials and unrelated participant data from shared artifacts.

## Immediate next input and execution order

Start with E1. No active scene or headset is required for the arithmetic stage.
The committed project_paths.json references a transform JSON on a network share;
that actual alignment file is not in this repository. Obtain the JSON really used
by JsonSpawner (its serialized key/path can differ from the aligner) and metric
site bounds or a representative GeoJSON with resolved heights covering the site.
Do not assume a small set of current POIs covers the reconstruction's full extent.
The committed shared config does not contain JsonSpawner's default
json_spawner_transform_path key, so resolve its actual scene configuration explicitly.

Then implement and validate the E1 collector, followed by E2/E3 native Unity batch
collectors and the E5 two-client fault runner. Final measured E4 accuracy needs
independent reference acquisition. Prepare E7 while collecting technical pilots;
freeze its design before participant recruitment/data collection.

## Primary methodological references

- NIST, exact binomial confidence limits:
  https://itl.nist.gov/div898/software/dataplot/refman2/auxillar/exacbici.htm
- NASA, official Task Load Index and instructions:
  https://www.nasa.gov/human-systems-integration-division/nasa-task-load-index-tlx/
- Brooke (1996), original SUS chapter:
  https://www.taylorfrancis.com/chapters/edit/10.1201/9781498710411-35/sus-quick-dirty-usability-scale-john-brooke
- ASPRS, overview of positional accuracy standards and independent checkpoints:
  https://my.asprs.org/Common/Uploaded%20files/PERS/HLA/HLA%202025-05.pdf

These references inform measurement/reporting; they do not validate this system
or substitute for locking the experiment-specific decisions above.

## E1 collector implementation

The [numerical collector guide](NUMERICAL_EXPERIMENT.md) documents the implemented
C# collector, independent PROJ reference, native Unity and headless entry points,
sampling and raw output. A native Unity Harokopio pilot was analyzed on 26 October
2026. [Experiment status](EXPERIMENT_STATUS.md) distinguishes that numerical
evidence from remaining geographic validation and final design decisions.

The [scene consistency guide](SCENE_CONSISTENCY_EXPERIMENT.md) documents the E2
collector, inactive fixture in the actual map hierarchy, explicit one-cycle warmup,
production save/reload path, per-cycle drift and outbox measurements. Real network
uploads and a manual manipulation replay remain separate trials.


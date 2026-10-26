# Native height and geometry capture: 26 October 2026

Capture: `AppNumerics_20261026T204743Z_0d4b3b69`.
Uploaded archive: `AppNumerics_20261026T204743Z_0d4b3b69(1).zip`.
SHA-256: `1528415f230b10abbfa54bd6afe1e49b754bc7171dc1ca32862fed04b7a8f8a1`.
Runtime: Unity Editor **6000.3.7f1**, Harokopio scene.

All output-integrity checks passed. All 52 recorded source hashes match the
published collector/runtime sources. Coverage is complete: 2,160 surface
observations, 11 polygon fixtures and 63 deterministic core checks. Source feature
data, map matrix and save-file hash were unchanged after collection.

**Overall predeclared outcome: FAILED_NORMAL.** The original capture remains a
failure baseline. Neither the 5 mm synthetic height budget nor the normal/stress
scope assignments have been changed after seeing these results.

## Measurements

| Condition | Observations | Result |
| --- | ---: | --- |
| Flat/sloped surfaces, poses 0-3, both calibrations/collider states | 480 | Zero 5 mm exceedances; median 0.001888 mm, p95 0.680381 mm, max 1.297507 mm |
| Flat/sloped surfaces, pose 4, rotated children under a nonuniform parent | 120 | 96 height-budget failures; max 1.221447962 m |
| Controlled misses, inactive surfaces and missing colliders, all poses | 1,080 | Zero unexpected hits; all unavailable/status outcomes correct |
| Collider enabled-state restoration, all cases | 2,160 | Zero failures |
| Intended lower floor in two-level fixtures, non-stress poses | 300 | 300 intended-floor failures; first-hit surface selection does not find the intended lower floor |
| Far-origin miniature stress, pose 5 | 360 | 180 failed expected height/hit outcomes; includes large height errors and 90 missing expected site-calibration hits |
| Polygon fixtures | 11 | All passed acceptance, winding, closure, raw metadata and rejection-preservation checks |
| Deterministic native core checks | 63 | All passed; fake-server cases remain state checks, not real HTTP fault measurements |

The complete normal single-level error distribution (including pose 4) has p95
0.832825441 m and max 1.221447962 m. Its invariance check has 96 exceedances too.
The poses 0-3 subgroup above is a diagnostic breakdown, not a replacement for
the failed predeclared aggregate criterion. Successful hits are reported with
coverage/misses; missing heights have not been dropped to manufacture a pass.

## Diagnosis and controlled next step

Every normal single-level exceedance belongs to pose 4. This combines parent
scale `(0.8,1.2,0.9)` with a rotated/reflected/nonuniform child. The captured
matrix has maximum normalized axis dot product 0.199085, confirming shear;
the other poses are orthogonal to approximately 1e-7 or better.

An independent matrix calculation reconstructed the pose-4 identity-calibration
flat-plane errors using a collider transform made from the composed rotation
and signed column-length scale, rather than the full sheared matrix. It agrees
with the 60 enabled/disabled flat-plane observations within 1.276 micrometres,
without fitting parameters. This strongly supports a physics-collider versus
full-transform geometry mismatch; it is not ordinary coordinate rounding drift.

Unity's official [6000.3.15f1 release notes](https://unity.com/releases/editor/whats-new/6000.3.15f1)
list a fix for MeshCollider handling of nonuniform scale, issue
[UUM-139681](https://issuetracker.unity3d.com/issues/mesh-collider-does-not-skew-slash-shear-correctly-when-nested-inside-another-gameobject).
This is a matching engine-defect hypothesis, not a confirmed attribution from
the version comparison yet. The controlled next run is the same unchanged
collector, fixture cases and budgets on a project copy opened with 6000.3.15f1.
Keep the old capture and label all results by engine version. No runtime code,
fixture geometry, scope or tolerance change is needed for that comparison.

The two-level first-hit behavior and far-origin float limitations are separate
from the nonuniform-hierarchy failure. An engine fix must not be assumed to
resolve those cases. Surface fixtures establish synthetic numeric behavior,
not real geographic accuracy, participant performance or HTTP reliability.

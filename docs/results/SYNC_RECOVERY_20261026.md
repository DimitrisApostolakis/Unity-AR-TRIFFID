# Verified native offline update and recovery pilot

Capture: `Capture_20261026T231106Z_6a3d1d04`.
Archive SHA-256: `83aca51a8daaade545fd33ba820b63f2671d99a0830d88cdfa8d31151e5ca5a7`.
Unity 6000.3.15f1, private local HTTP server, Harokopio alignment.
Captured 26 October UTC / 27 October 2026 Europe/Athens.

**Verified outcome: PASSED**, one guided native point-update/outage/recovery pilot.
All recorded file hashes passed. All 48 prepared runtime/source hashes match the
current source. The captured recovery observer matches the published script after
normalizing Windows/Unix line endings. This verification did not modify the archive.

| Check | Verified evidence |
| --- | --- |
| Initial state | All 23 private fixture features acknowledged and settled |
| Unchanged save | No feature writes or new outbox operations; observation 13.399139 s with five actual Unity feature reads |
| Durable offline edit | Exactly one pending update for `e2_point_00`; 21 saved-state observations cover 5.023839 s with identical edited feature data |
| Server while offline | All 23 features remain at baseline; no feature write before recovery |
| Recovery | Exactly one feature PATCH, HTTP 200; payload equals the frozen offline local feature |
| Unrelated features | Other 22 features unchanged, including unsupported-class ID, nested foreign data and extra properties |
| Target properties | Protected properties retained; absent confidence stays absent; normal altitude/height-status metadata is added by the deliberate edit |
| Final state | All 23 features match across offline local data, final local file and server database; pending outbox empty; nextRevision 1 to 2 |
| Observer publication | Role/status checks passed; status PATCH requests are distinguished from feature PATCH requests |
| Recovery confirmation | 13.25 s quiet confirmation with remote polling configured to 5 s |

The server process IDs differ before/after the outage (42076 and 22892),
consistent with the observer stopping and restarting its owned server. The
application itself remained running; no application restart was tested.

Observed saved-local/server/outbox convergence occurred **0.282966 s after an
independent successful API health check**, sampled at 0.25 s resolution. The
13.25 s quiet confirmation is separate. This is one coarse observed convergence
time, not Unity callback latency, network-only latency or a reliability rate.

The edited point reports `heightSource=unavailable`, `heightStatus=no_surface`.
The capture therefore establishes persistence and delivery, not surface-height
accuracy. Geographic movement magnitude is not a positioning-accuracy measurement.

Remaining fault coverage includes creates, deletes, lost responses, application
restart/process termination, two-client conflicts, repeated fault schedules and
production-backend semantics. Participant studies remain deferred. No broader
fault-reliability claim follows from this single passing recovery pilot.

#!/usr/bin/env python3
"""Observe real Unity offline edits and the local HTTP server. Python 3.9+, stdlib only.

The script never edits Unity's snapshot or implements Unity's synchronization.
Use --resume for an outage you already performed, or run without it for guidance.
The local server must be stopped first. The existing database is backed up, then
used directly so successfully synchronized changes persist after this test.
"""
from __future__ import annotations

import argparse
import copy
import json
import math
import socket
import subprocess
import sys
import time
import uuid
from pathlib import Path
from urllib.error import URLError
from urllib.request import Request, urlopen

REPO = Path(__file__).resolve().parents[1]
PROJECT = REPO / "projects" / "TRIFFID"
FEATURES = "/wp-json/map-manager/v1/features"
STATUS = "/wp-json/observer-sync/v1/status"
AR_FLAGS = {"fe_updated": 0, "mobile_updated": 0, "ar_updated": 1}


class TestFailure(RuntimeError):
    pass


def read_json(path, encoding="utf-8-sig"):
    value = json.loads(Path(path).read_text(encoding=encoding))
    if not isinstance(value, dict) or not isinstance(value.get("features"), list):
        raise TestFailure(f"Expected a complete feature collection: {path}")
    return value


def write_json(path, value):
    Path(path).write_text(json.dumps(value, indent=2, ensure_ascii=False), encoding="utf-8")


def outbox(root):
    state = root.get("_ar2Sync")
    if not isinstance(state, dict) or state.get("schemaVersion") != 1:
        raise TestFailure("Unity snapshot has no supported _ar2Sync state. Use the updated experiments branch.")
    if not isinstance(state.get("pending"), list) or not isinstance(state.get("remoteIds"), dict):
        raise TestFailure("Unity snapshot has malformed synchronization state.")
    return state


def scenario_from_snapshot(root, delete_id=None, existing_ids=()):
    state = outbox(root)
    live = {str(f.get("id")): f for f in root["features"]}
    creates, deletes = [], {}
    for edit in state["pending"]:
        fid = str(edit.get("featureId", ""))
        if edit.get("kind") == "create" and fid in live and fid not in existing_ids:
            if edit.get("mayHaveReachedServer") or edit.get("status") in ("conflict", "reconcile", "inflight"):
                raise TestFailure(f"Create {fid} has an uncertain outcome; this test requires an ordinary unsent offline create.")
            creates.append(fid)
        if edit.get("kind") == "delete" and fid not in live and fid in state["remoteIds"]:
            if delete_id is None or fid == delete_id:
                deletes[fid] = str(state["remoteIds"][fid])
    if not creates or not deletes:
        raise TestFailure("Waiting for a durable pending create AND a deletion of a previously synchronized item.")
    return {"creates": sorted(creates), "deletes": deletes}


def comparable(feature):
    props = copy.deepcopy(feature.get("properties") or {})
    props.pop("id", None)
    props.pop("ar2_client_id", None)
    return {"type": feature.get("type", "Feature"), "geometry": feature.get("geometry"), "properties": props}


def convergence_problems(scenario, local, remote):
    state = outbox(local)
    local_features = {str(f.get("id")): f for f in local["features"]}
    remote_features = {str(f.get("id")): f for f in remote["features"]}
    pending = {str(p.get("featureId")): p for p in state["pending"]}
    problems = []
    for fid, remote_id in scenario["deletes"].items():
        if fid in local_features or remote_id in remote_features:
            problems.append(f"Deleted item {fid} is still present locally or on the server.")
        if fid in pending or fid in state["remoteIds"]:
            problems.append(f"Deletion {fid} has not been acknowledged locally.")
    for fid in scenario["creates"]:
        if fid in pending:
            edit = pending[fid]
            problems.append(f"Create {fid}: {edit.get('status')} {edit.get('error') or ''}".strip())
        binding = state["remoteIds"].get(fid)
        matches = [f for f in remote["features"] if
                   str((f.get("properties") or {}).get("ar2_client_id", "")) == fid or str(f.get("id")) == fid]
        if len(matches) != 1:
            problems.append(f"Create {fid} has {len(matches)} server records; expected exactly one.")
        if fid not in local_features or not binding or str(binding) not in remote_features:
            problems.append(f"Create {fid} is missing a local feature or confirmed server binding.")
        elif comparable(local_features[fid]) != comparable(remote_features[str(binding)]):
            problems.append(f"Create {fid} has different content locally and on the server.")
    return problems


def audit_records(path):
    if not Path(path).exists():
        return []
    result = []
    for line in Path(path).read_text(encoding="utf-8").splitlines():
        try:
            result.append(json.loads(line))
        except json.JSONDecodeError:
            pass  # A request thread may still be appending the last record.
    return result


def observer_problems(records, since_ns):
    ar = [r for r in records if r.get("time_ns", 0) >= since_ns
          and r.get("actor") != "harness" and r.get("user_agent", "").startswith("UnityPlayer")]
    writes = [r for r in ar if r["path"].startswith(FEATURES)
              and r["method"] in ("PUT", "PATCH", "DELETE")
              and (200 <= r["status"] < 300 or (r["method"] == "DELETE" and r["status"] == 404))]
    notices = [r for r in ar if r["path"] == STATUS and r["method"] == "PATCH" and 200 <= r["status"] < 300]
    publishes = [r for r in notices if r.get("body") == AR_FLAGS]
    problems = []
    if not writes:
        problems.append("No confirmed Unity feature writes recorded. Check Use Localhost, port, and the Unity client.")
    if len(publishes) != len(writes):
        problems.append(f"Observer publishes={len(publishes)}, confirmed feature writes={len(writes)}; expected one per delivered change.")
    if any(r.get("body") not in (AR_FLAGS, {"ar_updated": 0}) for r in notices):
        problems.append("Unexpected AR observer payload; expected (0,0,1) publish or ar_updated=0 acknowledgement only.")
    return problems


class LocalServer:
    def __init__(self, db, run_dir, port=8080, rewrite_ids=False):
        self.db, self.run_dir, self.port = Path(db), Path(run_dir), port
        self.rewrite_ids = rewrite_ids
        self.process = None
        self.log = None
        self.audit = self.run_dir / "requests.jsonl"

    def request(self, path, method="GET", body=None):
        data = None if body is None else json.dumps(body).encode("utf-8")
        req = Request(f"http://127.0.0.1:{self.port}{path}", data=data, method=method,
                      headers={"Content-Type": "application/json", "X-AR2-Test": "harness"})
        with urlopen(req, timeout=2) as response:
            return json.load(response)

    def require_free_port(self):
        try:
            connection = socket.create_connection(("127.0.0.1", self.port), timeout=0.2)
        except OSError:
            return
        connection.close()
        raise TestFailure(f"Port {self.port} is occupied. Stop the existing local server first; this script will not kill it.")

    def start(self):
        self.require_free_port()
        db = read_json(self.db, encoding="utf-8")  # Match the server's decoder; never initialize an invalid DB as empty.
        command = [sys.executable, "-u", str(REPO / "LocalScripts" / "local_geojson_server.py"),
                   "--host", "127.0.0.1", "--port", str(self.port), "--db", str(self.db), "--audit-log", str(self.audit)]
        message = (db.get("mqtt_latest") or {}).get("message") or {}
        for field, flag in (("latitude", "--lat"), ("longitude", "--lon")):
            value = message.get(field)
            if isinstance(value, (int, float)) and math.isfinite(value):
                command.extend([flag, str(value)])
        if self.rewrite_ids:
            command.append("--rewrite-create-ids")
        self.log = (self.run_dir / "server.log").open("a", encoding="utf-8")
        self.process = subprocess.Popen(command, stdout=self.log, stderr=subprocess.STDOUT)
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline:
            if self.process.poll() is not None:
                raise TestFailure(f"Local server exited. See {self.run_dir / 'server.log'}")
            try:
                self.request(FEATURES)
                return
            except (OSError, URLError):
                time.sleep(0.1)
        raise TestFailure("Local server did not become reachable within 10 seconds.")

    def stop(self):
        if self.process is not None:
            if self.process.poll() is None:
                self.process.terminate()
                try:
                    self.process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    self.process.kill()
                    self.process.wait(timeout=5)
            self.process = None
        if self.log:
            self.log.close()
            self.log = None


def wait_for(label, check, timeout, quiet_seconds=0):
    deadline = time.monotonic() + timeout
    good_since, last_notice, problems = None, 0.0, ["No observation yet."]
    while time.monotonic() < deadline:
        try:
            problems = check()
        except (OSError, URLError, json.JSONDecodeError, TestFailure) as exc:
            problems = [str(exc)]
        now = time.monotonic()
        if not problems:
            good_since = good_since or now
            if now - good_since >= quiet_seconds:
                print(f"PASS {label}", flush=True)
                return
        else:
            good_since = None
            if now - last_notice >= 5:
                print(f"Waiting: {problems[0]}", flush=True)
                last_notice = now
        time.sleep(0.25)
    raise TestFailure(f"{label} timed out: {'; '.join(problems)}")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--resume", action="store_true", help="Verify a pending offline delete+create already made in Unity")
    parser.add_argument("--snapshot", type=Path, default=PROJECT / "Coordinates" / "GeoJSON_updated.json")
    parser.add_argument("--db", type=Path, default=PROJECT / "LocalScripts" / "local_features.json")
    parser.add_argument("--port", type=int, default=8080)
    parser.add_argument("--timeout", type=float, default=120)
    parser.add_argument("--settle-seconds", type=float, default=3)
    parser.add_argument("--rewrite-create-ids", action="store_true")
    parser.add_argument("--exit-after-test", action="store_true", help="Stop the test server after PASS instead of keeping it running")
    args = parser.parse_args(argv)
    if args.timeout <= 0 or args.settle_seconds < 0 or not 0 < args.port < 65536:
        parser.error("Use a positive timeout, nonnegative settle time and a valid port.")
    run_dir = PROJECT / "Logs" / "AR2OfflineSync" / (time.strftime("%Y%m%d-%H%M%S") + "-" + uuid.uuid4().hex[:6])
    server, report = None, {"result": "FAILED", "mode": "resume" if args.resume else "guided",
                           "not_verified": ["rendered marker visibility", "controller/hand interaction"]}
    try:
        initial, db = read_json(args.snapshot), read_json(args.db)
        outbox(initial)
        server = LocalServer(args.db.resolve(), run_dir, args.port, args.rewrite_create_ids)
        server.require_free_port()
        if args.resume:
            scenario = scenario_from_snapshot(initial)
        elif outbox(initial)["pending"]:
            raise TestFailure("There are already pending edits. Use --resume for that outage, or synchronize them before a guided test.")
        run_dir.mkdir(parents=True)
        write_json(run_dir / "server-before.json", db)
        write_json(run_dir / "unity-before.json", initial)
        print(f"Evidence: {run_dir}\nKeep Unity in Play mode with Use Localhost enabled. Finish/cancel any draft drawing.", flush=True)
        if not args.resume:
            server.start()
            fid = "ar2_offline_delete_" + uuid.uuid4().hex[:8]
            feature = {"type": "Feature", "id": fid, "properties": {"id": fid, "class": "animal", "category": "animal",
                       "confidence": 1.0, "source": "offline sync test"},
                       "geometry": {"type": "Point", "coordinates": [23.707802895875822, 37.960109561798006]}}
            remote_id = str(server.request(FEATURES, "PUT", feature)["id"])
            server.request(STATUS, "PATCH", {"fe_updated": 1, "mobile_updated": 0, "ar_updated": 0})
            selected = {}

            def baseline_check():
                root = read_json(args.snapshot)
                state = outbox(root)
                local_id = next((key for key, value in state["remoteIds"].items() if str(value) == remote_id), None)
                if not local_id or any(p["featureId"] == local_id for p in state["pending"]):
                    return ["Waiting for the seeded animal to be fetched and synchronized by Unity."]
                selected["id"] = local_id
                return []

            wait_for("seeded point synchronized", baseline_check, args.timeout, args.settle_seconds)
            baseline = read_json(args.snapshot)
            server.stop()
            print(f"\nSERVER OFFLINE. Delete animal ID {selected['id']} in Unity, then ADD ONE NEW POI.\n"
                  "Enable the Animal filter if needed to find it. Release the new POI and let autosave finish.\n"
                  "Confirm the deleted point stays hidden and the new point stays visible.", flush=True)
            input("Press Enter after making both edits: ")
            collected = {}

            def offline_check():
                collected["scenario"] = scenario_from_snapshot(read_json(args.snapshot), selected["id"],
                    {str(f.get("id")) for f in baseline["features"]})
                return []

            wait_for("offline operations persisted in Unity's outbox", offline_check, args.timeout)
            scenario = collected["scenario"]
        else:
            print("PASS pending offline delete+create found in Unity's saved outbox.", flush=True)
        offline = read_json(args.snapshot)
        write_json(run_dir / "unity-offline.json", offline)
        report["scenario"] = scenario
        before_ids = {str(f.get("id")) for f in read_json(args.db)["features"]}
        if any(remote_id not in before_ids for remote_id in scenario["deletes"].values()):
            raise TestFailure("A deletion target is already absent from this database. Check that --db is the same DB used before the outage.")
        since = time.time_ns()
        server.start()
        print("SERVER ONLINE. Waiting for Unity to deliver its queued changes...", flush=True)

        def final_check():
            return (convergence_problems(scenario, read_json(args.snapshot), server.request(FEATURES)) +
                    observer_problems(audit_records(server.audit), since))

        wait_for("data converged and observer follows the original defaults", final_check, args.timeout, args.settle_seconds)
        report["reconnection_seconds"] = round((time.time_ns() - since) / 1e9, 3)
        if convergence_problems(scenario, read_json(args.snapshot), read_json(args.db)):
            raise TestFailure("The server's persisted database did not converge.")
        server.stop()
        server.start()
        wait_for("server restart preserves the result without duplicated creates", final_check, args.timeout, args.settle_seconds)
        write_json(run_dir / "unity-after.json", read_json(args.snapshot))
        write_json(run_dir / "server-after.json", server.request(FEATURES))
        report.update(result="PASSED", observer=server.request(STATUS), verified_layers=["Unity JSON/outbox", "real local HTTP API", "server disk persistence", "observer request payloads"])
        write_json(run_dir / "report.json", report)
        print(f"\nPASSED. Report: {run_dir / 'report.json'}", flush=True)
        if not args.exit_after_test:
            print("The local server is still running here against the same database. Ctrl+C stops it.", flush=True)
            server.process.wait()
        return 0
    except KeyboardInterrupt:
        return 0 if report["result"] == "PASSED" else 130
    except (TestFailure, OSError, URLError, json.JSONDecodeError, EOFError) as exc:
        report["error"] = str(exc)
        print(f"FAILED: {exc}", file=sys.stderr, flush=True)
        return 1
    finally:
        if server:
            server.stop()
        if run_dir.exists():
            write_json(run_dir / "report.json", report)


if __name__ == "__main__":
    raise SystemExit(main())

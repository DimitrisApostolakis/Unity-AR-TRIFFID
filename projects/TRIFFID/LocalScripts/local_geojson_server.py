#!/usr/bin/env python3
"""
Local GeoJSON API server for Unity sync testing.
No external dependencies required.

Endpoints:
- GET    /wp-json/map-manager/v1/features
- PUT    /wp-json/map-manager/v1/features
- PATCH  /wp-json/map-manager/v1/features/{id}
- DELETE /wp-json/map-manager/v1/features/{id}

Test mode:
- Use --rewrite-create-ids to simulate a backend that assigns a different
  final server ID during feature creation.
"""

from __future__ import annotations

import argparse
import json
import os
import threading
import time
import uuid
from datetime import datetime
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any, Dict, List, Optional
from urllib.parse import urlparse

API_BASE = "/wp-json/map-manager/v1/features"
STATUS_BASE = "/wp-json/observer-sync/v1/status"
MQTT_LATEST_BASE = "/wp-json/mqtt/v1/latest"


class GeoJsonStore:
    def __init__(self, db_path: str, rewrite_create_ids: bool = False) -> None:
        self.db_path = db_path
        self.rewrite_create_ids = rewrite_create_ids
        self.lock = threading.Lock()
        self.motion_stop = threading.Event()
        self.motion_thread: Optional[threading.Thread] = None
        self._ensure_db()
        self._ensure_motion_worker()

    def _ensure_db(self) -> None:
        if os.path.exists(self.db_path):
            return

        self._write(self._default_db())

    def _default_db(self) -> Dict[str, Any]:
        initial = {
            "type": "FeatureCollection",
            "features": [],
            "observer_status": {
                "last_update": self._now_string(),
                "fe_updated": 1,
                "mobile_updated": 0,
                "ar_updated": 0,
            },
            "mqtt_latest": self._default_mqtt_latest(),
        }
        self._ensure_motion_defaults(initial)
        return initial

    def _read(self) -> Dict[str, Any]:
        try:
            with open(self.db_path, "r", encoding="utf-8") as f:
                data = json.load(f)
        except (FileNotFoundError, json.JSONDecodeError, OSError):
            data = self._default_db()
            self._write(data)

        if not isinstance(data, dict):
            data = self._default_db()

        if "type" not in data:
            data["type"] = "FeatureCollection"
        if "features" not in data or not isinstance(data["features"], list):
            data["features"] = []

        status = data.get("observer_status")
        if not isinstance(status, dict):
            status = {}

        status.setdefault("last_update", self._now_string())
        status.setdefault("fe_updated", 1)
        status.setdefault("mobile_updated", 0)
        status.setdefault("ar_updated", 0)
        data["observer_status"] = status

        mqtt_latest = data.get("mqtt_latest")
        if not isinstance(mqtt_latest, dict):
            mqtt_latest = self._default_mqtt_latest()

        message = mqtt_latest.get("message")
        if not isinstance(message, dict):
            message = self._default_mqtt_message()

        header = message.get("header")
        if not isinstance(header, dict):
            header = self._default_mqtt_header()

        stamp = header.get("stamp")
        if not isinstance(stamp, dict):
            stamp = self._default_mqtt_stamp()

        stamp.setdefault("sec", 772)
        stamp.setdefault("nanosec", 63000000)
        header["stamp"] = stamp
        header.setdefault("frame_id", "b2/base_link")
        message["header"] = header

        message.setdefault("roll", 0.0)
        message.setdefault("pitch", 0.0)
        message.setdefault("yaw", 0.0)
        message.setdefault("latitude", 0.0)
        message.setdefault("longitude", 0.0)
        message.setdefault("battery_temperature", 0)
        message.setdefault("power_voltage", 0)
        message.setdefault("power_current", 0)
        message.setdefault("temperature_ntc1", 0)
        message.setdefault("temperature_ntc2", 0)
        message.setdefault("mode", 0)
        message.setdefault("commander", "none")
        message.setdefault("behavior", "IDLE")
        message.setdefault("action_command", "{}")

        mqtt_latest["message"] = message
        mqtt_latest.setdefault("topic", "/b2/ugv_status_from_ros2")
        mqtt_latest.setdefault("time", self._now_string())
        mqtt_latest.setdefault("motion", self._default_motion_state())
        self._normalize_motion_state(mqtt_latest["motion"])
        data["mqtt_latest"] = mqtt_latest

        return data

    def _write(self, data: Dict[str, Any]) -> None:
        temp = self.db_path + ".tmp"
        with open(temp, "w", encoding="utf-8") as f:
            json.dump(data, f, ensure_ascii=False, indent=2)
        os.replace(temp, self.db_path)

    def get_all(self) -> Dict[str, Any]:
        with self.lock:
            return self._read()

    def put_feature(self, feature: Dict[str, Any]) -> Dict[str, Any]:
        with self.lock:
            db = self._read()
            features = db["features"]

            incoming_fid = str(feature.get("id") or "").strip()

            if self.rewrite_create_ids:
                # Test mode:
                # Simulate a real backend that ignores Unity's temporary ID
                # and assigns its own final server-side ID.
                fid = f"server_{uuid.uuid4().hex}"
                feature["id"] = fid
            else:
                # Normal mode:
                # Preserve Unity's ID if it sent one.
                fid = incoming_fid
                if not fid:
                    fid = self._next_id(features)
                    feature["id"] = fid

            props = feature.get("properties")
            if isinstance(props, dict):
                if self.rewrite_create_ids:
                    # In test mode, force both IDs to match the server ID.
                    props["id"] = fid
                elif not str(props.get("id") or "").strip():
                    props["id"] = fid

            existing_index = self._find_feature_index(features, fid)
            if existing_index is not None:
                features[existing_index] = feature
            else:
                features.append(feature)

            self._write(db)
            return feature

    def patch_feature(self, feature_id: str, patch: Dict[str, Any]) -> Optional[Dict[str, Any]]:
        with self.lock:
            db = self._read()
            features = db["features"]
            idx = self._find_feature_index(features, feature_id)
            if idx is None:
                return None

            target = features[idx]
            if "geometry" in patch and isinstance(patch["geometry"], dict):
                target["geometry"] = patch["geometry"]

            if "properties" in patch and isinstance(patch["properties"], dict):
                existing_props = target.get("properties")
                if not isinstance(existing_props, dict):
                    existing_props = {}
                existing_props.update(patch["properties"])
                if not str(existing_props.get("id") or "").strip():
                    existing_props["id"] = str(target.get("id") or feature_id)
                target["properties"] = existing_props

            if "id" in patch and str(patch["id"] or "").strip():
                target["id"] = str(patch["id"])

            features[idx] = target
            self._write(db)
            return target

    def delete_feature(self, feature_id: str) -> bool:
        with self.lock:
            db = self._read()
            features = db["features"]
            idx = self._find_feature_index(features, feature_id)
            if idx is None:
                return False

            features.pop(idx)
            self._write(db)
            return True

    def get_status(self) -> Dict[str, Any]:
        with self.lock:
            db = self._read()
            status = db.get("observer_status")
            if not isinstance(status, dict):
                status = {
                    "last_update": self._now_string(),
                    "fe_updated": 1,
                    "mobile_updated": 0,
                    "ar_updated": 0,
                }
                db["observer_status"] = status
                self._write(db)
            return status

    def get_mqtt_latest(self) -> Dict[str, Any]:
        with self.lock:
            db = self._read()
            mqtt_latest = db.get("mqtt_latest")
            if not isinstance(mqtt_latest, dict):
                mqtt_latest = self._default_mqtt_latest()
                db["mqtt_latest"] = mqtt_latest
                self._write(db)
            self._normalize_motion_state(mqtt_latest.setdefault("motion", self._default_motion_state()))
            return mqtt_latest

    def patch_mqtt_latest(self, patch: Dict[str, Any]) -> Dict[str, Any]:
        with self.lock:
            db = self._read()
            mqtt_latest = db.get("mqtt_latest")
            if not isinstance(mqtt_latest, dict):
                mqtt_latest = self._default_mqtt_latest()

            message = mqtt_latest.get("message")
            if not isinstance(message, dict):
                message = self._default_mqtt_message()

            nested_message = patch.get("message")
            if isinstance(nested_message, dict):
                patch = nested_message

            for key in ("latitude", "longitude", "roll", "pitch", "yaw"):
                if key in patch:
                    try:
                        message[key] = float(patch[key])
                    except Exception:
                        pass

            for key in (
                "battery_temperature",
                "power_voltage",
                "power_current",
                "temperature_ntc1",
                "temperature_ntc2",
                "mode",
            ):
                if key in patch:
                    try:
                        message[key] = int(patch[key])
                    except Exception:
                        pass

            for key in ("commander", "behavior", "action_command"):
                if key in patch:
                    message[key] = str(patch[key])

            motion_patch = patch.get("motion")
            if isinstance(motion_patch, dict):
                mqtt_latest["motion"] = self._merge_motion_state(mqtt_latest.get("motion"), motion_patch)
                self._normalize_motion_state(mqtt_latest["motion"])

            if "auto_move_enabled" in patch:
                motion_state = mqtt_latest.get("motion")
                if not isinstance(motion_state, dict):
                    motion_state = self._default_motion_state()
                motion_state["enabled"] = bool(patch["auto_move_enabled"])
                mqtt_latest["motion"] = motion_state
                self._normalize_motion_state(motion_state)

            if "start_after_sec" in patch:
                motion_state = mqtt_latest.get("motion")
                if not isinstance(motion_state, dict):
                    motion_state = self._default_motion_state()
                motion_state["start_after_sec"] = self._safe_float(patch.get("start_after_sec"), 0.0)
                motion_state["start_at_epoch"] = time.time() + motion_state["start_after_sec"]
                mqtt_latest["motion"] = motion_state
                self._normalize_motion_state(motion_state)

            if "lat_step" in patch or "lon_step" in patch or "interval_sec" in patch:
                motion_state = mqtt_latest.get("motion")
                if not isinstance(motion_state, dict):
                    motion_state = self._default_motion_state()
                if "lat_step" in patch:
                    motion_state["lat_step"] = self._safe_float(patch.get("lat_step"), motion_state["lat_step"])
                if "lon_step" in patch:
                    motion_state["lon_step"] = self._safe_float(patch.get("lon_step"), motion_state["lon_step"])
                if "interval_sec" in patch:
                    motion_state["interval_sec"] = max(0.1, self._safe_float(patch.get("interval_sec"), motion_state["interval_sec"]))
                mqtt_latest["motion"] = motion_state
                self._normalize_motion_state(motion_state)

            header = message.get("header")
            if not isinstance(header, dict):
                header = self._default_mqtt_header()

            stamp = header.get("stamp")
            if not isinstance(stamp, dict):
                stamp = self._default_mqtt_stamp()
            stamp["sec"] = int(datetime.now().timestamp())
            stamp["nanosec"] = datetime.now().microsecond * 1000
            header["stamp"] = stamp
            message["header"] = header

            mqtt_latest["message"] = message
            mqtt_latest["time"] = self._now_string()
            mqtt_latest.setdefault("topic", "/b2/ugv_status_from_ros2")

            db["mqtt_latest"] = mqtt_latest
            self._write(db)
            return mqtt_latest

    def _ensure_motion_worker(self) -> None:
        if self.motion_thread is not None and self.motion_thread.is_alive():
            return

        self.motion_stop.clear()
        self.motion_thread = threading.Thread(target=self._motion_worker, name="mqtt-motion-worker", daemon=True)
        self.motion_thread.start()

    def _motion_worker(self) -> None:
        TARGET_LON = 23.710736362392083
        TARGET_LAT = 37.96188968428186
        TOLERANCE = 0.0001

        while not self.motion_stop.is_set():
            try:
                with self.lock:
                    db = self._read()
                    mqtt_latest = db.get("mqtt_latest")
                    if not isinstance(mqtt_latest, dict):
                        mqtt_latest = self._default_mqtt_latest()

                    motion = mqtt_latest.get("motion")
                    if not isinstance(motion, dict):
                        motion = self._default_motion_state()
                        mqtt_latest["motion"] = motion

                    self._normalize_motion_state(motion)

                    if motion.get("enabled") and time.time() >= float(motion.get("start_at_epoch") or 0.0):
                        last_step = float(motion.get("last_step_epoch") or 0.0)
                        interval = max(0.1, float(motion.get("interval_sec") or 1.0))
                        now = time.time()
                        if now - last_step >= interval:
                            message = mqtt_latest.get("message")
                            if not isinstance(message, dict):
                                message = self._default_mqtt_message()

                            message["latitude"] = float(message.get("latitude") or 0.0) + float(motion.get("lat_step") or 0.0)
                            message["longitude"] = float(message.get("longitude") or 0.0) + float(motion.get("lon_step") or 0.0)
                            message["yaw"] = float(message.get("yaw") or 0.0) + float(motion.get("yaw_step") or 0.0)

                            current_lat = float(message.get("latitude") or 0.0)
                            current_lon = float(message.get("longitude") or 0.0)
                            lat_diff = abs(current_lat - TARGET_LAT)
                            lon_diff = abs(current_lon - TARGET_LON)

                            if lat_diff <= TOLERANCE and lon_diff <= TOLERANCE:
                                motion["enabled"] = False
                                print(f"[motion] Target reached: {current_lat}, {current_lon}")

                            header = message.get("header")
                            if not isinstance(header, dict):
                                header = self._default_mqtt_header()
                            stamp = header.get("stamp")
                            if not isinstance(stamp, dict):
                                stamp = self._default_mqtt_stamp()
                            stamp["sec"] = int(now)
                            stamp["nanosec"] = int((now - int(now)) * 1_000_000_000)
                            header["stamp"] = stamp
                            message["header"] = header

                            motion["last_step_epoch"] = now
                            mqtt_latest["message"] = message
                            mqtt_latest["time"] = self._now_string()
                            db["mqtt_latest"] = mqtt_latest
                            self._write(db)
            except Exception:
                pass

            self.motion_stop.wait(0.25)

    def _ensure_motion_defaults(self, data: Dict[str, Any]) -> None:
        mqtt_latest = data.get("mqtt_latest")
        if not isinstance(mqtt_latest, dict):
            return
        motion = mqtt_latest.get("motion")
        if not isinstance(motion, dict):
            motion = self._default_motion_state()
            mqtt_latest["motion"] = motion
        self._normalize_motion_state(motion)

    @staticmethod
    def _default_motion_state() -> Dict[str, Any]:
        return {
            "enabled": False,
            "start_after_sec": 0.0,
            "start_at_epoch": 0.0,
            "interval_sec": 1.0,
            "lat_step": 0.00001,
            "lon_step": 0.00001,
            "yaw_step": 0.0,
            "last_step_epoch": 0.0,
        }

    def _merge_motion_state(self, current: Any, patch: Dict[str, Any]) -> Dict[str, Any]:
        state = self._default_motion_state()
        if isinstance(current, dict):
            state.update(current)

        for key in ("enabled", "start_after_sec", "start_at_epoch", "interval_sec", "lat_step", "lon_step", "yaw_step", "last_step_epoch"):
            if key in patch:
                state[key] = patch[key]

        return state

    def _normalize_motion_state(self, state: Dict[str, Any]) -> None:
        state["enabled"] = bool(state.get("enabled"))
        state["start_after_sec"] = self._safe_float(state.get("start_after_sec"), 0.0)
        state["start_at_epoch"] = self._safe_float(state.get("start_at_epoch"), 0.0)
        state["interval_sec"] = max(0.1, self._safe_float(state.get("interval_sec"), 1.0))
        state["lat_step"] = self._safe_float(state.get("lat_step"), 0.00001)
        state["lon_step"] = self._safe_float(state.get("lon_step"), 0.00001)
        state["yaw_step"] = self._safe_float(state.get("yaw_step"), 0.0)
        state["last_step_epoch"] = self._safe_float(state.get("last_step_epoch"), 0.0)
        if state["enabled"] and state["start_at_epoch"] <= 0.0:
            state["start_at_epoch"] = time.time() + state["start_after_sec"]

    @staticmethod
    def _safe_float(value: Any, fallback: float) -> float:
        try:
            return float(value)
        except Exception:
            return fallback

    def patch_status(self, patch: Dict[str, Any]) -> Dict[str, Any]:
        with self.lock:
            db = self._read()
            status = db.get("observer_status")
            if not isinstance(status, dict):
                status = {
                    "last_update": self._now_string(),
                    "fe_updated": 1,
                    "mobile_updated": 0,
                    "ar_updated": 0,
                }

            for key in ("fe_updated", "mobile_updated", "ar_updated"):
                if key in patch:
                    try:
                        status[key] = int(patch[key])
                    except Exception:
                        status[key] = 0

            status["last_update"] = self._now_string()
            db["observer_status"] = status
            self._write(db)
            return status

    @staticmethod
    def _now_string() -> str:
        return datetime.now().strftime("%Y-%m-%d %H:%M:%S")

    @staticmethod
    def _default_mqtt_stamp() -> Dict[str, Any]:
        return {"sec": 772, "nanosec": 63000000}

    @staticmethod
    def _default_mqtt_header() -> Dict[str, Any]:
        return {"stamp": GeoJsonStore._default_mqtt_stamp(), "frame_id": "b2/base_link"}

    @staticmethod
    def _default_mqtt_message() -> Dict[str, Any]:
        return {
            "header": GeoJsonStore._default_mqtt_header(),
            "roll": 0.0,
            "pitch": 0.0,
            "yaw": 0.0,
            "latitude": 37.95961198206017,
            "longitude": 23.707384148076102,
            "battery_temperature": 0,
            "power_voltage": 0,
            "power_current": 0,
            "temperature_ntc1": 0,
            "temperature_ntc2": 0,
            "mode": 0,
            "commander": "none",
            "behavior": "IDLE",
            "action_command": "{}",
        }

    @staticmethod
    def _default_mqtt_latest() -> Dict[str, Any]:
        return {
            "message": GeoJsonStore._default_mqtt_message(),
            "topic": "/b2/ugv_status_from_ros2",
            "time": GeoJsonStore._now_string(),
        }

    @staticmethod
    def _next_id(features: List[Dict[str, Any]]) -> str:
        used = set()
        for f in features:
            fid = str(f.get("id") or "").strip()
            if fid.isdigit():
                used.add(int(fid))
            pid = str((f.get("properties") or {}).get("id") or "").strip()
            if pid.isdigit():
                used.add(int(pid))

        n = 1
        while n in used:
            n += 1
        return str(n)

    @staticmethod
    def _find_feature_index(features: List[Dict[str, Any]], feature_id: str) -> Optional[int]:
        target = str(feature_id)
        for i, f in enumerate(features):
            fid = str(f.get("id") or "")
            pid = str((f.get("properties") or {}).get("id") or "")
            if fid == target or pid == target:
                return i
        return None


class Handler(BaseHTTPRequestHandler):
    store: GeoJsonStore = None

    def do_GET(self) -> None:
        path = urlparse(self.path).path
        if path == STATUS_BASE:
            self._json(200, self.store.get_status())
            return

        if path == MQTT_LATEST_BASE:
            self._json(200, self.store.get_mqtt_latest())
            return

        if path != API_BASE:
            self._json(404, {"error": "Not found"})
            return

        self._json(200, self.store.get_all())

    def do_PUT(self) -> None:
        path = urlparse(self.path).path
        if path != API_BASE:
            self._json(404, {"error": "Not found"})
            return

        body = self._read_json_body()
        if body is None:
            self._json(400, {"error": "Invalid JSON body"})
            return

        if not isinstance(body, dict):
            self._json(400, {"error": "Feature must be a JSON object"})
            return

        if body.get("type") != "Feature":
            body["type"] = "Feature"

        if not isinstance(body.get("properties"), dict):
            body["properties"] = {}

        if not isinstance(body.get("geometry"), dict):
            self._json(400, {"error": "Feature.geometry is required"})
            return

        saved = self.store.put_feature(body)
        self._json(200, {"id": saved.get("id"), "feature": saved})

    def do_PATCH(self) -> None:
        path = urlparse(self.path).path

        if path == STATUS_BASE:
            body = self._read_json_body()
            if body is None or not isinstance(body, dict):
                self._json(400, {"error": "Invalid JSON body"})
                return

            status = self.store.patch_status(body)
            self._json(200, {"success": True, "data": status})
            return

        if path == MQTT_LATEST_BASE:
            body = self._read_json_body()
            if body is None or not isinstance(body, dict):
                self._json(400, {"error": "Invalid JSON body"})
                return

            mqtt_latest = self.store.patch_mqtt_latest(body)
            self._json(200, mqtt_latest)
            return

        feature_id = self._extract_id(path)
        if feature_id is None:
            self._json(404, {"error": "Not found"})
            return

        body = self._read_json_body()
        if body is None or not isinstance(body, dict):
            self._json(400, {"error": "Invalid JSON body"})
            return

        patched = self.store.patch_feature(feature_id, body)
        if patched is None:
            self._json(404, {"success": False, "error": "Feature not found"})
            return

        self._json(200, {"success": True, "id": str(patched.get("id") or feature_id), "feature": patched})

    def do_DELETE(self) -> None:
        path = urlparse(self.path).path
        feature_id = self._extract_id(path)
        if feature_id is None:
            self._json(404, {"error": "Not found"})
            return

        deleted = self.store.delete_feature(feature_id)
        if not deleted:
            self._json(404, {"error": "Feature not found"})
            return

        self._json(200, {"deleted": feature_id})

    def _extract_id(self, path: str) -> Optional[str]:
        prefix = API_BASE + "/"
        if not path.startswith(prefix):
            return None
        fid = path[len(prefix):].strip("/")
        return fid or None

    def _read_json_body(self) -> Optional[Any]:
        try:
            length = int(self.headers.get("Content-Length", "0"))
        except ValueError:
            return None

        raw = self.rfile.read(length) if length > 0 else b"{}"
        try:
            return json.loads(raw.decode("utf-8"))
        except Exception:
            return None

    def _json(self, status: int, payload: Dict[str, Any]) -> None:
        encoded = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(encoded)))
        self.send_header("Access-Control-Allow-Origin", "*")
        self.end_headers()
        self.wfile.write(encoded)

    def log_message(self, fmt: str, *args: Any) -> None:
        print(f"[server] {self.address_string()} - {fmt % args}")


def main() -> int:
    parser = argparse.ArgumentParser(description="Run local GeoJSON API server")
    parser.add_argument("--host", default="127.0.0.1", help="Bind host")
    parser.add_argument("--port", default=8080, type=int, help="Bind port")
    parser.add_argument("--db", default="local_features.json", help="Path to JSON db file")
    parser.add_argument("--lat", type=float, default=37.9603098916767, help="Initial MQTT latitude")
    parser.add_argument("--lon", type=float, default=23.708289295677133, help="Initial MQTT longitude")
    parser.add_argument("--auto-move-after", type=float, default=-1.0, help="Start moving automatically after N seconds; negative disables")
    parser.add_argument("--move-interval", type=float, default=1.0, help="Seconds between movement updates")
    parser.add_argument("--move-step-lat", type=float, default=0.00001, help="Latitude delta per update")
    parser.add_argument("--move-step-lon", type=float, default=0.00001, help="Longitude delta per update")
    parser.add_argument(
        "--rewrite-create-ids",
        action="store_true",
        help="Test mode: assign a new server-side ID for every PUT create"
    )
    args = parser.parse_args()

    db_path = os.path.abspath(args.db)
    store = GeoJsonStore(db_path, rewrite_create_ids=args.rewrite_create_ids)

    with store.lock:
        db = store._read()
        mqtt_latest = db.get("mqtt_latest")
        if isinstance(mqtt_latest, dict) and isinstance(mqtt_latest.get("message"), dict):
            mqtt_latest["message"]["latitude"] = args.lat
            mqtt_latest["message"]["longitude"] = args.lon
            mqtt_latest["time"] = store._now_string()
            if args.auto_move_after >= 0:
                motion = mqtt_latest.get("motion")
                if not isinstance(motion, dict):
                    motion = store._default_motion_state()
                motion["enabled"] = True
                motion["start_after_sec"] = args.auto_move_after
                motion["start_at_epoch"] = time.time() + args.auto_move_after
                motion["interval_sec"] = max(0.1, args.move_interval)
                motion["lat_step"] = args.move_step_lat
                motion["lon_step"] = args.move_step_lon
                mqtt_latest["motion"] = motion
            db["mqtt_latest"] = mqtt_latest
            store._write(db)

    Handler.store = store

    server = ThreadingHTTPServer((args.host, args.port), Handler)
    print(f"Local GeoJSON API running on http://{args.host}:{args.port}{API_BASE}")
    print(f"Local Observer Status API on http://{args.host}:{args.port}{STATUS_BASE}")
    print(f"DB file: {db_path}")
    if args.rewrite_create_ids:
        print("Create ID rewrite test mode: ENABLED")
    print("Press Ctrl+C to stop.")

    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\nStopping server...")
    finally:
        server.server_close()

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
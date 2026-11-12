#!/usr/bin/env python3
"""
Local stand-in for the UGV sensor API, used by local_geojson_server.py.

Served by the local server (same shapes as the real API):
- GET  /wp-json/mqtt/v1/thresholds
- GET  /wp-json/mqtt/v1/alerts            (?sensor=temp, ?limit=50, ?page=1)

Local test helpers (the real API has no equivalent for the app):
- POST /wp-json/mqtt/v1/alerts            {"sensor": "temp", "value": 31.5}
      Records the alert whatever the value. With "reading": true the value is
      treated as a sensor reading, as the real server does: an alert is
      recorded only when it reaches or passes the limit.
- POST /wp-json/mqtt/v1/thresholds        {"temp": 30}

Run this file to record an alert on the running local server:

    python LocalScripts/local_sensor_alerts.py temp 31.5
    python LocalScripts/local_sensor_alerts.py co2_eq              (value just above the limit)
    python LocalScripts/local_sensor_alerts.py humidity --no-gps
    python LocalScripts/local_sensor_alerts.py --list

Alerts are kept in LocalScripts/local_sensor_alerts.json so ids keep counting
up across server restarts, as they do on the real server. Delete that file to
start again from id 1.
"""

from __future__ import annotations

import argparse
import json
import os
import threading
import urllib.error
import urllib.request
from datetime import datetime, timezone
from typing import Any, Dict, List, Optional

THRESHOLDS_BASE = "/wp-json/mqtt/v1/thresholds"
ALERTS_BASE = "/wp-json/mqtt/v1/alerts"

DEFAULT_THRESHOLDS: Dict[str, float] = {
    "co2_eq": 600,
    "temp": 29,
    "humidity": 0.27,
    "pressure": 93885,
}

DEFAULT_LIMIT = 50
MAX_LIMIT = 200


class SensorAlertStore:
    def __init__(self, path: str) -> None:
        self.path = path
        self.lock = threading.Lock()
        self.data = self._load()

    def _load(self) -> Dict[str, Any]:
        data: Dict[str, Any] = {}
        if os.path.exists(self.path):
            try:
                with open(self.path, "r", encoding="utf-8") as handle:
                    loaded = json.load(handle)
                if isinstance(loaded, dict):
                    data = loaded
            except (OSError, ValueError):
                data = {}

        thresholds = data.get("thresholds")
        if not isinstance(thresholds, dict):
            thresholds = {}
        data["thresholds"] = {**DEFAULT_THRESHOLDS, **thresholds}
        if not isinstance(data.get("alerts"), list):
            data["alerts"] = []
        return data

    def _save(self) -> None:
        temporary = self.path + ".tmp"
        with open(temporary, "w", encoding="utf-8") as handle:
            json.dump(self.data, handle, indent=2)
        os.replace(temporary, self.path)

    def thresholds(self) -> Dict[str, float]:
        with self.lock:
            return dict(self.data["thresholds"])

    def set_thresholds(self, patch: Dict[str, Any]) -> Dict[str, float]:
        with self.lock:
            for sensor, limit in patch.items():
                if isinstance(limit, (int, float)) and not isinstance(limit, bool):
                    self.data["thresholds"][str(sensor)] = limit
            self._save()
            return dict(self.data["thresholds"])

    def page(self, sensor: Optional[str], limit: int, page: int) -> Dict[str, Any]:
        with self.lock:
            alerts: List[Dict[str, Any]] = [a for a in self.data["alerts"] if not sensor or a.get("sensor") == sensor]
            # Newest first, as the real API answers.
            alerts.sort(key=lambda a: (a.get("timestamp", ""), a.get("id", 0)), reverse=True)
            limit = max(1, min(MAX_LIMIT, limit))
            page = max(1, page)
            start = (page - 1) * limit
            return {
                "total": len(alerts),
                "page": page,
                "limit": limit,
                "thresholds": dict(self.data["thresholds"]),
                "alerts": alerts[start:start + limit],
            }

    def add(self, body: Dict[str, Any]) -> Optional[Dict[str, Any]]:
        sensor = str(body.get("sensor") or "").strip()
        if not sensor:
            return None

        with self.lock:
            limit = self.data["thresholds"].get(sensor)
            value = body.get("value")
            if not isinstance(value, (int, float)) or isinstance(value, bool):
                if limit is None:
                    return None
                # No value given: a reading just above the limit.
                value = limit * 1.02 if limit else 1.0

            # A plain reading below its limit is not an alert.
            if body.get("reading") and limit is not None and value < limit:
                return {"recorded": False, "sensor": sensor, "value": value, "threshold": limit}

            latitude = body.get("latitude", 0)
            longitude = body.get("longitude", 0)
            now = datetime.now(timezone.utc).replace(microsecond=0).isoformat()
            alert = {
                "id": max([a.get("id", 0) for a in self.data["alerts"]], default=0) + 1,
                "sensor": sensor,
                "value": value,
                "threshold": limit if limit is not None else value,
                "latitude": latitude,
                "longitude": longitude,
                "gnss_valid": bool(latitude or longitude),
                "timestamp": now,
                "received_at": now,
            }
            self.data["alerts"].append(alert)
            self._save()
            return alert


def default_store_path() -> str:
    return os.path.join(os.path.dirname(os.path.abspath(__file__)), "local_sensor_alerts.json")


# ---- command line: record an alert on the running local server ----

def _request(url: str, payload: Optional[Dict[str, Any]] = None) -> Any:
    data = json.dumps(payload).encode("utf-8") if payload is not None else None
    request = urllib.request.Request(url, data=data, method="POST" if data is not None else "GET",
                                     headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(request, timeout=10) as response:
        return json.loads(response.read().decode("utf-8"))


def main() -> int:
    parser = argparse.ArgumentParser(description="Record a UGV sensor alert on the local server")
    parser.add_argument("sensor", nargs="?", help="co2_eq, temp, humidity or pressure")
    parser.add_argument("value", nargs="?", type=float, help="Measured value in API units; default is just above the limit")
    parser.add_argument("--server", default="http://127.0.0.1:8080", help="Address of local_geojson_server.py")
    parser.add_argument("--lat", type=float, default=37.9603098916767, help="UGV latitude at the alert")
    parser.add_argument("--lon", type=float, default=23.708289295677133, help="UGV longitude at the alert")
    parser.add_argument("--no-gps", action="store_true", help="Record the alert without a position (0, 0)")
    parser.add_argument("--reading", action="store_true", help="Send the value as a reading: recorded only if it reaches the limit")
    parser.add_argument("--list", action="store_true", help="Show the limits and the newest alerts, then exit")
    args = parser.parse_args()

    base = args.server.rstrip("/")
    try:
        if args.list or not args.sensor:
            page = _request(base + ALERTS_BASE + "?limit=10")
            print("Limits:", json.dumps(page["thresholds"]))
            print(f"Alerts: {page['total']} recorded; newest first")
            for alert in page["alerts"]:
                print(f"  #{alert['id']:<4} {alert['sensor']:<9} {alert['value']:<12} limit {alert['threshold']:<8} {alert['timestamp']}")
            return 0

        body: Dict[str, Any] = {"sensor": args.sensor}
        if args.value is not None:
            body["value"] = args.value
        if not args.no_gps:
            body["latitude"] = args.lat
            body["longitude"] = args.lon

        if args.reading:
            body["reading"] = True

        alert = _request(base + ALERTS_BASE, body)
        if alert.get("recorded") is False:
            print(f"No alert: {alert['sensor']} = {alert['value']} is below the limit {alert['threshold']}")
            return 0
        print(f"Recorded alert #{alert['id']}: {alert['sensor']} = {alert['value']} (limit {alert['threshold']})")
        return 0
    except urllib.error.HTTPError as error:
        print(f"Server refused the request: {error.code} {error.read().decode('utf-8', 'replace')}")
    except (urllib.error.URLError, OSError) as error:
        print(f"Could not reach {base}: {error}. Is local_geojson_server.py running?")
    return 1


if __name__ == "__main__":
    raise SystemExit(main())

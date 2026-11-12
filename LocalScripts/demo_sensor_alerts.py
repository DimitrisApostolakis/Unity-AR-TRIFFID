#!/usr/bin/env python3
"""
Walks through the UGV sensor alert notifications of the app, one case at a
time, against the local server. Each step says what to send and what the app
should show, so the result can be checked by eye in Play mode.

Before running:
  1. Tick "Use Local Server" on Assets/TRIFFID/Resources/UgvSensorSettings.asset
  2. python LocalScripts/local_geojson_server.py
  3. Press Play in the Unity Editor and wait a few seconds

Then:
    python LocalScripts/demo_sensor_alerts.py            (waits for Enter between steps)
    python LocalScripts/demo_sensor_alerts.py --auto     (moves on by itself)
    python LocalScripts/demo_sensor_alerts.py --only burst

The app checks for new alerts every "Poll Seconds" (5 by default), so a card
appears up to that long after a step is sent.
"""

from __future__ import annotations

import argparse
import time
import urllib.error
from typing import Any, Callable, Dict, List, Tuple

from local_sensor_alerts import ALERTS_BASE, _request

LAT, LON = 37.9603098916767, 23.708289295677133


class Demo:
    def __init__(self, server: str) -> None:
        self.alerts_url = server.rstrip("/") + ALERTS_BASE
        self.limits: Dict[str, float] = _request(self.alerts_url + "?limit=1")["thresholds"]

    def send(self, sensor: str, factor: float, gps: bool = True, reading: bool = False) -> None:
        """Sends a value of `factor` times the sensor's limit."""
        body: Dict[str, Any] = {"sensor": sensor, "value": round(self.limits[sensor] * factor, 4)}
        if gps:
            body.update(latitude=LAT, longitude=LON)
        if reading:
            body["reading"] = True

        answer = _request(self.alerts_url, body)
        if answer.get("recorded") is False:
            print(f"    sent reading {sensor} = {answer['value']} (limit {answer['threshold']}): server recorded no alert")
        else:
            print(f"    recorded alert #{answer['id']}: {sensor} = {answer['value']} (limit {answer['threshold']})")


def below_limit(demo: Demo) -> None:
    demo.send("temp", 0.9, reading=True)


def single(demo: Demo) -> None:
    demo.send("temp", 1.08)


def no_gps(demo: Demo) -> None:
    demo.send("co2_eq", 1.05, gps=False)


def burst(demo: Demo) -> None:
    demo.send("humidity", 1.01)
    demo.send("temp", 1.1)
    demo.send("pressure", 1.0001)


def repeated(demo: Demo) -> None:
    for _ in range(3):
        demo.send("temp", 1.1)
        time.sleep(7)


# name, what is sent, what the app should show, action
STEPS: List[Tuple[str, str, str, Callable[[Demo], None]]] = [
    ("below", "A temperature reading below its limit.",
     "Nothing: no card, the menu is unchanged. Only a reading at or above the limit becomes an alert.",
     below_limit),
    ("single", "One temperature alert with a GPS position.",
     "A quiet card 'TEMPERATURE ALERT' with value, limit, time and coordinates. The Temperature cell of the menu glows, turns red and reads 'now'.",
     single),
    ("no-gps", "One CO2 alert without a position.",
     "A quiet card 'CO2 ALERT' ending in 'no GPS fix'. The CO2 cell turns red.",
     no_gps),
    ("burst", "Three alerts at once: humidity, temperature, pressure.",
     "Three cards, one after the other in the order sent: humidity, temperature, pressure. Never two at once. All three cells show their new values.",
     burst),
    ("repeated", "The same reading stays over its limit: three temperature alerts, 7 s apart.",
     "A card for each new alert, each waiting until the one before it has gone. This is what a lasting exceedance looks like.",
     repeated),
]


def main() -> int:
    parser = argparse.ArgumentParser(description="Step through the sensor alert notifications against the local server")
    parser.add_argument("--server", default="http://127.0.0.1:8080", help="Address of local_geojson_server.py")
    parser.add_argument("--auto", action="store_true", help="Do not wait for Enter between steps")
    parser.add_argument("--pause", type=float, default=15.0, help="Seconds between steps with --auto")
    parser.add_argument("--only", choices=[step[0] for step in STEPS], help="Run a single step")
    args = parser.parse_args()

    try:
        demo = Demo(args.server)
    except (urllib.error.URLError, OSError) as error:
        print(f"Could not reach {args.server}: {error}. Start LocalScripts/local_geojson_server.py first.")
        return 1

    steps = [step for step in STEPS if not args.only or step[0] == args.only]
    print("The app must be in Play mode with 'Use Local Server' ticked. Limits:", demo.limits)

    for number, (name, sends, expect, action) in enumerate(steps, 1):
        print(f"\n[{number}/{len(steps)}] {name}")
        print(f"  Sends:  {sends}")
        print(f"  Expect: {expect}")
        if not args.auto:
            input("  Press Enter to send...")
        action(demo)

        if number < len(steps):
            if args.auto:
                time.sleep(args.pause)
            else:
                input("  Check the app, then press Enter for the next step...")

    print("\nDone. After two minutes the red cells turn amber and show the age of their last alert.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

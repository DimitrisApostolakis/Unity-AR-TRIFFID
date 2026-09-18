"""Publish a test "ar.ready" job for the AR watcher (Assets/Editor/ArSplatInboxWatcher.cs).

The job folder must already be in the AR laptop's samba folder, laid out the way the ground
station writes it (every .asset/.bytes with its .meta next to it):
    <stem>/<stem>.asset, <stem>/<stem>_{pos,oth,col,shs}.bytes [, <stem>/<stem>_chk.bytes]
    <stem>/<stem>.obj, <stem>/<stem>.transform_colmap_to_enu.json

Run it where the RabbitMQ management API is reachable, i.e. on the pipeline machine
(the UI is bound to 127.0.0.1 there) or through `ssh -L 15672:localhost:15672 <pipeline>`:
    python publish_ar_job.py <stem> [--env path/to/.env] [--api http://127.0.0.1:15672]

Standard library only. Credentials come from RABBITMQ_USER/RABBITMQ_PASS in the .env.
"""

import argparse
import base64
import json
import pathlib
import urllib.parse
import urllib.request


def read_env(path: pathlib.Path) -> dict:
    values = {}
    if not path.exists():
        return values
    for line in path.read_text().splitlines():
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        values[key.strip()] = value.strip().strip("'\"")
    return values


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("stem", help="job folder / base name of the job files in the samba folder")
    parser.add_argument("--chunk", action="store_true", help="the asset also has <stem>_chk.bytes (compressed qualities)")
    parser.add_argument("--env", default=".env", help=".env with the RabbitMQ credentials (default: ./.env)")
    parser.add_argument("--api", default="http://127.0.0.1:15672", help="RabbitMQ management API base URL")
    args = parser.parse_args()

    env = read_env(pathlib.Path(args.env))
    exchange = env.get("RABBITMQ_EXCHANGE") or "pipeline.events"
    routing_key = env.get("RABBITMQ_AR_ROUTING_KEY") or "ar.ready"
    queue = env.get("RABBITMQ_AR_QUEUE") or "ar.queue"
    user = env.get("RABBITMQ_USER") or "guest"
    password = env.get("RABBITMQ_PASS") or "guest"
    auth = base64.b64encode(f"{user}:{password}".encode()).decode()

    def api(method: str, path: str, body: dict) -> str:
        request = urllib.request.Request(
            f"{args.api}/api/{path}",
            data=json.dumps(body).encode(),
            method=method,
            headers={"Authorization": f"Basic {auth}", "Content-Type": "application/json"},
        )
        with urllib.request.urlopen(request) as response:
            return response.read().decode()

    vhost = urllib.parse.quote("/", safe="")
    ex = urllib.parse.quote(exchange, safe="")
    q = urllib.parse.quote(queue, safe="")

    # Same topology the AR watcher declares (idempotent), so the job is kept even if the
    # laptop has never connected yet.
    api("PUT", f"exchanges/{vhost}/{ex}", {"type": "topic", "durable": True})
    api("PUT", f"queues/{vhost}/{q}", {"durable": True})
    api("POST", f"bindings/{vhost}/e/{ex}/q/{q}", {"routing_key": routing_key})

    # Paths relative to the samba folder, as the ground station publishes them.
    stem = args.stem
    suffixes = ["pos", "oth", "col", "shs"] + (["chk"] if args.chunk else [])
    job = {
        "job_id": f"manual-{stem}",
        "splat_asset_path": f"{stem}/{stem}.asset",
        "splat_data_paths": [f"{stem}/{stem}_{suffix}.bytes" for suffix in suffixes],
        "obj_path": f"{stem}/{stem}.obj",
        "transform_json_path": f"{stem}/{stem}.transform_colmap_to_enu.json",
    }
    print(api("POST", f"exchanges/{vhost}/{ex}/publish", {
        "properties": {"delivery_mode": 2, "content_type": "application/json"},
        "routing_key": routing_key,
        "payload": json.dumps(job),
        "payload_encoding": "string",
    }))


if __name__ == "__main__":
    main()

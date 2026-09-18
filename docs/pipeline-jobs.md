# Pipeline Jobs (RabbitMQ + shared folder)

The AR instance receives new scenes from the TRIFFID pipeline the same way the rest of the pipeline works: the files travel through a shared folder and RabbitMQ only carries the notification. The ground station Unity instance converts the splat, stages its own scene, then writes the job to the samba share and publishes `ar.ready` (`ArJobPublisher` in UnityGaussianSplattingAutoWatcher). Here, the editor script `Assets/Editor/ArSplatInboxWatcher.cs` picks the job up; it runs while the project is open in the Editor.

## Setup

1. Copy `.env.example` to `.env` at the repo root and set `RABBITMQ_HOST` to the pipeline machine's LAN address plus the broker credentials. On the pipeline machine, set `RABBITMQ_BIND` in its `.env` to that LAN address so the laptop can reach the broker. RabbitMQ's `guest` user only logs in from localhost, so use a real user.
2. Jobs arrive in `samba_folder/` at the repo root. Once the samba share is mounted, point `TRIFFID_SAMBA_DIR` at it instead (for example `\\192.168.1.50\unity` or `Z:\`).
3. Open the project. The Console shows a `Watching queue …` line with the broker, the credential source and the samba folder in use.

## Job message

Routing key `ar.ready` on the `pipeline.events` topic exchange (queue `ar.queue`). Each job is a `<stem>/` folder in the samba share:

```json
{
  "job_id": "flight_001",
  "splat_asset_path": "flight_001/flight_001.asset",
  "splat_data_paths": ["flight_001/flight_001_pos.bytes", "flight_001/flight_001_oth.bytes",
                       "flight_001/flight_001_col.bytes", "flight_001/flight_001_shs.bytes"],
  "obj_path": "flight_001/flight_001.obj",
  "transform_json_path": "flight_001/flight_001.transform_colmap_to_enu.json"
}
```

The splat is the Gaussian splat asset already converted by the ground station. It references its data files by GUID, so the `.asset` and every `.bytes` file must have their `.meta` files next to them. `splat_asset_path` and `splat_data_paths` are required; the mesh and geolocal json are optional. Paths are relative to the samba folder.

For each job the watcher:

1. Imports the asset and data files (with their `.meta` files), the obj and the json into `Assets/TriffidJobs/<stem>/`, replacing an earlier import of the same job. The shared copies are left in place.
2. Writes the asset, mesh and transform paths to `Assets/StreamingAssets/project_paths.json`. Inputs the job does not carry are removed.
3. Stages `Assets/Scenes/Harokopio.unity`: the Gaussian renderer, rotation aligner, JSON spawner and mesh loader are switched to the shared `project_paths.json`, the asset and mesh are loaded, then the JSON rotation is applied and the collider synced onto the new mesh. The scene is saved unless it already had unsaved edits. Detections are not produced yet; `ArSceneStager.StageDetections` is the empty slot for them.

Jobs wait in the queue during Play Mode and are staged when you exit it. An unreachable samba folder requeues the job; a job with missing files is dropped with the reason in the Console.

To test without the ground station, put a job folder in the samba folder and run `projects/TRIFFID/LocalScripts/publish_ar_job.py <stem>` where the broker's management API is reachable (on the pipeline machine).

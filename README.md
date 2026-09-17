# Unity AR Visualization System with Gaussian Splatting

A Unity-based augmented reality application for visualizing and interacting with geospatial Points of Interest (POIs) on 3D maps, built on top of [UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting).

> **Note:** This system is a research prototype developed within the framework of the **[TRIFFID Project](https://triffid-project.eu/)**. As an experimental tool, it is provided "as-is" and may contain bugs or unfinished features.

## Overview

This system allows real-time visualization and manipulation of geospatial data in AR/VR environments. Users can view, interact with, and modify Points of Interest on 3D terrain, with changes automatically persisted to GeoJSON files.

The current demo version focuses on PC VR usage with Meta Quest 3 through Quest Link. It includes demo scenes with Gaussian Splatting content, local GeoJSON-based annotation workflows, and a lightweight Python server for local synchronization testing.

## Features

* **GeoJSON Integration**: Load and visualize POIs from GeoJSON files with automatic coordinate parsing
* **Interactive POI Editing**: Move and reposition POIs in 3D space with real-time JSON updates
* **Point, Line, and Polygon Annotations**: Create and visualize different GeoJSON geometry types
* **Dual Input Support**: VR controllers and PC/debug input for development and testing
* **Controller Navigation**: Joysticks for smooth movement and XR-based interaction
* **Lock/Unlock Map**: Toggle map positioning for precise POI manipulation
* **Walk Mode**: Immersive VR navigation for large-scale terrain inspection
* **POI Filtering**: Selectively display POIs by category or properties
* **Information Panel**: Inspect selected annotation metadata during runtime
* **Reset Function**: Restore the map to its initial view
* **Runtime Synchronization**: Synchronize local annotation changes through a local GeoJSON API server
* **Atomic Local Persistence**: Store runtime changes safely to GeoJSON files
* **Demo Scenes**: Includes selected demo scenes with Gaussian Splatting assets for presentation and testing

## System Requirements

* **Unity**: 6000.3.7f1 (LTS)
* **OS**: Windows 11 tested
* **Hardware**:

  * Meta Quest 3 tested via Meta Quest Link USB-C
  * PC VR setup
  * HoloLens potential compatibility, not the current tested target
* **Python**: Python 3.x for the local GeoJSON demo server

> The current repository targets **PC VR / Quest Link**. Standalone Quest / Android APK deployment is not the main target of this demo version.

## Repository Structure

```text
.
├── README.md
├── LICENSE.md
├── .gitignore
└── projects/
    └── TRIFFID/
        ├── Assets/
        │   ├── Scenes/
        │   │   ├── Demo scenes
        │   │   └── Gaussian Splatting demo assets
        │   ├── Custom Scripts/
        │   ├── Prefabs/
        │   ├── XR/
        │   └── ...
        ├── LocalScripts/
        │   ├── local_geojson_server.py
        │   └── local_features.json
        ├── Packages/
        └── ProjectSettings/
```

## Setup

### 1. Clone the repository

```bash
git clone <repository-url>
cd <repository-folder>
```

### 2. Open the Unity project

Open the Unity project located at:

```text
projects/TRIFFID
```

Use Unity Hub and select the Unity version shown in:

```text
projects/TRIFFID/ProjectSettings/ProjectVersion.txt
```

### 3. Start the local GeoJSON server

From the repository root, run:

```bash
python ./projects/TRIFFID/LocalScripts/local_geojson_server.py --host 127.0.0.1 --port 8080 --db ./projects/TRIFFID/LocalScripts/local_features.json --auto-move-after 5 --move-interval 1 --move-step-lat 0.00001 --move-step-lon 0.00002
```

The local server exposes the following demo endpoints:

```text
GET    /wp-json/map-manager/v1/features
PUT    /wp-json/map-manager/v1/features
PATCH  /wp-json/map-manager/v1/features/{id}
DELETE /wp-json/map-manager/v1/features/{id}

GET    /wp-json/observer-sync/v1/status
PATCH  /wp-json/observer-sync/v1/status

GET    /wp-json/mqtt/v1/latest
PATCH  /wp-json/mqtt/v1/latest
```

### 4. Open a demo scene

In Unity, open one of the demo scenes under:

```text
projects/TRIFFID/Assets/Scenes
```

Then enter Play Mode

## Data Format

The application exchanges annotations as a GeoJSON `FeatureCollection`. Each feature uses the third coordinate as its absolute WGS84 altitude, while the extended height properties store the vertical distance above the configured map surface.

```json
{
  "type": "FeatureCollection",
  "features": [
    {
      "type": "Feature",
      "id": "point_001",
      "properties": {
        "class": "tree",
        "id": "point_001",
        "confidence": 1.0,
        "category": "environment",
        "source": "ground station",
        "altitude_m": 87.0964,
        "height_above_surface_m": 4.5864,
        "marker-color": "#00ff00"
      },
      "geometry": {
        "type": "Point",
        "coordinates": [23.708222, 37.960960, 87.0964]
      }
    },
    {
      "type": "Feature",
      "id": "line_001",
      "properties": {
        "class": "road",
        "id": "line_001",
        "confidence": 1.0,
        "category": "navigation",
        "source": "ground station",
        "altitude_m": 87.4190,
        "heights_above_surface_m": [8.1273, 13.6532, 5.8209],
        "marker-color": "#0000ff"
      },
      "geometry": {
        "type": "LineString",
        "coordinates": [
          [23.707925, 37.960138, 87.4190],
          [23.708536, 37.960562, 87.8477],
          [23.709371, 37.961113, 81.1797]
        ]
      }
    },
    {
      "type": "Feature",
      "id": "polygon_001",
      "properties": {
        "class": "safe",
        "id": "polygon_001",
        "confidence": 1.0,
        "category": "navigation",
        "source": "ground station",
        "altitude_m": 91.2,
        "heights_above_surface_m": [
          [5.2, 5.5, 4.9, 5.2]
        ],
        "marker-color": "#00ff00",
        "fill": "#00ff00",
        "fill-opacity": 0.25
      },
      "geometry": {
        "type": "Polygon",
        "coordinates": [
          [
            [23.707100, 37.960550, 91.2],
            [23.707420, 37.960780, 91.5],
            [23.707700, 37.960550, 90.9],
            [23.707100, 37.960550, 91.2]
          ]
        ]
      }
    }
  ]
}
```

Supported geometry types are `Point`, `MultiPoint`, `LineString`, `MultiLineString`, `Polygon`, and `MultiPolygon`.

### Identifiers and altitude fields

- `feature.id` and `properties.id` must contain the same annotation identifier.
- Every coordinate is `[longitude, latitude, altitude]`, where altitude is the absolute WGS84 altitude used for coordinate conversion and spawning.
- `altitude_m` stores the feature's reference absolute altitude.
- A Point uses the scalar `height_above_surface_m`.
- A LineString uses a flat `heights_above_surface_m` array aligned one-to-one with its coordinates.
- A Polygon uses a nested `heights_above_surface_m` array aligned with its coordinate rings. A polygon ring must repeat its first coordinate as its final coordinate.
- A height value may be `null` when no stored value is available and the surface raycast fails. Unity displays `-` for that value.

### Class handling

- Point and MultiPoint features are spawned only when `properties.class` matches an entry in `JsonSpawner.prefabEntries`.
- LineString and MultiLineString features accept any non-empty class value; their class does not need a prefab mapping.
- Polygon and MultiPolygon features are spawned only when their class is a drawable `XRControllerLogger.PolygonCategory` value (currently `safe` or `unsafe`) or matches an entry in `JsonSpawner.prefabEntries`.
- Unsupported Point or Polygon classes are skipped and do not create fallback nodes, centroids, or line renderers.

The server must return the top-level `FeatureCollection` object. A single `Feature` containing its own `features` array is not a valid response for this workflow.

## Notes on Local Synchronization

The local Python server is included for demo and testing purposes. It stores and serves GeoJSON annotations through a lightweight local API.

Runtime annotation operations may update the local GeoJSON file. This is expected during demo use when users create, move, or delete POIs.

The synchronization logic is designed to preserve local consistency during annotation editing and refresh operations. Invalid GeoJSON snapshots should not destroy the currently visible scene state.

## Controls

The demo supports XR controller interaction and PC/debug input for development and testing.

### Right Controller

| Input              | Action                                    |
| ------------------ | ----------------------------------------- |
| **A Button**       | Adds drawing nodes for lines and polygons |
| **Grip Button**    | Undo for unfinished lines and polygons    |
| **Joystick**       | Moves the user along the Y axis           |
| **Joystick Press** | Turns the camera by 90 degrees            |

### Left Controller

| Input           | Action                                               |
| --------------- | ---------------------------------------------------- |
| **X Button**    | Summons the currently open menu in front of the user |
| **Grip Button** | Saves and finalizes the active line or polygon       |
| **Joystick**    | Moves the user along the X/Z axes                    |

### PC / Debug Controls

PC controls are intended for development and testing without relying only on XR input.

| Key / Input        | Action                                                       |
| ------------------ | ------------------------------------------------------------ |
| **Mouse position** | Used as the screen ray target for debug placement            |
| **P**              | Place a point annotation                                     |
| **L**              | Add a line node                                              |
| **O**              | Add a polygon node                                           |
| **Enter**          | Finalize the current line or polygon                         |
| **Backspace**      | Undo the last unfinished line/polygon node                   |
| **Escape**         | Cancel the active unfinished line or polygon drawing session |

Undo is intentionally limited to unfinished line and polygon drawing. Finalized annotations and points should be removed through the normal delete workflow.


## Citation

If you use this work, the system, or parts of the implementation in academic or research work, please cite:

```bibtex
@misc{apostolakis2026interactive,
  title         = {Interactive Augmented Reality-enabled Outdoor Scene Visualization For Enhanced Real-time Disaster Response},
  author        = {Apostolakis, Dimitrios and Angelidis, Georgios and Argyriou, Vasileios and Sarigiannidis, Panagiotis and Papadopoulos, Georgios Th.},
  year          = {2026},
  eprint        = {2602.21874},
  archivePrefix = {arXiv},
  primaryClass  = {cs.HC},
  doi           = {https://arxiv.org/abs/2602.21874}
}
```

Paper link:

```text
https://arxiv.org/abs/2602.21874
```

## License and Attribution

This project is based on [UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting), originally authored by Aras Pranckevičius and licensed under the MIT License.

The original MIT license notice is preserved in `LICENSE.md`.

Additional project-specific code, XR interaction logic, GeoJSON synchronization logic, demo integration, and TRIFFID-specific extensions were developed as part of this research prototype unless otherwise stated.

## Research Context

This prototype was developed in the context of the TRIFFID project, focusing on XR-based visualization and interaction with robotic and geospatial data for disaster-response scenarios.

The system is intended to support experimentation with:

* 3D situational awareness
* Gaussian Splatting scene visualization
* geospatial annotation workflows
* XR interfaces for operational maps
* human-robot interaction interfaces
* disaster-scene visualization

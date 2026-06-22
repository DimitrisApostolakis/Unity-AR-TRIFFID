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

The system uses GeoJSON format with extended properties for AR metadata:

```json
{
  "type": "FeatureCollection",
  "lastModified": "2026-04-02 20:25:35",
  "features": [
    {
      "type": "Feature",
      "id": "unique_id",
      "properties": {
        "class": "poi_type",
        "id": "unique_id",
        "confidence": 0.98,
        "category": "classification",
        "source": "data_source",
        "altitude_m": 0.472,
        "marker-color": "#FF0000"
      },
      "geometry": {
        "type": "Point",
        "coordinates": [longitude, latitude, altitude]
      }
    }
  ]
}
```

Supported geometry types:

```text
Point
LineString
Polygon
```

For synchronization consistency, `feature.id` and `properties.id` should refer to the same annotation identifier.

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

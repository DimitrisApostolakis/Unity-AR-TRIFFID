#!/usr/bin/env bash
# Keep LF line endings on every platform (enforced by .gitattributes).
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
build_dir="$(mktemp -d "${TMPDIR:-/tmp}/ar2-regression.XXXXXX")"
trap 'rm -rf -- "$build_dir"' EXIT
mono_bin="${AR2_MONO:-mono}"
csc_bin="${AR2_CSC:-mcs}"
json_dll="${AR2_NEWTONSOFT_DLL:-/usr/lib/cli/Newtonsoft.Json-5.0/Newtonsoft.Json.dll}"
mono_options=()
if [[ -n "${AR2_MONO_CONFIG:-}" ]]; then mono_options=(--config "$AR2_MONO_CONFIG"); fi
compiler=("$csc_bin")
if [[ "$csc_bin" == *.exe ]]; then compiler=("$mono_bin" "${mono_options[@]}" "$csc_bin"); fi
if [[ ! -f "$json_dll" ]]; then
    printf '%s\n' 'Set AR2_NEWTONSOFT_DLL to Newtonsoft.Json.dll before running the tests.' >&2
    exit 2
fi
runtime_dir="$repo_root/projects/TRIFFID/Assets/TRIFFID/Runtime"
"${compiler[@]}" -langversion:latest -warnaserror -out:"$build_dir/AR2.Tests.exe" -r:"$json_dll" \
    "$runtime_dir/Features/Model/GeoJsonDataModels.cs" \
    "$runtime_dir/Features/Persistence/GeoJsonSyncState.cs" \
    "$runtime_dir/Features/Model/GeoJsonPolygonValidator.cs" \
    "$runtime_dir/Features/Persistence/GeoJsonAtomicFile.cs" \
    "$runtime_dir/Features/Sync/GeoJsonPayloadParser.cs" \
    "$runtime_dir/Features/Presentation/GeoJsonVisibilityFilter.cs" \
    "$runtime_dir/Features/Sync/GeoJsonSyncCoordinator.cs" \
    "$repo_root/projects/TRIFFID/Assets/TRIFFID/Editor/Checks/AR2CoreRegressionTests.cs" \
    "$repo_root/tests/headless/CoordinatorStubs.cs" \
    "$repo_root/tests/headless/CoordinatorRegressionTests.cs" \
    "$repo_root/tests/headless/HeadlessMain.cs"
cp -- "$json_dll" "$build_dir/Newtonsoft.Json.dll"
"$mono_bin" "${mono_options[@]}" "$build_dir/AR2.Tests.exe"

"${compiler[@]}" -langversion:latest -warnaserror -define:UNITY_EDITOR -out:"$build_dir/AR2.MeshTests.exe" \
    "$repo_root/projects/TRIFFID/Assets/TRIFFID/Runtime/Mapping/Loading/MeshPathJsonLoader.cs" \
    "$repo_root/tests/headless/MeshLoaderStubs.cs" \
    "$repo_root/tests/headless/MeshLoaderRegressionTests.cs"
"$mono_bin" "${mono_options[@]}" "$build_dir/AR2.MeshTests.exe"

# Keep runtime code and its checks in separate assemblies, as Unity does for
# Assets/Editor, so inaccessible runtime helpers fail this build as well.
"${compiler[@]}" -langversion:latest -warnaserror -target:library -out:"$build_dir/AR2.InteractionRuntime.dll" \
    "$runtime_dir/Geospatial/Coordinates/GeographicSurfaceQuery.cs" \
    "$repo_root/projects/TRIFFID/Assets/TRIFFID/Runtime/UI/InfoPanel/PointInfoPanel.cs" \
    "$repo_root/projects/TRIFFID/Assets/TRIFFID/Runtime/Features/Presentation/MarkerEventManager.cs" \
    "$repo_root/projects/TRIFFID/Assets/TRIFFID/Runtime/Interaction/Annotations/RuntimeLine.cs" \
    "$repo_root/tests/headless/InteractionStubs.cs"
"${compiler[@]}" -langversion:latest -warnaserror -out:"$build_dir/AR2.InteractionTests.exe" \
    -r:"$build_dir/AR2.InteractionRuntime.dll" \
    "$repo_root/tests/headless/InteractionRegressionTests.cs"
"$mono_bin" "${mono_options[@]}" "$build_dir/AR2.InteractionTests.exe"

# Editor-only grouping, search and registrations, compiled against Editor doubles.
"${compiler[@]}" -langversion:latest -warnaserror -out:"$build_dir/AR2.InspectorTests.exe" \
    "$repo_root/projects/TRIFFID/Assets/TRIFFID/Editor/Inspectors/TriffidGroupedEditor.cs" \
    "$repo_root/projects/TRIFFID/Assets/TRIFFID/Editor/Inspectors/TriffidComponentEditors.cs" \
    "$repo_root/projects/TRIFFID/Assets/TRIFFID/Editor/Inspectors/UnifiedPathInspectors.cs" \
    "$repo_root/tests/headless/InspectorStubs.cs" \
    "$repo_root/tests/headless/InspectorRegressionTests.cs"
"$mono_bin" "${mono_options[@]}" "$build_dir/AR2.InspectorTests.exe"

# Native experiment entry points compile against Editor doubles. These checks
# validate guards/pose bookkeeping, not native matrix or scene measurements.
"${compiler[@]}" -langversion:latest -warnaserror -out:"$build_dir/AR2.SceneCollectorTests.exe" -r:"$json_dll" \
    "$runtime_dir/Features/Model/GeoJsonDataModels.cs" \
    "$runtime_dir/Features/Persistence/GeoJsonSyncState.cs" \
    "$runtime_dir/Features/Persistence/GeoJsonAtomicFile.cs" \
    "$repo_root/projects/TRIFFID/Assets/TRIFFID/Editor/Experiments/SceneConsistencyCollector.cs" \
    "$repo_root/projects/TRIFFID/Assets/TRIFFID/Editor/Experiments/SceneConsistencySession.cs" \
    "$repo_root/tests/experiments/SceneEditorStubs.cs" \
    "$repo_root/tests/experiments/SceneCollectorSafetyTests.cs"
"$mono_bin" "${mono_options[@]}" "$build_dir/AR2.SceneCollectorTests.exe"

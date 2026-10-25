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
coordinate_dir="$repo_root/projects/TRIFFID/Assets/Custom Scripts/Coordinate Scripts"
"${compiler[@]}" -langversion:latest -warnaserror -out:"$build_dir/AR2.Tests.exe" -r:"$json_dll" \
    "$coordinate_dir/GeoJsonDataModels.cs" \
    "$coordinate_dir/GeoJsonSyncState.cs" \
    "$coordinate_dir/GeoJsonPolygonValidator.cs" \
    "$coordinate_dir/GeoJsonAtomicFile.cs" \
    "$coordinate_dir/GeoJsonPayloadParser.cs" \
    "$coordinate_dir/GeoJsonSyncCoordinator.cs" \
    "$repo_root/projects/TRIFFID/Assets/Editor/AR2CoreRegressionTests.cs" \
    "$repo_root/tests/headless/CoordinatorStubs.cs" \
    "$repo_root/tests/headless/CoordinatorRegressionTests.cs" \
    "$repo_root/tests/headless/HeadlessMain.cs"
cp -- "$json_dll" "$build_dir/Newtonsoft.Json.dll"
"$mono_bin" "${mono_options[@]}" "$build_dir/AR2.Tests.exe"

"${compiler[@]}" -langversion:latest -warnaserror -define:UNITY_EDITOR -out:"$build_dir/AR2.MeshTests.exe" \
    "$repo_root/projects/TRIFFID/Assets/Custom Scripts/Map Scripts/MeshPathJsonLoader.cs" \
    "$repo_root/tests/headless/MeshLoaderStubs.cs" \
    "$repo_root/tests/headless/MeshLoaderRegressionTests.cs"
"$mono_bin" "${mono_options[@]}" "$build_dir/AR2.MeshTests.exe"

"${compiler[@]}" -langversion:latest -warnaserror -out:"$build_dir/AR2.InteractionTests.exe" \
    "$coordinate_dir/GeographicSurfaceQuery.cs" \
    "$repo_root/projects/TRIFFID/Assets/Custom Scripts/Icon Scripts/PointInfoPanel.cs" \
    "$repo_root/projects/TRIFFID/Assets/Custom Scripts/Icon Scripts/MarkerEventManager.cs" \
    "$repo_root/tests/headless/InteractionStubs.cs" \
    "$repo_root/tests/headless/InteractionRegressionTests.cs"
"$mono_bin" "${mono_options[@]}" "$build_dir/AR2.InteractionTests.exe"

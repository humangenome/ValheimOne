#!/usr/bin/env bash
set -euo pipefail

project_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
output_dll="${project_root}/src/ValheimOne/bin/Release/net472/ValheimOne.dll"

export DOTNET_ROLL_FORWARD=LatestMajor

dotnet build "${project_root}/ValheimOne.sln" -c Release

if [[ ! -f "${output_dll}" ]]; then
    echo "Build completed, but the expected DLL was not found: ${output_dll}" >&2
    exit 1
fi

echo "ValheimOne.dll: ${output_dll}"

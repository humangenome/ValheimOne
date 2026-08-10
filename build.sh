#!/usr/bin/env bash
set -euo pipefail

project_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
output_dll="${project_root}/src/ValheimOne/bin/Release/net472/ValheimOne.dll"
global_json="${project_root}/global.json"

if ! pinned_sdk="$(jq -er '.sdk.version' "${global_json}")"; then
    echo "Unable to read the pinned .NET SDK version from ${global_json}." >&2
    exit 1
fi

if ! resolved_sdk="$(dotnet --version 2>/dev/null)"; then
    resolved_sdk="not found"
fi

echo "Resolved .NET SDK: ${resolved_sdk}"

if [[ "${resolved_sdk}" != "${pinned_sdk}" ]]; then
    echo "ValheimOne requires .NET SDK ${pinned_sdk}, but dotnet resolved to ${resolved_sdk}." >&2
    echo "Install the pinned SDK with:" >&2
    printf '  curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --version %s --install-dir "$HOME/.dotnet"\n' "${pinned_sdk}" >&2
    echo 'Then put "$HOME/.dotnet" first on PATH.' >&2
    exit 1
fi

export DOTNET_ROLL_FORWARD=LatestMajor

dotnet build "${project_root}/ValheimOne.sln" -c Release

if [[ ! -f "${output_dll}" ]]; then
    echo "Build completed, but the expected DLL was not found: ${output_dll}" >&2
    exit 1
fi

echo "ValheimOne.dll: ${output_dll}"

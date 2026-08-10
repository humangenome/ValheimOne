#!/usr/bin/env bash
# Records authoritative release hashes produced on a developer machine.
#
# By default this runs the clean build performed by tools/package-release.sh.
# Use --skip-build only when the release artifacts already came from a clean
# build with the currently resolved pinned SDK.
#
# Usage: tools/record-provenance.sh [--skip-build] [--force]

set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
global_json="${repo_root}/global.json"
version_file="${repo_root}/src/ValheimOne/Networking/VersionInfo.cs"
package_script="${repo_root}/tools/package-release.sh"
provenance="${repo_root}/tools/release/provenance.tsv"
server_manifest="${HOME}/valheim-modding/server/steamapps/appmanifest_896660.acf"
skip_build=false
force=false

usage() {
    printf '%s\n' \
        'Usage: tools/record-provenance.sh [--skip-build] [--force]' \
        '' \
        'Build and package the current version, then write its authoritative hashes' \
        'with its commit and toolchain to tools/release/provenance.tsv.' \
        '' \
        '  --skip-build  Package existing build output without rebuilding.' \
        '  --force       Replace a row even when its inputs or hashes differ.' \
        '  -h, --help    Show this help.'
}

fail() { printf 'record-provenance: %s\n' "$*" >&2; exit 1; }

while (( $# > 0 )); do
    case "$1" in
        --skip-build) skip_build=true ;;
        --force) force=true ;;
        -h|--help) usage; exit 0 ;;
        *) fail "unknown argument: $1" ;;
    esac
    shift
done

[[ -f ${provenance} ]] || fail "missing provenance ledger ${provenance}"

if ! commit="$(git -C "${repo_root}" rev-parse --verify HEAD 2>/dev/null)"; then
    fail 'unable to resolve the Git commit; provenance must be recorded from a Git checkout because the commit SHA is embedded in ValheimOne.dll'
fi
[[ ${commit} =~ ^[0-9a-f]{40}$ ]] || fail "Git HEAD is not a full 40-hex commit SHA: ${commit}"

# Keep this check in lockstep with build.sh: global.json is authoritative, and
# the SDK selected from the repository root must match it exactly.
if ! pinned_sdk="$(jq -er '.sdk.version' "${global_json}")"; then
    fail "unable to read the pinned .NET SDK version from ${global_json}"
fi
if ! resolved_sdk="$(cd "${repo_root}" && dotnet --version 2>/dev/null)"; then
    resolved_sdk="not found"
fi
if [[ ${resolved_sdk} != "${pinned_sdk}" ]]; then
    printf 'record-provenance: ValheimOne requires .NET SDK %s, but dotnet resolved to %s.\n' \
        "${pinned_sdk}" "${resolved_sdk}" >&2
    printf 'Install it with:\n' >&2
    printf '  curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --version %s --install-dir "$HOME/.dotnet"\n' \
        "${pinned_sdk}" >&2
    fail 'put "$HOME/.dotnet" first on PATH and try again'
fi

version="$(sed -nE 's/.*public const string PluginVersion = "([^"]+)";.*/\1/p' "${version_file}" | head -n 1)"
[[ -n ${version} ]] || fail "could not read PluginVersion from ${version_file}"

bepinex_pack="$(sed -nE 's/^bepinex_pack_version="([^"]+)"/\1/p' "${package_script}" | head -n 1)"
[[ -n ${bepinex_pack} ]] || fail "could not read the BepInEx pack pin from ${package_script}"

[[ -f ${server_manifest} ]] || fail "missing Valheim server manifest ${server_manifest}"
valheim_buildid="$({
    awk -F '"' '/^[[:space:]]*"buildid"[[:space:]]+"[0-9]+"/ { print $4; exit }' "${server_manifest}"
} || true)"
[[ ${valheim_buildid} =~ ^[0-9]+$ ]] || fail "could not read a numeric buildid from ${server_manifest}"

if ${skip_build}; then
    "${package_script}" --skip-build
else
    "${package_script}"
fi

dll="${repo_root}/src/ValheimOne/bin/Release/net472/ValheimOne.dll"
plugin_zip="${repo_root}/artifacts/release/ValheimOne-${version}.zip"
full_zip="${repo_root}/artifacts/release/ValheimOne-full-${version}.zip"
for artifact in "${dll}" "${plugin_zip}" "${full_zip}"; do
    [[ -s ${artifact} ]] || fail "missing or empty release artifact ${artifact}"
done

dll_sha256="$(sha256sum "${dll}" | cut -d' ' -f1)"
plugin_zip_sha256="$(sha256sum "${plugin_zip}" | cut -d' ' -f1)"
full_zip_sha256="$(sha256sum "${full_zip}" | cut -d' ' -f1)"

row_count="$(awk -F '\t' -v version="${version}" '$1 == version { count++ } END { print count + 0 }' "${provenance}")"
(( row_count <= 1 )) || fail "provenance ledger contains ${row_count} rows for version ${version}"
existing_row="$(awk -F '\t' -v version="${version}" '$1 == version { print; exit }' "${provenance}")"

if [[ -n ${existing_row} ]]; then
    IFS=$'\t' read -r \
        _ existing_commit _ _ _ existing_dll_sha256 existing_plugin_zip_sha256 existing_full_zip_sha256 _ \
        <<< "${existing_row}"

    if [[ ${existing_commit} != "${commit}" ||
          ${existing_dll_sha256} != "${dll_sha256}" ||
          ${existing_plugin_zip_sha256} != "${plugin_zip_sha256}" ||
          ${existing_full_zip_sha256} != "${full_zip_sha256}" ]]; then
        if ! ${force}; then
            printf 'record-provenance: refusing to replace version %s because its reproduction inputs differ:\n' "${version}" >&2
            printf '  commit     recorded: %s\n             current:  %s\n' "${existing_commit}" "${commit}" >&2
            printf '  DLL        recorded: %s\n             current:  %s\n' "${existing_dll_sha256}" "${dll_sha256}" >&2
            printf '  plugin zip recorded: %s\n             current:  %s\n' "${existing_plugin_zip_sha256}" "${plugin_zip_sha256}" >&2
            printf '  full zip   recorded: %s\n             current:  %s\n' "${existing_full_zip_sha256}" "${full_zip_sha256}" >&2
            fail 'review the divergence, then re-run with --force only for a deliberate re-record'
        fi
        printf 'record-provenance: --force replacing version %s with reviewed hashes\n' "${version}"
    fi
fi

new_row="$(printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\tyes' \
    "${version}" "${commit}" "${resolved_sdk}" "${bepinex_pack}" "${valheim_buildid}" \
    "${dll_sha256}" "${plugin_zip_sha256}" "${full_zip_sha256}")"
tmp_file="$(mktemp "${provenance}.tmp.XXXXXX")"
trap 'rm -f "${tmp_file}"' EXIT

awk -F '\t' -v version="${version}" -v replacement="${new_row}" '
    $1 == version {
        if (!replaced) {
            print replacement
            replaced = 1
        }
        next
    }
    { print }
    END {
        if (!replaced) print replacement
    }
' "${provenance}" > "${tmp_file}"
chmod 0644 "${tmp_file}"
mv "${tmp_file}" "${provenance}"
trap - EXIT

printf 'PROVENANCE RECORDED %s\n' "${version}"
printf '  commit:           %s\n' "${commit}"
printf '  dotnet_sdk:       %s\n' "${resolved_sdk}"
printf '  bepinex_pack:      %s\n' "${bepinex_pack}"
printf '  valheim_buildid:   %s\n' "${valheim_buildid}"
printf '  dll_sha256:        %s\n' "${dll_sha256}"
printf '  plugin_zip_sha256: %s\n' "${plugin_zip_sha256}"
printf '  full_zip_sha256:   %s\n' "${full_zip_sha256}"

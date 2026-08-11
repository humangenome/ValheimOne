#!/usr/bin/env bash
# Asserts that CI reproduced the developer-recorded release artifacts exactly.
#
# The Valheim build ID must be the value resolved by the toolchain step. Pass it
# explicitly or through VALHEIM_BUILDID.
#
# Usage: tools/ci/assert-reproducible.sh --valheim-buildid <buildid>
#
# Prints `REPRODUCIBLE <version>` on success.

set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
version_file="${repo_root}/src/ValheimOne/Networking/VersionInfo.cs"
package_script="${repo_root}/tools/package-release.sh"
provenance="${repo_root}/tools/release/provenance.tsv"
valheim_buildid="${VALHEIM_BUILDID:-}"

usage() {
    printf '%s\n' \
        'Usage: tools/ci/assert-reproducible.sh --valheim-buildid <buildid>' \
        '' \
        'Compare the current release DLL and both zips with the authoritative' \
        'cross-machine hashes in tools/release/provenance.tsv.' \
        '' \
        '  --valheim-buildid <id>  Build ID resolved by the CI toolchain.' \
        '  -h, --help              Show this help.'
}

fail() { printf 'REPRODUCIBILITY FAIL: %s\n' "$*" >&2; exit 1; }

while (( $# > 0 )); do
    case "$1" in
        --valheim-buildid)
            (( $# >= 2 )) || fail '--valheim-buildid requires a value'
            valheim_buildid="$2"
            shift
            ;;
        -h|--help) usage; exit 0 ;;
        *) fail "unknown argument: $1" ;;
    esac
    shift
done

plugin_version="$(sed -nE 's/.*public const string PluginVersion = "([^"]+)";.*/\1/p' "${version_file}" | head -n 1)"
[[ -n ${plugin_version} ]] || fail "could not read PluginVersion from ${version_file}"
[[ -f ${provenance} ]] || fail "missing provenance ledger ${provenance}"

row_count="$(awk -F '\t' -v version="${plugin_version}" '$1 == version { count++ } END { print count + 0 }' "${provenance}")"
(( row_count > 0 )) || fail "no authoritative provenance row for ${plugin_version}; run tools/record-provenance.sh and commit tools/release/provenance.tsv before releasing"
(( row_count == 1 )) || fail "provenance ledger contains ${row_count} rows for version ${plugin_version}"
row="$(awk -F '\t' -v version="${plugin_version}" '$1 == version { print; exit }' "${provenance}")"
IFS=$'\t' read -r \
    recorded_version recorded_commit recorded_sdk recorded_bepinex recorded_buildid \
    expected_dll expected_plugin_zip expected_full_zip reproducible \
    <<< "${row}"

case "${reproducible}" in
    yes) ;;
    no) fail "version ${plugin_version} predates the SDK pin and must be re-recorded before it can be released reproducibly" ;;
    *) fail "version ${plugin_version} has invalid reproducible=${reproducible:-<empty>} in ${provenance}" ;;
esac

[[ ${recorded_commit} =~ ^[0-9a-f]{40}$ ]] ||
    fail "version ${plugin_version} has invalid commit=${recorded_commit:-<empty>} in ${provenance}"
if ! actual_commit="$(git -C "${repo_root}" rev-parse --verify HEAD 2>/dev/null)"; then
    fail 'could not resolve the Git commit CI is building'
fi

[[ -n ${valheim_buildid} ]] || fail 'no Valheim build ID supplied; pass --valheim-buildid or set VALHEIM_BUILDID'

if ! actual_sdk="$(cd "${repo_root}" && dotnet --version 2>/dev/null)"; then
    actual_sdk="not found"
fi
package_bepinex="$(sed -nE 's/^bepinex_pack_version="([^"]+)"/\1/p' "${package_script}" | head -n 1)"
[[ -n ${package_bepinex} ]] || fail "could not read the BepInEx pack pin from ${package_script}"
actual_bepinex="${BEPINEX_VERSION:-${package_bepinex}}"

dll="${repo_root}/src/ValheimOne/bin/Release/net472/ValheimOne.dll"
plugin_zip="${repo_root}/artifacts/release/ValheimOne-${plugin_version}.zip"
full_zip="${repo_root}/artifacts/release/ValheimOne-full-${plugin_version}.zip"

hash_or_missing() {
    if [[ -s $1 ]]; then
        sha256sum "$1" | cut -d' ' -f1
    else
        printf 'MISSING'
    fi
}

actual_dll="$(hash_or_missing "${dll}")"
actual_plugin_zip="$(hash_or_missing "${plugin_zip}")"
actual_full_zip="$(hash_or_missing "${full_zip}")"
mismatches=0

compare_value() {
    local label="$1" expected="$2" actual="$3"
    if [[ ${expected} != "${actual}" ]]; then
        printf 'REPRODUCIBILITY MISMATCH: %s\n  expected: %s\n  actual:   %s\n' \
            "${label}" "${expected}" "${actual}" >&2
        mismatches=$((mismatches + 1))
    else
        printf 'reproduced: %s %s\n' "${label}" "${actual}"
    fi
}

compare_value 'dotnet SDK' "${recorded_sdk}" "${actual_sdk}"
# The provenance row is committed after the recorded build, so the release commit
# always differs from the recorded one by the ledger/contract-pass files alone.
# The DLL embeds no commit SHA (IncludeSourceRevisionInInformationalVersion=false),
# so the artifact hash comparisons below are the reproducibility gate; the commits
# are printed for the audit trail.
printf 'provenance recorded at commit %s; this run builds %s\n' \
    "${recorded_commit}" "${actual_commit}"
compare_value 'BepInEx pack' "${recorded_bepinex}" "${actual_bepinex}"
compare_value 'Valheim build ID' "${recorded_buildid}" "${valheim_buildid}"
compare_value 'ValheimOne.dll' "${expected_dll}" "${actual_dll}"
compare_value "ValheimOne-${plugin_version}.zip" "${expected_plugin_zip}" "${actual_plugin_zip}"
compare_value "ValheimOne-full-${plugin_version}.zip" "${expected_full_zip}" "${actual_full_zip}"

if [[ ${actual_bepinex} != "${package_bepinex}" ]]; then
    printf 'REPRODUCIBILITY MISMATCH: CI BepInEx toolchain vs packaging pin\n  expected: %s\n  actual:   %s\n' \
        "${package_bepinex}" "${actual_bepinex}" >&2
    mismatches=$((mismatches + 1))
fi

(( mismatches == 0 )) || fail "${mismatches} recorded value(s) did not match CI output for ${plugin_version}"
printf 'REPRODUCIBLE %s\n' "${plugin_version}"

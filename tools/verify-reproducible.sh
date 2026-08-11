#!/usr/bin/env bash
# Rebuilds ValheimOne and verifies the binary against release provenance.
#
# Usage:
#   tools/verify-reproducible.sh
#   tools/verify-reproducible.sh --release <tag>
#   tools/verify-reproducible.sh --against <path-to-dll-or-plugin-zip>
#   tools/verify-reproducible.sh --allow-dirty [--release <tag> | --against <path>]

set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
version_file="${repo_root}/src/ValheimOne/Networking/VersionInfo.cs"
package_script="${repo_root}/tools/package-release.sh"
provenance="${repo_root}/tools/release/provenance.tsv"
release_tag=""
against_path=""
allow_dirty=false
unofficial=false
tmp_dir=""

usage() {
    printf '%s\n' \
        'Usage:' \
        '  tools/verify-reproducible.sh' \
        '  tools/verify-reproducible.sh --release <tag>' \
        '  tools/verify-reproducible.sh --against <path-to-dll-or-plugin-zip>' \
        '  tools/verify-reproducible.sh --allow-dirty [--release <tag> | --against <path>]' \
        '' \
        'Clean-build the current source and compare ValheimOne.dll with its' \
        'authoritative hash. --release downloads the upstream GitHub plugin zip;' \
        '--against checks a DLL or plugin zip already on disk.' \
        '' \
        '  --release <tag>  Verify the DLL inside that published GitHub release.' \
        '  --against <path> Verify a local .dll or plugin .zip.' \
        '  --allow-dirty    Continue with tracked changes, but mark the result unofficial.' \
        '  -h, --help       Show this help.'
}

fail() { printf 'verify-reproducible: %s\n' "$*" >&2; exit 1; }

while (( $# > 0 )); do
    case "$1" in
        --release)
            (( $# >= 2 )) || fail '--release requires a tag'
            release_tag="$2"
            shift
            ;;
        --against)
            (( $# >= 2 )) || fail '--against requires a path'
            against_path="$2"
            shift
            ;;
        --allow-dirty) allow_dirty=true ;;
        -h|--help) usage; exit 0 ;;
        *) fail "unknown argument: $1" ;;
    esac
    shift
done

[[ -z ${release_tag} || -z ${against_path} ]] || fail '--release and --against are mutually exclusive'

on_exit() {
    local status=$?
    if [[ -n ${tmp_dir} && -d ${tmp_dir} ]]; then
        rm -rf "${tmp_dir}"
    fi
    if (( status != 0 )); then
        printf 'NOT REPRODUCIBLE\n' >&2
    fi
}
trap on_exit EXIT

git_checkout_message='reproduction requires a git clone checked out at the release tag, not a downloaded source tarball or zip, so the exact tagged sources are what gets built'
if ! git -C "${repo_root}" rev-parse --git-dir >/dev/null 2>&1 ||
   ! checkout_root="$(git -C "${repo_root}" rev-parse --show-toplevel 2>/dev/null)" ||
   [[ ${checkout_root} != "${repo_root}" ]]; then
    fail "${git_checkout_message}"
fi
if ! head_commit="$(git -C "${repo_root}" rev-parse --verify HEAD 2>/dev/null)"; then
    fail "${git_checkout_message}"
fi
printf 'building HEAD commit: %s\n' "${head_commit}"

dirty_files="$(git -C "${repo_root}" diff --name-only --no-ext-diff HEAD --)" ||
    fail 'could not inspect the working tree'
if [[ -n ${dirty_files} ]]; then
    if ! ${allow_dirty}; then
        printf 'verify-reproducible: refusing to certify a dirty working tree; tracked files differ from HEAD:\n' >&2
        while IFS= read -r dirty_file; do
            printf '  %s\n' "${dirty_file}" >&2
        done <<< "${dirty_files}"
        fail 'commit or restore those files, or use --allow-dirty for an explicitly unofficial result'
    fi

    unofficial=true
    printf 'verify-reproducible: WARNING: tracked files differ from HEAD:\n' >&2
    while IFS= read -r dirty_file; do
        printf '  %s\n' "${dirty_file}" >&2
    done <<< "${dirty_files}"
    printf 'verify-reproducible: --allow-dirty makes this result UNOFFICIAL; the certified artifacts come from committed sources only.\n' >&2
fi

if [[ -n ${release_tag} ]]; then
    if ! tag_commit="$(git -C "${repo_root}" rev-parse --verify "refs/tags/${release_tag}^{commit}" 2>/dev/null)"; then
        fail "release tag ${release_tag} does not exist in this checkout"
    fi
    if [[ ${head_commit} != "${tag_commit}" ]]; then
        printf 'verify-reproducible: checkout does not match release tag %s\n  HEAD: %s\n  tag:  %s\n' \
            "${release_tag}" "${head_commit}" "${tag_commit}" >&2
        exit 1
    fi
fi

version="$(sed -nE 's/.*public const string PluginVersion = "([^"]+)";.*/\1/p' "${version_file}" | head -n 1)"
[[ -n ${version} ]] || fail "could not read PluginVersion from ${version_file}"
[[ -f ${provenance} ]] || fail "missing provenance ledger ${provenance}"

row_count="$(awk -F '\t' -v version="${version}" '$1 == version { count++ } END { print count + 0 }' "${provenance}")"
(( row_count > 0 )) || fail "no authoritative provenance row for ${version}"
(( row_count == 1 )) || fail "provenance ledger contains ${row_count} rows for version ${version}"
row="$(awk -F '\t' -v version="${version}" '$1 == version { print; exit }' "${provenance}")"
IFS=$'\t' read -r \
    recorded_version recorded_commit recorded_sdk recorded_bepinex recorded_buildid \
    expected_dll _ _ reproducible \
    <<< "${row}"

case "${reproducible}" in
    yes) ;;
    no) fail "version ${version} predates the SDK pin and has no cross-machine reproducibility claim; it must be re-recorded first" ;;
    *) fail "version ${version} has invalid reproducible=${reproducible:-<empty>} in ${provenance}" ;;
esac

[[ ${recorded_commit} =~ ^[0-9a-f]{40}$ ]] ||
    fail "version ${version} has invalid commit=${recorded_commit:-<empty>} in ${provenance}"
# The ledger row for a release is committed after the recorded build, so the
# tagged release commit differs from the recorded one by the ledger files alone.
# The DLL embeds no commit SHA; byte-equality of the deterministic build proves
# the sources match. Print both commits for the audit trail.
if [[ ${recorded_commit} != "${head_commit}" ]]; then
    printf 'verify-reproducible: provenance recorded at %s; this checkout builds %s\n' \
        "${recorded_commit}" "${head_commit}"
fi

if [[ -n ${release_tag} ]]; then
    release_version="${release_tag#v}"
    [[ ${release_version} == "${version}" ]] || fail "release ${release_tag} is version ${release_version}, but this checkout is ${version}"
fi

if ! actual_sdk="$(cd "${repo_root}" && dotnet --version 2>/dev/null)"; then
    actual_sdk="not found"
fi
if [[ ${recorded_sdk} != "${actual_sdk}" ]]; then
    printf 'verify-reproducible: .NET SDK mismatch\n  expected: %s\n  actual:   %s\n' \
        "${recorded_sdk}" "${actual_sdk}" >&2
    exit 1
fi

package_bepinex="$(sed -nE 's/^bepinex_pack_version="([^"]+)"/\1/p' "${package_script}" | head -n 1)"
[[ -n ${package_bepinex} ]] || fail "could not read the BepInEx pack pin from ${package_script}"
if [[ ${recorded_bepinex} != "${package_bepinex}" ]]; then
    printf 'verify-reproducible: BepInEx pack pin mismatch\n  expected: %s\n  actual:   %s\n' \
        "${recorded_bepinex}" "${package_bepinex}" >&2
    exit 1
fi

rm -rf "${repo_root}/src/ValheimOne/bin" "${repo_root}/src/ValheimOne/obj"
"${repo_root}/build.sh"

built_dll="${repo_root}/src/ValheimOne/bin/Release/net472/ValheimOne.dll"
[[ -s ${built_dll} ]] || fail "missing clean build output ${built_dll}"

compare_dll() {
    local label="$1" path="$2" actual
    actual="$(sha256sum "${path}" | cut -d' ' -f1)"
    if [[ ${actual} != "${expected_dll}" ]]; then
        printf 'verify-reproducible: %s mismatch\n  expected: %s\n  actual:   %s\n' \
            "${label}" "${expected_dll}" "${actual}" >&2
        return 1
    fi
    printf 'verified: %s %s\n' "${label}" "${actual}"
}

compare_dll 'clean build ValheimOne.dll' "${built_dll}" || exit 1

verify_zip_dll() {
    local label="$1" zip_path="$2" extracted_dll="$3"
    if ! unzip -p "${zip_path}" BepInEx/plugins/ValheimOne.dll > "${extracted_dll}"; then
        fail "${label} does not contain BepInEx/plugins/ValheimOne.dll"
    fi
    [[ -s ${extracted_dll} ]] || fail "${label} contains an empty ValheimOne.dll"
    compare_dll "${label} ValheimOne.dll" "${extracted_dll}" || exit 1
}

if [[ -n ${release_tag} ]]; then
    command -v gh >/dev/null 2>&1 || fail 'gh is required for --release'
    tmp_dir="$(mktemp -d)"
    release_zip="${tmp_dir}/ValheimOne-${version}.zip"
    gh release download "${release_tag}" \
        --repo HumanGenome/ValheimOne \
        --pattern "ValheimOne-${version}.zip" \
        --dir "${tmp_dir}"
    [[ -s ${release_zip} ]] || fail "release ${release_tag} did not provide ValheimOne-${version}.zip"
    verify_zip_dll "published ${release_tag} plugin zip" "${release_zip}" "${tmp_dir}/published.dll"
elif [[ -n ${against_path} ]]; then
    [[ -f ${against_path} ]] || fail "comparison file not found: ${against_path}"
    case "${against_path,,}" in
        *.dll) compare_dll "${against_path}" "${against_path}" || exit 1 ;;
        *.zip)
            tmp_dir="$(mktemp -d)"
            verify_zip_dll "${against_path}" "${against_path}" "${tmp_dir}/against.dll"
            ;;
        *) fail '--against accepts a .dll or .zip file' ;;
    esac
fi

if ${unofficial}; then
    printf 'UNOFFICIAL REPRODUCIBLE %s (dirty working tree)\n' "${version}"
else
    printf 'REPRODUCIBLE %s\n' "${version}"
fi

#!/usr/bin/env bash
# Asserts that ValheimOne's version is stated consistently everywhere.
#
# `VersionInfo.PluginVersion` is the single source of truth. This script fails
# if the csproj version properties disagree with it, and — when a release tag is
# supplied — if the tag or the CHANGELOG heading disagrees too.
#
# Usage:
#   tools/ci/assert-version.sh              # internal consistency only
#   tools/ci/assert-version.sh --tag v1.2.3 # full release gate
#
# Prints `VERSION ASSERT PASS <version>` on success.

set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
version_file="${repo_root}/src/ValheimOne/Networking/VersionInfo.cs"
csproj="${repo_root}/src/ValheimOne/ValheimOne.csproj"
changelog="${repo_root}/CHANGELOG.md"

tag=""
while (( $# > 0 )); do
    case $1 in
        --tag)
            [[ $# -ge 2 ]] || { echo "assert-version: --tag needs a value" >&2; exit 2; }
            tag=$2
            shift 2
            ;;
        -h|--help)
            sed -n '2,14p' "${BASH_SOURCE[0]}"
            exit 0
            ;;
        *)
            echo "assert-version: unknown argument: $1" >&2
            exit 2
            ;;
    esac
done

failures=0
fail() {
    printf 'VERSION ASSERT FAIL: %s\n' "$*" >&2
    failures=$(( failures + 1 ))
}

# ------------ single source of truth

[[ -f ${version_file} ]] || { echo "assert-version: missing ${version_file}" >&2; exit 1; }
[[ -f ${csproj} ]] || { echo "assert-version: missing ${csproj}" >&2; exit 1; }

plugin_version_count=$(grep -cE 'public const string PluginVersion = "[^"]+";' "${version_file}" || true)
if [[ ${plugin_version_count} -ne 1 ]]; then
    echo "assert-version: expected exactly one PluginVersion declaration in ${version_file}, found ${plugin_version_count}" >&2
    exit 1
fi

plugin_version=$(sed -nE 's/.*public const string PluginVersion = "([^"]+)";.*/\1/p' "${version_file}")
if [[ ! ${plugin_version} =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo "assert-version: PluginVersion '${plugin_version}' is not MAJOR.MINOR.PATCH" >&2
    exit 1
fi

read_csproj_property() {
    local property=$1
    sed -nE "s:.*<${property}>([^<]+)</${property}>.*:\\1:p" "${csproj}"
}

for property in Version AssemblyVersion FileVersion; do
    values=$(read_csproj_property "${property}")
    count=$(printf '%s' "${values}" | grep -c . || true)
    if [[ ${count} -ne 1 ]]; then
        fail "expected exactly one <${property}> in ValheimOne.csproj, found ${count}"
        continue
    fi

    case ${property} in
        Version)
            [[ ${values} == "${plugin_version}" ]] ||
                fail "<Version> is ${values}, PluginVersion is ${plugin_version}"
            ;;
        *)
            # net472 assembly identities are 4-part; accept the bare version too.
            [[ ${values} == "${plugin_version}" || ${values} == "${plugin_version}.0" ]] ||
                fail "<${property}> is ${values}, expected ${plugin_version} or ${plugin_version}.0"
            ;;
    esac
done

# ------------ release gate

if [[ -n ${tag} ]]; then
    if [[ ${tag} != v* ]]; then
        fail "tag '${tag}' does not start with 'v'"
    elif [[ ${tag#v} != "${plugin_version}" ]]; then
        fail "tag '${tag}' does not match PluginVersion ${plugin_version} (expected v${plugin_version})"
    fi

    if [[ ! -f ${changelog} ]]; then
        fail "CHANGELOG.md is missing"
    elif ! grep -qE "^## \[${plugin_version//./\\.}\] - " "${changelog}"; then
        fail "CHANGELOG.md has no '## [${plugin_version}] - <date>' heading"
    fi
fi

if (( failures )); then
    printf 'VERSION ASSERT FAILED with %s problem(s).\n' "${failures}" >&2
    exit 1
fi

if [[ -n ${tag} ]]; then
    printf 'VERSION ASSERT PASS %s (tag %s)\n' "${plugin_version}" "${tag}"
else
    printf 'VERSION ASSERT PASS %s\n' "${plugin_version}"
fi

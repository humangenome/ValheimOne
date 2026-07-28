#!/usr/bin/env bash
# Asserts that a CONTRACT PASS was recorded locally for the version being released.
#
# `tools/contract-test.sh` boots a real Valheim dedicated server against the
# pinned smoke world, so a release runner is not always able to reproduce it.
# On every PASS the contract test writes `tools/contract-pass.txt` recording the
# version it passed at and the SHA-256 of the golden fingerprint it matched.
# This gate refuses to release a version that has no such record, or whose
# record was taken against a different golden fingerprint.
#
# Usage: tools/ci/assert-contract-pass.sh
#
# Prints `CONTRACT PASS RECORD OK <version>` on success.

set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
version_file="${repo_root}/src/ValheimOne/Networking/VersionInfo.cs"
golden="${repo_root}/tools/golden-fingerprint.txt"
record="${repo_root}/tools/contract-pass.txt"

fail() { printf 'CONTRACT PASS RECORD FAIL: %s\n' "$*" >&2; exit 1; }

plugin_version=$(sed -nE 's/.*public const string PluginVersion = "([^"]+)";.*/\1/p' "${version_file}" | head -n 1)
[[ -n ${plugin_version} ]] || fail "could not read PluginVersion from ${version_file}"

[[ -f ${golden} ]] || fail "missing golden fingerprint ${golden}"
[[ -f ${record} ]] || fail "no contract pass recorded; run tools/contract-test.sh locally and commit tools/contract-pass.txt"

read_key() {
    sed -nE "s/^$1=(.*)$/\\1/p" "${record}" | head -n 1
}

recorded_version=$(read_key version)
recorded_golden=$(read_key golden-sha256)
recorded_at=$(read_key recorded)
recorded_commit=$(read_key commit)

[[ -n ${recorded_version} ]] || fail "tools/contract-pass.txt has no version= line"
[[ -n ${recorded_golden} ]] || fail "tools/contract-pass.txt has no golden-sha256= line"

if [[ ${recorded_version} != "${plugin_version}" ]]; then
    fail "contract pass was recorded at ${recorded_version}, but this release is ${plugin_version}; re-run tools/contract-test.sh"
fi

golden_sha=$(sha256sum "${golden}" | cut -d' ' -f1)
if [[ ${recorded_golden} != "${golden_sha}" ]]; then
    fail "contract pass was recorded against golden fingerprint ${recorded_golden}, but tools/golden-fingerprint.txt is now ${golden_sha}; re-run tools/contract-test.sh"
fi

printf 'CONTRACT PASS RECORD OK %s (recorded %s, commit %s, golden %s)\n' \
    "${plugin_version}" "${recorded_at:-unknown}" "${recorded_commit:-unknown}" "${golden_sha:0:12}"

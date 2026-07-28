#!/usr/bin/env bash
# Fails the build if a shipped file carries a host/build-machine detail.
#
# ValheimOne is developed inside a private hosting codebase and ships as a
# public binary, so the interesting failure is a string that survived the
# compile: an internal panel path, a build-host home directory, a test-server
# address, a private repo URL. `strings -a` reads text files fine too, so the
# same pass covers the shipped config.
#
# The LiveMap web bundle is embedded compressed, so the sanctioned
# "Server hosting by SurvivalServers.com" footer never appears in the string
# table and does not need an exception here. If that ever changes, add a narrow
# allow for that exact anchor rather than widening the pattern.
#
# Usage: tools/ci/leak-gate.sh <file> [<file> ...]
#
# Prints `LEAK GATE PASS` on success.

set -euo pipefail

(( $# > 0 )) || { echo "leak-gate: no files given" >&2; exit 2; }

patterns=(
    'sspanel'
    'passrcon'
    'gameserverid'
    'b-cdn\.net'
    'bitbucket\.org'
    'survivalservers\.com/games/'
    'ryandev00'
    '/home/ryanpennington'
    'C:\\+sspanel'
    '72\.9\.145\.13'
)

pattern="$(IFS='|'; echo "${patterns[*]}")"

failures=0
for file in "$@"; do
    if [[ ! -f ${file} ]]; then
        echo "LEAK GATE FAIL: missing file ${file}" >&2
        failures=$(( failures + 1 ))
        continue
    fi

    hits="$(strings -a -- "${file}" | grep -inE "${pattern}" || true)"
    if [[ -n ${hits} ]]; then
        printf 'LEAK GATE FAIL: %s contains internal references:\n%s\n' "${file}" "${hits}" >&2
        failures=$(( failures + 1 ))
    else
        printf 'clean: %s\n' "${file}"
    fi
done

if (( failures )); then
    printf 'LEAK GATE FAILED on %s file(s).\n' "${failures}" >&2
    exit 1
fi

printf 'LEAK GATE PASS\n'

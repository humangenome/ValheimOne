#!/usr/bin/env bash
# Fails the build if a shipped file carries a host/build-machine detail.
#
# ValheimOne ships as a public binary, so the interesting failure is a string
# that survived the compile: a build-host home directory, a Windows profile path,
# or a named internal reference. `strings -a` reads text files fine too, so the
# same pass covers the shipped config.
#
# Two kinds of check:
#   - generic shapes, always: a Unix home directory and a Windows profile path;
#   - named terms, from the VALHEIMONE_LEAK_PATTERN environment variable (one
#     extended regular expression), or a file named in VALHEIMONE_LEAK_PATTERN_FILE.
#     The terms are not in this public file: listing them here would publish them.
#     The Release workflow passes them from a repository secret on tag pushes only;
#     the Build workflow and pull requests run the generic shapes alone.
#
# A hit prints the file and how many strings matched, never the matched text,
# because CI logs are public.
#
# Usage: tools/ci/leak-gate.sh [--require-named] <file> [<file> ...]
#   --require-named   fail when no named terms were supplied (the release run)
#
# Prints `LEAK GATE PASS` on success.

set -euo pipefail

require_named=0
if [[ ${1:-} == --require-named ]]; then require_named=1; shift; fi
(( $# > 0 )) || { echo "leak-gate: no files given" >&2; exit 2; }

generic='/home/[A-Za-z0-9._-]+/|[A-Za-z]:\\+Users\\+'

named="${VALHEIMONE_LEAK_PATTERN:-}"
if [[ -z ${named} && -n ${VALHEIMONE_LEAK_PATTERN_FILE:-} ]]; then
    named="$(cat -- "${VALHEIMONE_LEAK_PATTERN_FILE}")"
fi
named="$(printf '%s' "${named}" | tr -d '\r\n')"   # a stray newline would add an empty pattern that matches everything
if [[ -z ${named} ]]; then
    if (( require_named )); then
        echo "LEAK GATE FAIL: no named terms supplied (repository secret VALHEIMONE_LEAK_PATTERN); refusing to release unchecked files" >&2
        exit 1
    fi
    echo "leak-gate: named terms not supplied here; checking the generic shapes only (the release run checks the named terms)"
    pattern="${generic}"
else
    pattern="${generic}|${named}"
fi

failures=0
for file in "$@"; do
    if [[ ! -f ${file} ]]; then
        echo "LEAK GATE FAIL: missing file ${file}" >&2
        failures=$(( failures + 1 ))
        continue
    fi

    count="$(strings -a -- "${file}" | grep -ciE -e "${pattern}" || true)"
    if [[ ${count:-0} -gt 0 ]]; then
        printf 'LEAK GATE FAIL: %s has %s string(s) with an internal reference (text not shown)\n' "${file}" "${count}" >&2
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

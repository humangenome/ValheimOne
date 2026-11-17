#!/usr/bin/env bash
# Builds and packages the Thunderstore archive for the current ValheimOne version.
#
#   ValheimOne-<version>-thunderstore.zip
#       manifest.json, README.md, CHANGELOG.md, icon.png, LICENSE,
#       plugins/ValheimOne.dll
#
# The DLL is the Thunderstore build: the same sources as the GitHub release,
# compiled with -p:ValheimOneHostingCredit=false, which leaves the sidebar footer
# link out of the live map page. Nothing else differs. tools/ci/hosting-credit-gate.py
# decompresses the embedded pages and fails if the link is still there.
#
# The DLL and archive hashes are recorded per version in
# tools/release/thunderstore-provenance.tsv. The default run rebuilds and must
# reproduce them byte for byte (the Release workflow runs it); --record writes
# the row for a new version (RELEASING.md step 6).
#
# The page text comes from tools/release/thunderstore/README.md, never from the
# repository README. Thunderstore rejects packages whose listing text promotes
# an outside service, so the text gate below fails the package if the manifest,
# README or changelog names a hosting provider, a tracking link or a coupon.
#
# The changelog is the current minor line of CHANGELOG.md (every 0.13.x section
# for a 0.13.x release); the full history stays on GitHub.
#
# Usage: tools/package-thunderstore.sh [--record] [--skip-build]

set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
project="${repo_root}/src/ValheimOne/ValheimOne.csproj"
build_bin="${repo_root}/src/ValheimOne/bin/Thunderstore"
build_obj="${repo_root}/src/ValheimOne/obj/Thunderstore"
dll="${build_bin}/net472/ValheimOne.dll"
ledger="${repo_root}/tools/release/thunderstore-provenance.tsv"

fail() { echo "package-thunderstore: $*" >&2; exit 1; }

record=false
skip_build=false
while [[ $# -gt 0 ]]; do
    case "$1" in
        --record) record=true; shift ;;
        --skip-build) skip_build=true; shift ;;
        *) fail "unknown argument: $1" ;;
    esac
done

# BepInEx pack dependency. Keep it equal to the pin in tools/package-release.sh.
bepinex_dependency="denikson-BepInExPack_Valheim-5.4.2351"
release_pin="$(sed -n 's/^bepinex_pack_version="\([^"]*\)".*/\1/p' "${repo_root}/tools/package-release.sh")"
[[ "${bepinex_dependency}" == "denikson-BepInExPack_Valheim-${release_pin}" ]] \
    || fail "dependency ${bepinex_dependency} differs from the package-release.sh pin ${release_pin}"

# Fixed timestamp for zip determinism (same stamp as package-release.sh).
zip_touch_stamp="202001010000.00"

# ------------ version (single source of truth)

version_file="${repo_root}/src/ValheimOne/Networking/VersionInfo.cs"
version="$(sed -n 's/.*PluginVersion = "\([0-9][^"]*\)".*/\1/p' "${version_file}")"
[[ -n "${version}" ]] || fail "could not read PluginVersion from ${version_file}"
[[ "${version}" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail "Thunderstore needs a plain major.minor.patch version, got ${version}"
minor_line="${version%.*}"
echo "Packaging ValheimOne ${version} for Thunderstore"

if ${record}; then
    # Record only what is committed: the ledgers and the contract-pass record are
    # the only files allowed to differ from HEAD (they are committed together on
    # top of the content commit, RELEASING.md step 6).
    git -C "${repo_root}" diff --quiet HEAD -- . \
        ':(exclude)tools/release/provenance.tsv' \
        ':(exclude)tools/release/thunderstore-provenance.tsv' \
        ':(exclude)tools/contract-pass.txt' \
        || fail "tracked changes besides the ledgers; commit them before --record"
fi

# ------------ DLL: the Thunderstore build of the same sources

if ! ${skip_build}; then
    pinned_sdk="$(jq -er '.sdk.version' "${repo_root}/global.json")" \
        || fail "unable to read the pinned .NET SDK version from global.json"
    resolved_sdk="$(cd "${repo_root}" && dotnet --version 2>/dev/null)" || resolved_sdk="not found"
    [[ "${resolved_sdk}" == "${pinned_sdk}" ]] \
        || fail "ValheimOne requires .NET SDK ${pinned_sdk}, but dotnet resolved to ${resolved_sdk} (see build.sh)"
    rm -rf "${build_bin}" "${build_obj}"
    DOTNET_ROLL_FORWARD=LatestMajor dotnet build "${project}" -c Release -p:ValheimOneHostingCredit=false
fi
[[ -s "${dll}" ]] || fail "missing Thunderstore build ${dll}"

python3 "${repo_root}/tools/ci/hosting-credit-gate.py" "${dll}" --hosting-credit absent \
    || fail "the Thunderstore DLL still carries the map footer link"
"${repo_root}/tools/ci/leak-gate.sh" "${dll}"

# ------------ staging

src_dir="${repo_root}/tools/release/thunderstore"
out_dir="${repo_root}/artifacts/release"
mkdir -p "${out_dir}"
for f in README.md icon.png; do
    [[ -f "${src_dir}/${f}" ]] || fail "missing ${src_dir}/${f}"
done

stage="${out_dir}/.stage-thunderstore"
rm -rf "${stage}"
mkdir -p "${stage}/plugins"
trap 'rm -rf "${stage}"' EXIT

cp "${dll}" "${stage}/plugins/ValheimOne.dll"
dll_sha="$(sha256sum "${stage}/plugins/ValheimOne.dll" | cut -d' ' -f1)"

# ------------ icon: Thunderstore requires a 256x256 PNG

icon_dims="$(python3 - "${src_dir}/icon.png" <<'PY'
import struct, sys
data = open(sys.argv[1], "rb").read(24)
if data[:8] != b"\x89PNG\r\n\x1a\n":
    print("not-png")
else:
    print("%dx%d" % struct.unpack(">II", data[16:24]))
PY
)"
[[ "${icon_dims}" == "256x256" ]] || fail "icon.png must be a 256x256 PNG, got ${icon_dims}"
cp "${src_dir}/icon.png" "${stage}/icon.png"

# ------------ page text

cp "${src_dir}/README.md" "${stage}/README.md"
cp "${repo_root}/LICENSE" "${stage}/LICENSE"

# Changelog: every section of the current minor line, newest first.
awk -v line="${minor_line}" '
    /^## \[/ {
        keep = 0
        if (match($0, /^## \[[0-9]+\.[0-9]+\.[0-9]+\]/)) {
            ver = substr($0, 5, RLENGTH - 5)
            sub(/\.[0-9]+$/, "", ver)
            keep = (ver == line)
        }
    }
    keep { print }
' "${repo_root}/CHANGELOG.md" > "${stage}/CHANGELOG.body"
grep -q "^## \[${version}\]" "${stage}/CHANGELOG.body" || fail "CHANGELOG.md has no section for ${version}"
{
    echo "# Changelog"
    echo
    echo "Changes in the ${minor_line}.x line. The full history is in the [repository changelog](https://github.com/HumanGenome/ValheimOne/blob/main/CHANGELOG.md)."
    echo
    cat "${stage}/CHANGELOG.body"
} > "${stage}/CHANGELOG.md"
rm -f "${stage}/CHANGELOG.body"

# ------------ manifest

python3 - "${stage}/manifest.json" "${version}" "${bepinex_dependency}" <<'PY'
import json, sys
path, version, dependency = sys.argv[1:4]
manifest = {
    "name": "ValheimOne",
    "version_number": version,
    "website_url": "https://github.com/HumanGenome/ValheimOne",
    "description": "Adds a live web map, a browser admin console, item and recipe search, Discord alerts and up to 127 players to a Valheim dedicated server. Players join with the normal game.",
    "dependencies": [dependency],
}
if len(manifest["description"]) > 250:
    raise SystemExit("manifest description exceeds Thunderstore's 250 characters")
with open(path, "w", encoding="utf-8", newline="\n") as handle:
    json.dump(manifest, handle, indent=2)
    handle.write("\n")
PY

# ------------ text gate: nothing on the Thunderstore page promotes a service

gate_pattern='survivalservers|utm_|official hosting|recommended host|server hosting by|coupon|promo code|% off|\baffiliates?\b'
if grep -n -i -E "${gate_pattern}" "${stage}/manifest.json" "${stage}/README.md" "${stage}/CHANGELOG.md"; then
    fail "promotional text in the Thunderstore page files (lines above); Thunderstore rejects listings that advertise an outside service"
fi

# ------------ zip (deterministic, same recipe as package-release.sh)

zip_path="${out_dir}/ValheimOne-${version}-thunderstore.zip"
find "${stage}" -type d -exec chmod 0755 {} +
find "${stage}" -type f -exec chmod 0644 {} +
find "${stage}" -exec env TZ=UTC touch -t "${zip_touch_stamp}" {} +
new_zip="${out_dir}/.ValheimOne-${version}-thunderstore.zip.new"
rm -f "${new_zip}"
(cd "${stage}" && find . -type f | sed 's|^\./||' | LC_ALL=C sort | TZ=UTC zip -q -X "${new_zip}" -@)
zip_sha="$(sha256sum "${new_zip}" | cut -d' ' -f1)"

# Thunderstore versions are immutable. Never replace an archive that may already
# have been uploaded under this version with different bytes.
if [[ -f "${zip_path}" ]]; then
    if cmp -s "${zip_path}" "${new_zip}"; then
        rm -f "${new_zip}"
    else
        rm -f "${new_zip}"
        fail "${zip_path} already exists with different contents; a changed Thunderstore package needs a new version"
    fi
else
    mv "${new_zip}" "${zip_path}"
fi

# ------------ ledger: record a new version, or reproduce the recorded one

[[ -f "${ledger}" ]] || fail "missing ledger ${ledger}"
rows="$(awk -F'\t' -v v="${version}" '$1 == v { n++ } END { print n + 0 }' "${ledger}")"
(( rows <= 1 )) || fail "${ledger} has ${rows} rows for ${version}"
recorded="$(awk -F'\t' -v v="${version}" '$1 == v { print; exit }' "${ledger}")"

if ${record}; then
    commit="$(git -C "${repo_root}" rev-parse --verify HEAD)"
    row="$(printf '%s\t%s\t%s\t%s' "${version}" "${commit}" "${dll_sha}" "${zip_sha}")"
    if [[ -n "${recorded}" ]]; then
        IFS=$'\t' read -r _ _ old_dll old_zip <<< "${recorded}"
        [[ "${old_dll}" == "${dll_sha}" && "${old_zip}" == "${zip_sha}" ]] \
            || fail "${version} is already recorded with different hashes; a changed Thunderstore package needs a new version"
        echo "Already recorded: ${recorded}"
    else
        tmp_ledger="$(mktemp "${ledger}.tmp.XXXXXX")"
        cat "${ledger}" > "${tmp_ledger}"
        printf '%s\n' "${row}" >> "${tmp_ledger}"
        chmod 0644 "${tmp_ledger}"
        mv "${tmp_ledger}" "${ledger}"
        echo "Recorded: ${row}"
    fi
else
    [[ -n "${recorded}" ]] || fail "no row for ${version} in ${ledger}; run with --record at the content commit first"
    IFS=$'\t' read -r _ recorded_commit expected_dll expected_zip <<< "${recorded}"
    [[ "${expected_dll}" == "${dll_sha}" ]] \
        || fail "Thunderstore DLL ${dll_sha} differs from the recorded ${expected_dll}"
    [[ "${expected_zip}" == "${zip_sha}" ]] \
        || fail "Thunderstore archive ${zip_sha} differs from the recorded ${expected_zip}"
    echo "Reproduced the ${version} Thunderstore build recorded at ${recorded_commit}"
fi

echo
echo "Thunderstore artifact:"
ls -la "${zip_path}"
sha256sum "${zip_path}"
echo "DLL sha256 ${dll_sha} (Thunderstore build, no map footer link)"

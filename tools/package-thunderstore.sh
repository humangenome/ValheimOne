#!/usr/bin/env bash
# Packages the Thunderstore archive for an already packaged ValheimOne release.
#
#   ValheimOne-<version>-thunderstore.zip
#       manifest.json, README.md, CHANGELOG.md, icon.png, LICENSE,
#       plugins/ValheimOne.dll
#
# The DLL is taken from artifacts/release/ValheimOne-<version>.zip and checked
# against tools/release/provenance.tsv, so Thunderstore always carries the same
# bytes as the GitHub release. Nothing is compiled here.
#
# The page text comes from tools/release/thunderstore/README.md, never from the
# repository README. Thunderstore rejects packages whose listing text promotes
# an outside service, so the text gate below fails the package if the manifest,
# README or changelog names a hosting provider, a tracking link or a coupon.
#
# The changelog is the current minor line of CHANGELOG.md (every 0.13.x section
# for a 0.13.x release); the full history stays on GitHub.
#
# Usage: tools/package-thunderstore.sh [--plugin-zip <path>]

set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"

fail() { echo "package-thunderstore: $*" >&2; exit 1; }

plugin_zip=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --plugin-zip) plugin_zip="${2:-}"; shift 2 ;;
        *) fail "unknown argument: $1" ;;
    esac
done

# BepInEx pack dependency. Keep it equal to the pin in tools/package-release.sh.
bepinex_dependency="denikson-BepInExPack_Valheim-5.4.2333"
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

# ------------ inputs

src_dir="${repo_root}/tools/release/thunderstore"
out_dir="${repo_root}/artifacts/release"
[[ -n "${plugin_zip}" ]] || plugin_zip="${out_dir}/ValheimOne-${version}.zip"
[[ -f "${plugin_zip}" ]] || fail "missing plugin zip ${plugin_zip} (run tools/package-release.sh, or pass --plugin-zip with the published asset)"
for f in README.md icon.png; do
    [[ -f "${src_dir}/${f}" ]] || fail "missing ${src_dir}/${f}"
done

stage="${out_dir}/.stage-thunderstore"
rm -rf "${stage}"
mkdir -p "${stage}/plugins"
trap 'rm -rf "${stage}"' EXIT

# ------------ DLL: the released bytes, checked against the provenance ledger

unzip -q -j "${plugin_zip}" "BepInEx/plugins/ValheimOne.dll" -d "${stage}/plugins" \
    || fail "${plugin_zip} has no BepInEx/plugins/ValheimOne.dll"
dll_sha="$(sha256sum "${stage}/plugins/ValheimOne.dll" | cut -d' ' -f1)"
ledger_sha="$(awk -F'\t' -v v="${version}" '$1 == v { print $6 }' "${repo_root}/tools/release/provenance.tsv")"
[[ -n "${ledger_sha}" ]] || fail "no provenance row for ${version}; record provenance first (RELEASING.md step 6)"
[[ "${dll_sha}" == "${ledger_sha}" ]] || fail "DLL ${dll_sha} differs from the provenance ledger ${ledger_sha}"

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
    "description": "Server plugin: live browser map, dungeon viewer, item codex, admin console, Discord alerts and opt-in gameplay modules. Map and console work with vanilla clients.",
    "dependencies": [dependency],
}
if len(manifest["description"]) > 250:
    raise SystemExit("manifest description exceeds Thunderstore's 250 characters")
with open(path, "w", encoding="utf-8", newline="\n") as handle:
    json.dump(manifest, handle, indent=2)
    handle.write("\n")
PY

# ------------ text gate: nothing on the Thunderstore page promotes a service

gate_pattern='survivalservers|utm_|official hosting|recommended host|server hosting by|coupon|promo code|% off|affiliate'
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

echo
echo "Thunderstore artifact:"
ls -la "${zip_path}"
sha256sum "${zip_path}"
echo "DLL sha256 ${dll_sha} (matches provenance ${version})"

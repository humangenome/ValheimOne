#!/usr/bin/env bash
# Packages ValheimOne release zips from a clean build.
#
#   ValheimOne-<version>.zip       plugin-only: drop onto an existing BepInEx server
#   ValheimOne-full-<version>.zip  plugin + denikson BepInExPack_Valheim overlay for
#                                  from-scratch installs (Windows and Linux servers)
#
# Version is read from src/ValheimOne/Networking/VersionInfo.cs (the single
# source of truth). Outputs land in artifacts/release/. Zips are deterministic
# for a given build: fixed file timestamps, sorted entries, no extra fields.
#
# Usage: tools/package-release.sh [--skip-build]

set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
skip_build=false
[[ "${1:-}" == "--skip-build" ]] && skip_build=true

# BepInEx pack pin (denikson/BepInExPack_Valheim on Thunderstore).
bepinex_pack_version="5.4.2333"
bepinex_pack_sha256="5dd24ccbcaa9260f714b200f23c4c15547e2aa5f06906cafcc0dee56db1bf716"
bepinex_pack_url="https://thunderstore.io/package/download/denikson/BepInExPack_Valheim/${bepinex_pack_version}/"
bepinex_pack_cache="${repo_root}/tools/cache/BepInExPack_Valheim-${bepinex_pack_version}.zip"

# Fixed timestamp for zip determinism.
zip_touch_stamp="202001010000.00"

fail() { echo "package-release: $*" >&2; exit 1; }

# ------------ version (single source of truth)

version_file="${repo_root}/src/ValheimOne/Networking/VersionInfo.cs"
version="$(sed -n 's/.*PluginVersion = "\([0-9][^"]*\)".*/\1/p' "${version_file}")"
[[ -n "${version}" ]] || fail "could not read PluginVersion from ${version_file}"
echo "Packaging ValheimOne ${version}"

# ------------ clean build

dll="${repo_root}/src/ValheimOne/bin/Release/net472/ValheimOne.dll"
if ! ${skip_build}; then
    rm -rf "${repo_root}/src/ValheimOne/bin" "${repo_root}/src/ValheimOne/obj"
    "${repo_root}/build.sh"
fi
[[ -f "${dll}" ]] || fail "missing build output ${dll}"

# ------------ reference config sanity

ref_cfg="${repo_root}/tools/release/valheimone.cfg"
[[ -f "${ref_cfg}" ]] || fail "missing reference config ${ref_cfg} (regenerate it; see RELEASING.md)"

# Pristine defaults: Enabled = true is allowed only in [Server] and [ActivityLog].
bad_enabled="$(awk '
    /^\[/ { section = $0 }
    /^Enabled = true/ && section != "[Server]" && section != "[ActivityLog]" { print section }
' "${ref_cfg}")"
[[ -z "${bad_enabled}" ]] || fail "reference config has non-default Enabled = true in: ${bad_enabled//$'\n'/ }"
grep -q '^\[Server\]' "${ref_cfg}" || fail "reference config is missing the [Server] section"

# Guard against a stale reference config after new modules ship: every feature
# section registered in source should exist in the reference config.
missing_sections="$(
    grep -rhoP 'Section\s*=>\s*"\K[^"]+' \
        "${repo_root}/src/ValheimOne" --include='*.cs' | sort -u |
    while read -r section; do
        grep -q "^\[${section}\]" "${ref_cfg}" || echo "[${section}]"
    done
)"
[[ -z "${missing_sections}" ]] || fail "reference config is stale; missing sections: ${missing_sections//$'\n'/ } (regenerate; see RELEASING.md)"

# ------------ staging helpers

out_dir="${repo_root}/artifacts/release"
stage_root="${out_dir}/.stage"
rm -rf "${stage_root}"
mkdir -p "${out_dir}" "${stage_root}"

make_zip() {
    local stage_dir="$1" zip_path="$2"
    rm -f "${zip_path}"
    find "${stage_dir}" -exec touch -t "${zip_touch_stamp}" {} +
    (cd "${stage_dir}" && find . -type f | sed 's|^\./||' | LC_ALL=C sort | TZ=UTC zip -q -X "${zip_path}" -@)
}

overlay_plugin() {
    local stage_dir="$1"
    mkdir -p "${stage_dir}/BepInEx/plugins" "${stage_dir}/BepInEx/config"
    cp "${dll}" "${stage_dir}/BepInEx/plugins/ValheimOne.dll"
    cp "${ref_cfg}" "${stage_dir}/BepInEx/config/valheimone.cfg"
}

# ------------ plugin-only zip

plugin_stage="${stage_root}/plugin"
mkdir -p "${plugin_stage}"
overlay_plugin "${plugin_stage}"
plugin_zip="${out_dir}/ValheimOne-${version}.zip"
make_zip "${plugin_stage}" "${plugin_zip}"

# ------------ full zip (BepInExPack overlay)

if [[ ! -f "${bepinex_pack_cache}" ]]; then
    mkdir -p "$(dirname "${bepinex_pack_cache}")"
    echo "Fetching BepInExPack_Valheim ${bepinex_pack_version}"
    curl -fsSL -o "${bepinex_pack_cache}.tmp" "${bepinex_pack_url}"
    mv "${bepinex_pack_cache}.tmp" "${bepinex_pack_cache}"
fi
echo "${bepinex_pack_sha256}  ${bepinex_pack_cache}" | sha256sum -c --quiet - \
    || fail "BepInExPack zip failed sha256 verification"

full_stage="${stage_root}/full"
pack_extract="${stage_root}/pack"
mkdir -p "${full_stage}" "${pack_extract}"
unzip -q "${bepinex_pack_cache}" -d "${pack_extract}"
[[ -d "${pack_extract}/BepInExPack_Valheim" ]] || fail "unexpected BepInExPack zip layout"
# Ship the pack exactly as denikson does: the contents of BepInExPack_Valheim/
# go to the zip root, so the archive extracts straight into the server directory.
cp -a "${pack_extract}/BepInExPack_Valheim/." "${full_stage}/"
chmod +x "${full_stage}"/start_*_bepinex.sh 2>/dev/null || true
overlay_plugin "${full_stage}"
full_zip="${out_dir}/ValheimOne-full-${version}.zip"
make_zip "${full_stage}" "${full_zip}"

# ------------ summary

rm -rf "${stage_root}"
(cd "${out_dir}" && sha256sum "$(basename "${plugin_zip}")" "$(basename "${full_zip}")" > "SHA256SUMS-${version}.txt")
echo
echo "Release artifacts:"
ls -la "${plugin_zip}" "${full_zip}" "${out_dir}/SHA256SUMS-${version}.txt"

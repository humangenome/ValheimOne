#!/usr/bin/env bash
set -euo pipefail
repo=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
modding=${VALHEIM_MODDING_DIR:-"$HOME/valheim-modding"}
server="$modding/testserver"
[[ -x "$server/valheim_server.x86_64" ]] || { echo 'Native Linux testserver is required.' >&2; exit 1; }
exec 9>"$server/.runtime-regression.lock"
flock -n 9 || { echo 'Runtime regression already running.' >&2; exit 1; }
# Compiler servers can outlive this script; they must not inherit the test lock.
dotnet build "$repo/tools/RuntimeRegression/RuntimeRegression.csproj" -c Release 9>&-
backup=$(mktemp -d)
plugins="$server/BepInEx/plugins"
config="$server/BepInEx/config"
output="$repo/artifacts/runtime-regression"
browser_hold=${VALHEIMONE_BROWSER_HOLD_SECONDS:-0}
[[ $browser_hold =~ ^[0-9]+$ && $browser_hold -le 600 ]] || { echo 'Browser hold must be 0–600 seconds.' >&2; exit 1; }
export VALHEIMONE_BROWSER_HOLD_SECONDS="$browser_hold"
console_result="$backup/console-result.txt"
export CONSOLE_PROBE_TOKEN
CONSOLE_PROBE_TOKEN=$(python3 -c 'import secrets; print(secrets.token_hex(32))')
# 0.13.27: every run sets a relay key, so every live check also proves the machine's own
# (loopback) calls are served without it. VALHEIMONE_RELAY_KEY= (empty) runs without one.
export VALHEIMONE_RELAY_KEY
VALHEIMONE_RELAY_KEY=${VALHEIMONE_RELAY_KEY-$(python3 -c 'import secrets; print(secrets.token_hex(16))')}
# VALHEIMONE_RELAY_PROBE=1 also binds every address and checks an outside-address call is refused
# without the key and served with it.
relay_probe=${VALHEIMONE_RELAY_PROBE:-0}
mkdir -p "$plugins" "$config" "$output" "$backup/worlds/worlds_local"
pid=
cleanup() {
    if [[ -n $pid ]]; then
        kill -INT -- "-$pid" 2>/dev/null || true
        for _ in {1..30}; do kill -0 -- "-$pid" 2>/dev/null || break; sleep 1; done
        kill -TERM -- "-$pid" 2>/dev/null || true
        for _ in {1..10}; do kill -0 -- "-$pid" 2>/dev/null || break; sleep 1; done
        kill -KILL -- "-$pid" 2>/dev/null || true
        wait "$pid" 2>/dev/null || true
        pid=
    fi
    for name in ValheimOne.dll ValheimOne.RuntimeRegression.dll; do
        rm -f "$plugins/$name"
        [[ ! -f "$backup/$name" ]] || mv "$backup/$name" "$plugins/$name"
    done
    rm -f "$config/valheimone.cfg"
    [[ ! -f "$backup/valheimone.cfg" ]] || mv "$backup/valheimone.cfg" "$config/valheimone.cfg"
    [[ ! -d "$config/runtime-regression" ]] || cp -a "$config/runtime-regression" "$output/data-$(date -u +%Y%m%dT%H%M%S)"
    rm -rf "$config/runtime-regression"
    [[ ! -d "$backup/runtime-regression" ]] || mv "$backup/runtime-regression" "$config/runtime-regression"
    rm -rf "$backup"
}
for name in ValheimOne.dll ValheimOne.RuntimeRegression.dll; do
    [[ ! -f "$plugins/$name" ]] || cp -p "$plugins/$name" "$backup/$name"
done
[[ ! -f "$config/valheimone.cfg" ]] || cp -p "$config/valheimone.cfg" "$backup/valheimone.cfg"
[[ ! -d "$config/runtime-regression" ]] || mv "$config/runtime-regression" "$backup/runtime-regression"
trap cleanup EXIT
# VALHEIMONE_PLUGIN_DLL tests another build of the plugin, such as the Thunderstore build.
plugin_dll=${VALHEIMONE_PLUGIN_DLL:-"$repo/src/ValheimOne/bin/Release/net472/ValheimOne.dll"}
cp "$plugin_dll" "$plugins/ValheimOne.dll"
if [[ -n ${VALHEIMONE_PLUGIN_ZIP:-} ]]; then
    unzip -p "$VALHEIMONE_PLUGIN_ZIP" BepInEx/plugins/ValheimOne.dll > "$plugins/ValheimOne.dll"
    cmp "$plugins/ValheimOne.dll" "$plugin_dll"
fi
cp "$repo/tools/RuntimeRegression/bin/Release/net472/ValheimOne.RuntimeRegression.dll" "$plugins/"
python3 - "$repo/tools/release/valheimone.cfg" "$config/valheimone.cfg" <<'PY'
import configparser, os, sys
cfg=configparser.ConfigParser(interpolation=None, strict=False)
cfg.optionxform=str
cfg.read(sys.argv[1])
cfg['CraftFromChest']['Enabled']='true'
cfg['MapSharing']['Enabled']='true'
cfg['MapSharing']['SharedExploration']='true'
cfg['StationAutomation'].update({'Enabled':'true','SmelterAutoFuel':'true','FireplaceAutoFuel':'true'})
cfg['CookingStation'].update({'Enabled':'true','AutoFuel':'true','AutoFeedRaw':'true','IgnoreFireRequirement':'true'})
cfg['ProductionSpeeds'].update({'Enabled':'true','SmelterProductionSeconds':'5','SmelterMaxQueue':'12','SmelterMaxFuel':'12'})
cfg['LiveMap'].update({'Enabled':'true', 'ConsoleEnabled':'true', 'BindIp':'127.0.0.1',
    'Port':'24583', 'TextureSize':'512', 'AccessToken':os.environ['CONSOLE_PROBE_TOKEN'],
    'PublicView':'false', 'StatusPublic':'false', 'RelayKey':os.environ['VALHEIMONE_RELAY_KEY']})
if int(os.environ['VALHEIMONE_BROWSER_HOLD_SECONDS']) > 0:
    cfg['LiveMap'].update({'PublicView':'true', 'FogMode':'trails', 'FogHideUnexplored':'true',
        'PublicWebPins':'true', 'SharedPinEditing':'true',
        'ShareToken':'browser-regression-shared',
        'BindIp':os.environ.get('VALHEIMONE_BROWSER_BIND', '127.0.0.1')})
if os.environ.get('VALHEIMONE_RELAY_PROBE') == '1':
    cfg['LiveMap'].update({'BindIp':''})
with open(sys.argv[2], 'w') as out: cfg.write(out)
PY
cp "$repo/tools/fixtures/SmokeWorld.fwl" "$repo/tools/fixtures/SmokeWorld.db" "$backup/worlds/worlds_local/"
cd "$server"
rm -f "$server/BepInEx/LogOutput.log"
VALHEIMONE_RUNTIME_REGRESSION=1 VALHEIMONE_CONSOLE_PROBE=1 CONSOLE_PROBE_SETTLE_SECONDS=0 \
CONSOLE_PROBE_URL=http://127.0.0.1:24583 CONSOLE_PROBE_RESULT="$console_result" SteamAppId=892970 setsid bash -c '
    export DOORSTOP_ENABLED=1
    export DOORSTOP_TARGET_ASSEMBLY=./BepInEx/core/BepInEx.Preloader.dll
    export LD_LIBRARY_PATH="./doorstop_libs:./linux64:${LD_LIBRARY_PATH:-}"
    export LD_PRELOAD=libdoorstop_x64.so
    exec "$@"
' regression-doorstop "$server/valheim_server.x86_64" -name regression -port 24580 \
    -world SmokeWorld -password regression1 -savedir "$backup/worlds" -nographics -batchmode -public 0 \
    > "$output/server.log" 2>&1 9>&- &
pid=$!
deadline=$((SECONDS + 240))
while (( SECONDS < deadline )); do
    if [[ -f "$config/runtime-regression/result.txt" && -f "$console_result" ]]; then break; fi
    if [[ -f "$server/BepInEx/LogOutput.log" ]] &&
        grep -qE 'Failed to patch|Feature patch application failed' "$server/BepInEx/LogOutput.log"; then break; fi
    kill -0 "$pid" 2>/dev/null || break
    sleep 1
done
status=1
if [[ -f "$config/runtime-regression/result.txt" ]]; then
    cp "$config/runtime-regression/result.txt" "$output/result.txt"
    cat "$output/result.txt"
    if grep -q '^RUNTIME REGRESSION PASS ' "$output/result.txt"; then status=0; fi
else
    echo 'Runtime regression did not complete.' >&2
    tail -n 50 "$output/server.log" >&2
fi
if [[ -f "$console_result" ]]; then
    cp "$console_result" "$output/console-result.txt"
    cat "$output/console-result.txt"
    grep -q '^CONSOLE PROBE PASS$' "$output/console-result.txt" || status=1
else
    echo 'HTTP console save probe did not complete.' >&2
    status=1
fi
if [[ $status == 0 && $relay_probe == 1 ]]; then
    "$repo/tools/relay-probe.sh" "$output/relay-probe.txt" || status=1
    cat "$output/relay-probe.txt"
fi
if [[ $status == 0 && $browser_hold -gt 0 ]]; then
    rm -f "$output/browser-done"
    echo 'Native browser fixture ready on port 24583.'
    touch "$output/browser-ready"
    deadline=$((SECONDS + browser_hold))
    while (( SECONDS < deadline )) && [[ ! -f "$output/browser-done" ]]; do sleep 1; done
    rm -f "$output/browser-ready" "$output/browser-done"
fi
cleanup
trap - EXIT
cp "$server/BepInEx/LogOutput.log" "$output/BepInEx.log"
if [[ -n $VALHEIMONE_RELAY_KEY ]] && grep -qF "$VALHEIMONE_RELAY_KEY" "$output/BepInEx.log" "$output/server.log"; then
    echo 'The relay key appears in a log.' >&2
    status=1
fi
if grep -qE 'Failed to patch|Feature patch application failed|RUNTIME REGRESSION FAIL|FieldAccessException|MissingFieldException|NullReferenceException' "$output/BepInEx.log" "$output/server.log"; then
    echo 'Runtime log contains an unhandled failure.' >&2
    grep -nE -A8 'Failed to patch|Feature patch application failed|RUNTIME REGRESSION FAIL|FieldAccessException|MissingFieldException|NullReferenceException' "$output/BepInEx.log" "$output/server.log" | head -n 100 >&2 || true
    status=1
fi
exit "$status"

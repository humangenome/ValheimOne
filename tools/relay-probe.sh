#!/usr/bin/env bash
# Called by runtime-regression.sh with VALHEIMONE_RELAY_PROBE=1 (the map then listens on every
# address). Proves the 0.13.27 relay key on the running map: a call to this machine's outside
# address is refused without the key or with a wrong one, served with it, and a loopback call is
# served without it. Never prints the key.
set -uo pipefail
out=$1
key=${VALHEIMONE_RELAY_KEY:?}
token=${CONSOLE_PROBE_TOKEN:?}
port=24583
addr=$(hostname -I 2>/dev/null | tr ' ' '\n' | grep -E '^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$' | grep -v '^127\.' | head -n1)
: > "$out"
fail=0
code() { curl -s -o /dev/null -w '%{http_code}' --max-time 5 "$@"; }
expect() {
    local want=$1 name=$2; shift 2
    local got
    got=$(code "$@")
    if [[ $got == "$want" ]]; then echo "ok   $name (HTTP $got)" >> "$out"; else echo "FAIL $name (HTTP $got, wanted $want)" >> "$out"; fail=1; fi
}
if [[ -z $addr ]]; then echo 'FAIL no outside address on this machine' >> "$out"; exit 1; fi
outside="http://$addr:$port"
expect 403 'outside address, no key: status' -H "X-LiveMap-Token: $token" "$outside/api/status"
expect 403 'outside address, no key: map page' "$outside/"
expect 403 'outside address, no key: asset' "$outside/assets/app.js"
expect 403 'outside address, no key: catalog' "$outside/api/catalog"
expect 403 'outside address, no key: console' -H "X-LiveMap-Token: $token" "$outside/api/admin/log"
expect 403 'outside address, wrong key' -H "X-LiveMap-Token: $token" -H "X-Relay-Key: ${key%?}x" "$outside/api/status"
expect 403 'outside address, key in the query string (header only)' -H "X-LiveMap-Token: $token" "$outside/api/status?relay=$key&key=$key"
expect 200 'outside address, right key: status' -H "X-LiveMap-Token: $token" -H "X-Relay-Key: $key" "$outside/api/status"
expect 200 'outside address, right key: catalog' -H "X-Relay-Key: $key" "$outside/api/catalog"
expect 401 'outside address, right key, no map token: still needs the token' -H "X-Relay-Key: $key" "$outside/api/status"
expect 200 'loopback, no key: status' -H "X-LiveMap-Token: $token" "http://127.0.0.1:$port/api/status"
body=$(curl -s --max-time 5 "$outside/api/status")
if [[ $body == '{"error":"forbidden"}' ]]; then echo 'ok   a refusal says only forbidden' >> "$out"; else echo "FAIL refusal body: ${body:0:80}" >> "$out"; fail=1; fi
if grep -qF "$key" "$out"; then echo 'FAIL the key reached the probe output' > "$out"; fail=1; fi
if (( fail )); then echo 'RELAY PROBE FAIL' >> "$out"; exit 1; fi
echo 'RELAY PROBE PASS' >> "$out"

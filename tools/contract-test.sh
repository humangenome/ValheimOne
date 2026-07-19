#!/usr/bin/env bash
set -euo pipefail

usage() {
    printf 'Usage: %s [--bless]\n' "${0##*/}" >&2
}

bless=0
while (( $# > 0 )); do
    case $1 in
        --bless)
            bless=1
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            usage
            exit 2
            ;;
    esac
done

repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
modding_dir=${VALHEIM_MODDING_DIR:-"${HOME}/valheim-modding"}
testserver="${modding_dir}/testserver"
log_dir="${modding_dir}/harness/logs"
server_bin="${testserver}/valheim_server.x86_64"
plugin_dir="${testserver}/BepInEx/plugins"
bepinex_log="${testserver}/BepInEx/LogOutput.log"
world_dir="${testserver}/worlds/worlds_local"
world_name=SmokeWorld
world_fwl="${world_dir}/${world_name}.fwl"
world_db="${world_dir}/${world_name}.db"
fixture_fwl="${repo_root}/tools/fixtures/${world_name}.fwl"
fixture_db="${repo_root}/tools/fixtures/${world_name}.db"
candidate="${repo_root}/tools/contract-fingerprint.txt"
golden="${repo_root}/tools/golden-fingerprint.txt"
output_dll="${repo_root}/src/ValheimOne/bin/Release/net472/ValheimOne.dll"
harmony_exception_pattern='Harmony(Lib|X|Exception)?.*(patch(ing)? exception|exception.*patch|failed to patch|patching failed)|(patch(ing)? exception|failed to patch|patching failed).*Harmony(Lib|X|Exception)?'
unity_log_error_pattern='\[(Error|Fatal)[[:space:]]*:[[:space:]]*Unity Log[[:space:]]*\]'
missing_db_error='Failed to load world with name "SmokeWorld", data error MissingDB'
baseline_allowlist=(
    "Can't return the graphics config when it's not loaded!"
    'DllNotFoundException: party assembly:'
    'ArgumentNullException: Value cannot be null.'
)

[[ -x $server_bin ]] || {
    printf 'Missing executable server: %s\n' "$server_bin" >&2
    exit 1
}
[[ -f $fixture_fwl ]] || {
    printf 'Missing pinned world fixture: %s\n' "$fixture_fwl" >&2
    exit 1
}
[[ -f $fixture_db ]] || {
    printf 'Missing pinned world fixture: %s\n' "$fixture_db" >&2
    exit 1
}
[[ -d $world_dir ]] || {
    printf 'Missing testserver world directory: %s\n' "$world_dir" >&2
    exit 1
}

"${repo_root}/build.sh"
mkdir -p -- "$plugin_dir"
cp -f -- "$output_dll" "${plugin_dir}/ValheimOne.dll"

backup_dir=$(mktemp -d "${TMPDIR:-/tmp}/valheimone-contract.XXXXXX")
backup_fwl="${backup_dir}/${world_name}.fwl"
backup_db="${backup_dir}/${world_name}.db"
had_fwl=0
had_db=0
world_replaced=0
server_pid=
server_pgid=

group_is_alive() {
    [[ -n $server_pgid ]] && kill -0 -- "-$server_pgid" 2>/dev/null
}

wait_for_group() {
    local timeout=$1
    local elapsed=0
    while group_is_alive && (( elapsed < timeout )); do
        sleep 1
        (( elapsed += 1 ))
    done
    ! group_is_alive
}

stop_server() {
    if group_is_alive; then
        kill -INT -- "-$server_pgid" 2>/dev/null || true
        wait_for_group 30 || true
    fi
    if group_is_alive; then
        kill -TERM -- "-$server_pgid" 2>/dev/null || true
        wait_for_group 10 || true
    fi
    if group_is_alive; then
        kill -KILL -- "-$server_pgid" 2>/dev/null || true
        wait_for_group 2 || true
    fi
}

append_line() {
    local variable_name=$1
    local line=$2
    if [[ -n ${!variable_name} ]]; then
        printf -v "$variable_name" '%s\n%s' "${!variable_name}" "$line"
    else
        printf -v "$variable_name" '%s' "$line"
    fi
}

classify_bepinex_errors() {
    local line baseline
    local allowlisted

    warning_evidence=
    warning_count=0
    ignored_baseline_evidence=
    ignored_baseline_count=0
    missing_db_count=0

    while IFS= read -r line; do
        if [[ $line == *"$missing_db_error"* ]]; then
            (( missing_db_count += 1 ))
            continue
        fi

        allowlisted=0
        if [[ $line =~ $unity_log_error_pattern ]]; then
            for baseline in "${baseline_allowlist[@]}"; do
                if [[ $line == *"$baseline"* ]]; then
                    allowlisted=1
                    break
                fi
            done

            if grep -Eqi 'Harmony|BepInEx' <<< "$line" ||
                grep -Fqi -- 'ValheimOne' <<< "$line"; then
                allowlisted=0
            fi
        fi

        if (( allowlisted )); then
            append_line ignored_baseline_evidence "$line"
            (( ignored_baseline_count += 1 ))
        elif ! grep -Eqi "$harmony_exception_pattern" <<< "$line"; then
            append_line warning_evidence "$line"
            (( warning_count += 1 ))
        fi
    done < <(grep -n -E '\[(Error|Fatal)' "$bepinex_log" || true)
}

cleanup() {
    local status=$?
    local restore_status=0
    local server_pattern=${server_bin//./\\.}
    set +e
    trap - EXIT INT TERM

    stop_server
    if [[ -n $server_pid ]]; then
        pkill -f -- "^${server_pattern}([[:space:]]|$)" 2>/dev/null || true
        wait "$server_pid" 2>/dev/null || true
    fi

    if (( world_replaced )); then
        rm -f -- "$world_fwl" "$world_db"
        if (( had_fwl )); then
            cp -a -- "$backup_fwl" "$world_fwl" || restore_status=1
        fi
        if (( had_db )); then
            cp -a -- "$backup_db" "$world_db" || restore_status=1
        fi
    fi

    rm -f -- "$backup_fwl" "$backup_db"
    rmdir -- "$backup_dir" 2>/dev/null || true

    if (( restore_status )); then
        printf 'ERROR: failed to restore the original SmokeWorld files.\n' >&2
        status=1
    fi
    exit "$status"
}

trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

if [[ -e $world_fwl ]]; then
    cp -a -- "$world_fwl" "$backup_fwl"
    had_fwl=1
fi
if [[ -e $world_db ]]; then
    cp -a -- "$world_db" "$backup_db"
    had_db=1
fi
world_replaced=1
cp -a -- "$fixture_fwl" "$world_fwl"
cp -a -- "$fixture_db" "$world_db"
rm -f -- "$candidate"

printf 'Starting contract server; waiting up to 240 seconds for diagnostics.\n'
mkdir -p -- "$log_dir"
epoch=$(date +%s)
server_log="${log_dir}/server-${epoch}.log"
while [[ -e $server_log ]]; do
    (( epoch += 1 ))
    server_log="${log_dir}/server-${epoch}.log"
done
: > "$server_log"
ln -sfn "${server_log##*/}" "${log_dir}/server-latest.log"

cd "$testserver"
export SteamAppId=892970
export LD_LIBRARY_PATH="./linux64:${LD_LIBRARY_PATH:-}"
rm -f -- "$bepinex_log"

server_args=(
    -name "harness-smoke"
    -port 24560
    -world "$world_name"
    -password "smokepass1"
    -savedir "${testserver}/worlds"
    -nographics
    -batchmode
    -public 0
)

VALHEIMONE_CONTRACT=1 setsid bash -c '
    export DOORSTOP_ENABLED=1
    export DOORSTOP_TARGET_ASSEMBLY=./BepInEx/core/BepInEx.Preloader.dll
    export LD_LIBRARY_PATH="./doorstop_libs:${LD_LIBRARY_PATH:-}"
    export LD_PRELOAD="libdoorstop_x64.so:${LD_PRELOAD:-}"
    exec "$@"
' harness-doorstop "$server_bin" "${server_args[@]}" \
    > >(tee -a "$server_log") 2>&1 &
server_pid=$!

shell_pgid=$(ps -o pgid= -p "$$" 2>/dev/null | tr -d '[:space:]' || true)
for _ in {1..20}; do
    candidate_pgid=$(ps -o pgid= -p "$server_pid" 2>/dev/null | tr -d '[:space:]' || true)
    if [[ -n $candidate_pgid && $candidate_pgid != "$shell_pgid" ]]; then
        server_pgid=$candidate_pgid
        break
    fi
    kill -0 "$server_pid" 2>/dev/null || break
    sleep 0.1
done
if [[ -z $server_pgid ]]; then
    printf 'FAIL: could not establish an isolated server process group.\n' >&2
    exit 1
fi

contract_ready=0
deadline=$(( SECONDS + 240 ))
while (( SECONDS < deadline )); do
    if [[ -r $bepinex_log ]] &&
        grep -q 'VO_CONTRACT worldgen ' "$bepinex_log" &&
        grep -q 'VO_CONTRACT patches ' "$bepinex_log" &&
        grep -q 'VO_CONTRACT modules ' "$bepinex_log"; then
        contract_ready=1
        break
    fi
    if ! group_is_alive; then
        break
    fi
    sleep 1
done

if (( ! contract_ready )); then
    if group_is_alive; then
        printf 'FAIL: timed out waiting for all three VO_CONTRACT diagnostics.\n' >&2
    else
        printf 'FAIL: contract server exited before all three VO_CONTRACT diagnostics appeared.\n' >&2
    fi
fi

stop_server
wait "$server_pid" 2>/dev/null || true
server_pid=

failures=0
plugin_evidence=
harmony_evidence=
warning_evidence=
warning_count=0
ignored_baseline_evidence=
ignored_baseline_count=0
missing_db_count=0

if [[ ! -r $bepinex_log ]]; then
    printf 'FAIL: BepInEx log was not created: %s\n' "$bepinex_log" >&2
    failures=1
else
    for category in worldgen patches modules; do
        if ! grep -q "VO_CONTRACT ${category} " "$bepinex_log"; then
            printf 'FAIL: VO_CONTRACT %s diagnostic was not found.\n' "$category" >&2
            failures=1
        fi
    done

    plugin_evidence=$(grep -F 'Loading [ValheimOne' "$bepinex_log" | head -n 1 || true)
    if [[ -z $plugin_evidence ]]; then
        plugin_evidence=$(grep -F -- 'ValheimOne' "$bepinex_log" \
            | grep -E '\[Info[[:space:]]*:[[:space:]]*BepInEx\][[:space:]]+Loading' \
            | head -n 1 || true)
    fi
    if [[ -z $plugin_evidence ]]; then
        printf 'FAIL: plugin load line not found for ValheimOne.\n' >&2
        failures=1
    fi

    harmony_evidence=$(grep -n -i -E -A 8 "$harmony_exception_pattern" "$bepinex_log" || true)
    if [[ -n $harmony_evidence ]]; then
        printf 'FAIL: Harmony patching exception block(s) appeared in the BepInEx log:\n%s\n' \
            "$harmony_evidence" >&2
        failures=1
    fi

    classify_bepinex_errors
    if (( missing_db_count )); then
        printf 'FAIL: MissingDB appeared in the BepInEx log (%s); the pinned SmokeWorld fixture-pair install broke.\n' \
            "$missing_db_count" >&2
        failures=1
    fi
    if (( ignored_baseline_count )); then
        printf 'Ignored baseline vanilla errors (%s):\n%s\n' \
            "$ignored_baseline_count" "$ignored_baseline_evidence"
    fi
    if (( warning_count )); then
        printf 'WARNING: BepInEx reported non-baseline Error/Fatal lines; contract comparison continues:\n%s\n' \
            "$warning_evidence" >&2
    fi
fi

if (( failures )); then
    printf '  Server log: %s\n' "$server_log" >&2
    printf '  BepInEx log: %s\n' "$bepinex_log" >&2
    exit 1
fi

extract_contract_line() {
    local category=$1
    grep "VO_CONTRACT ${category} " "$bepinex_log" \
        | tail -n 1 \
        | sed -E "s/^.*(VO_CONTRACT ${category} .*)$/\\1/" \
        | tr -d '\r'
}

worldgen_line=$(extract_contract_line worldgen)
patches_line=$(extract_contract_line patches)
modules_line=$(extract_contract_line modules)
printf '%s\n%s\n%s\n' \
    "$worldgen_line" "$patches_line" "$modules_line" > "$candidate"

if (( bless )); then
    cp -f -- "$candidate" "$golden"
    printf 'CONTRACT BLESSED: %s\n' "$golden"
    exit 0
fi

if [[ ! -f $golden ]]; then
    printf 'NO GOLDEN — run with --bless to create\n' >&2
    exit 1
fi

if cmp -s -- "$golden" "$candidate"; then
    printf 'CONTRACT PASS\n'
    exit 0
fi

printf 'CONTRACT DRIFT\n' >&2
golden_worldgen=$(grep '^VO_CONTRACT worldgen ' "$golden" | tail -n 1 || true)
golden_patches=$(grep '^VO_CONTRACT patches ' "$golden" | tail -n 1 || true)
golden_modules=$(grep '^VO_CONTRACT modules ' "$golden" | tail -n 1 || true)

if [[ $golden_worldgen != "$worldgen_line" ]]; then
    printf 'WORLDGEN DRIFT: invalidate map caches and review worldgen changes.\n' >&2
fi
if [[ $golden_patches != "$patches_line" ]]; then
    printf 'PATCH DRIFT: failed= lists the exact broken patch points.\n' >&2
fi
if [[ $golden_modules != "$modules_line" ]]; then
    printf 'MODULE REGISTRATION DRIFT: module count or enabled registration changed.\n' >&2
fi
diff -u --label tools/golden-fingerprint.txt --label tools/contract-fingerprint.txt \
    "$golden" "$candidate" >&2 || true
exit 1

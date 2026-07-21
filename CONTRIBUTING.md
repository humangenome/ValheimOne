# Contributing to ValheimOne

Thanks for helping build ValheimOne. Please discuss large features in an issue before investing in a long implementation.

## Development basics

1. Target Valheim 0.221.12, BepInEx 5.4.2333, and `net472` unless the compatibility target changes deliberately.
2. Keep features isolated behind `IFeatureModule` and default every feature section to `Enabled = false`.
3. Declare typed keys and client/server classification through `FeatureRegistry`.
4. Run `./build.sh` and keep the build free of errors before submitting a change.
5. Update `README.md` and `CHANGELOG.md` when behavior or operator-facing configuration changes.

## Verify before you patch

Check every target member's existence and runtime visibility against the original Valheim game assembly before writing a patch. Use a verified public API where one exists and cached reflection for vanilla-private members. Do not use transpilers. Install patches at startup and keep their behavior behind runtime `Enabled` guards so synchronized configuration can hot-enable features safely.

## Clean-room requirement

ValheimOne is an original implementation. Do not copy, adapt, or consult ValheimPlus source code while contributing. Behavioral compatibility may be implemented from independently written specifications and observed game behavior. Respect the licenses of every dependency and reference.

## Style

Prefer small, stable Harmony hooks. Handle missing game APIs defensively, keep server operators informed through actionable logs, and document whether a setting is server-authoritative, synced (client-required), or client-only.

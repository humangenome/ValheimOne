# Contributing to ValheimOne

Thanks for helping build ValheimOne. The project is unreleased, so discuss large features before investing in a long implementation.

## Development basics

1. Target Valheim 0.221.12, BepInEx 5.4.2333, and `net472` unless the compatibility target changes deliberately.
2. Keep features isolated behind `IFeatureModule` and default every feature section to `Enabled = false`.
3. Declare typed keys and client/server classification through `FeatureRegistry`.
4. Run `./build.sh` and keep the build free of errors before submitting a change.
5. Update `README.md` and `CHANGELOG.md` when behavior or operator-facing configuration changes.

## Clean-room requirement

ValheimOne is an original implementation. Do not copy, adapt, or consult ValheimPlus source code while contributing. Behavioral compatibility may be implemented from independently written specifications and observed game behavior. Respect the licenses of every dependency and reference.

## Style

Prefer small, stable Harmony hooks over IL transpilers. Handle missing game APIs defensively, keep server operators informed through actionable logs, and document whether a setting is server-authoritative, requires clients, or is client-only.

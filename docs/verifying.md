# Verifying the binary

A byte-for-byte reproduction requires a Git clone checked out at the exact release tag, the .NET SDK pinned in `global.json`, and a clean working tree. The compile is deterministic and embeds no commit SHA or build-host paths, so identical sources and toolchain produce an identical DLL.

```bash
git clone https://github.com/HumanGenome/ValheimOne.git
cd ValheimOne
git checkout --detach v<version>
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --version 10.0.302 --install-dir "$HOME/.dotnet"
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"
tools/verify-reproducible.sh --release v<version>
```

The verifier clean-builds the DLL and checks it against both the checked-in release provenance and the DLL inside the GitHub release asset.

```text
building HEAD commit: <40-hex commit>
verified: clean build ValheimOne.dll <sha256>
verified: published v<version> plugin zip ValheimOne.dll <sha256>
REPRODUCIBLE <version>
```

The Thunderstore package is the same source built with `-p:ValheimOneHostingCredit=false`, which leaves the link in the map sidebar's footer out. At the release tag, `tools/package-thunderstore.sh` rebuilds that DLL and archive and checks both against `tools/release/thunderstore-provenance.tsv`.

# ![BSIPA](docs/images/banner_dark.svg) [![Build](https://github.com/nike4613/BeatSaber-IPA-Reloaded/workflows/Build/badge.svg)](https://github.com/nike4613/BeatSaber-IPA-Reloaded)

[ALL DOCUMENTATION HAS MOVED HERE](https://nike4613.github.io/BeatSaber-IPA-Reloaded/)

The v5 branch retains legacy IPA/BSIPA interfaces, configuration adapters and
SemVer facades. Compatibility copies use the game's `Plugins/.cache/Compatibility`
directory; original plugin DLLs remain unchanged. Managed and native dependencies
can use either `Libs` or `IPA/Libs`. Harmony 1 is packaged separately from Harmony 2
and receives its separate assembly identity when loaded through the compatibility
cache. These adapters do not guarantee compatibility with every historical mod
or game API.

Each plugin's original `IPA.Loader` assembly reference identifies its binary
contract. Historical BSIPA 3 builds use assembly version 1.0; BSIPA 4 and this v5
branch share assembly version 2.0. The binary alone cannot identify the precise
BSIPA release. `PluginMetadata.TargetLoaderVersion` preserves that binary version;
`TargetIpaVersion` exposes the profile selected with the manifest dependency range.
BSIPA 3 plugins use the legacy manager and configuration contracts;
BSIPA 4 plugins keep the modern manager and configuration APIs. Old
`AlmostVersion.SemverValue` calls returning `SemVer.Version` use a separate getter,
while current calls keep their `Hive.Versioning.Version` return type. Compatibility
rewriting applies only to types from `IPA.Loader`, including dependent libraries.

BSIPA dependencies are checked against the selected 3.13.7, 4.3.7 or 5.0.0 API
profile in the binary's family. A bare manifest can request those profiles without
an assembly reference. Plugins requiring an older exact version can declare
`"misc": { "ipa-version": "4.1.6" }`. The declaration must agree with the binary
family and `dependsOn.BSIPA`; it selects the existing family adapters, rather than
emulating every historical implementation detail. Future API profiles and future
binary contracts are rejected rather than rewritten as supported. An unrelated
legacy range no longer bypasses dependency checks.

The bootstrap locates the game's managed engine assembly and inserts its call
without replacing an existing camera initializer. Game data directories are
selected explicitly when multiple directories exist. This remains a managed Mono
loader; an IL2CPP player, a missing runtime API, or a plugin using a changed game
API needs separate support. Historical bug-for-bug behavior is not emulated.

For an output build against an installed game, pass its root explicitly:

```powershell
dotnet build IPA.Injector/IPA.Injector.csproj -c Release -p:GameDirectory="D:/BSManager/BSInstances/1.29.1"
```

`UnityManagedPath` can select another managed reference directory. Without either
property the projects use `Refs`. Compilation verifies reference compatibility;
startup, plugin lifecycle and native dependency compatibility require running the
result in each supported game version.

Build the complete Windows package with the packaging project:

```powershell
dotnet build BSIPA-Meta/BSIPA-Meta.csproj -c Release -p:Platform=x64 -p:GameDirectory="D:/BSManager/BSInstances/1.45.2"
```

The extracted layout is in `BSIPA-Meta/bin/x64/Release/net472`; the corresponding
archive is `BSIPA-Meta/bin/x64/Release/BSIPA.zip`. Its root contains `IPA`, `Libs`,
`doorstop_config.ini` and `winhttp.dll`. Use `Platform=x86` for a 32-bit package.
Release packages exclude PDB files. The injector project alone produces managed
components and does not create this installable layout.

Packaging downloads the official [Unity Doorstop 4.5.0](https://github.com/NeighTools/UnityDoorstop/releases/tag/v4.5.0) Windows release and source
archive, verifies both pinned SHA-256 values, and caches them under `BSIPA-Meta/obj`.
Its configuration invokes `Doorstop.Entrypoint.Start` in `IPA.Injector.dll`.
The Doorstop license and source archive accompany the binary under
`IPA/ThirdParty/Doorstop`. Package generation does not install or launch the game.

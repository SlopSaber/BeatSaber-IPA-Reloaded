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

BSIPA dependencies select the newest matching recorded release profile in the
binary's family, including bounded ranges for older releases. A known IPA binary
without `dependsOn.BSIPA` gets an inferred loader dependency instead of being
discarded. A bare manifest can select a profile through its dependency range.
`"misc": { "ipa-version": "4.1.6" }` can request an exact API profile. It must agree
with the binary family and dependency range. A profile uses the retained family
adapters; it does not run a complete historical loader beside the current one.
Future API profiles and binary contracts are rejected.

Legacy version parsing accepts historical loose forms, including `1.8.0.1` as
`1.8.0-1`, while the SemVer facade's strict constructor remains strict. Legacy
feature arrays are parsed, including empty arrays, feature declarations, config
providers, parameter injectors and diagnostic features. The BSIPA 3 and early
BSIPA 4 feature facades preserve positional arguments and Boolean load/init
hooks. Cached type/member references select these contracts without changing
current feature APIs. New compatibility adapters belong at these binary-contract
boundaries; copying an old `IPA.Loader.dll` into a mod folder does not provide
isolated historical loader behavior.

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

The installation check accepts the packaged `doorstop_config.ini`. If another
file triggers the check, the startup error identifies its path relative to the
game installation.

Both `Mods` and `Plugins` are searched recursively. Startup creates `Mods/Core`
for a mod manager to populate; BSIPA does not download core mods automatically.
For example:

```text
Beat Saber/
  IPA/
  Libs/
  Mods/
    Core/
      BeatSaberMarkupLanguage.dll
      SiraUtil.dll
    MyMod.dll
    Visual/
      Effects/
        Effects.dll
        Libs/Helper.dll
  Plugins/
    LegacyPlugin.dll
    AnotherFolder/AnotherLegacyPlugin.dll
```

The dependency graph determines load order for all folders, including `Core`.
Duplicate plugin IDs use the newest plugin version, with a stable path order for
equal versions. Loader caches, `.git` directories and native-library directories
are excluded from plugin discovery. Windows directory aliases are deduplicated
by their resolved directory paths to prevent recursive junction loops. Legacy
interface plugins use distinct cached copies for each source path, so identical
filenames in separate folders do not overwrite each other's cache files.

Managed libraries are discovered in both mod trees, root `Libs` and legacy
`IPA/Libs`. Resolution uses the assembly identity, including culture and public
key token, rather than requiring the filename to match the assembly name.
Exact versions are preferred, then libraries near the requesting plugin and
newer available versions. Renamed compatibility copies retain their source
location for this lookup. Native DLL directories are added to the search path.
This does not isolate arbitrary incompatible libraries sharing one identity.

Declared files retain their original game-relative meaning. Existing mod-local
files and legacy library locations are accepted when a mod is organized into a
subfolder; a unique relocated library/plugin file can also satisfy an old path.
Ambiguous or genuinely missing files are still reported. Moving a DLL does not
rewrite the plugin's own hardcoded asset paths. Mod managers must install its
complete library and asset dependencies. BSIPA cannot supply an absent third
party DLL, repair changed game APIs, or promise every historical bug and private
implementation detail.

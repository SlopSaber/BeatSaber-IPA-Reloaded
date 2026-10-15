# ![BSIPA](docs/images/banner_dark.svg) [![Build](https://github.com/nike4613/BeatSaber-IPA-Reloaded/workflows/Build/badge.svg)](https://github.com/nike4613/BeatSaber-IPA-Reloaded)

[ALL DOCUMENTATION HAS MOVED HERE](https://nike4613.github.io/BeatSaber-IPA-Reloaded/)

The v5 branch retains legacy IPA/BSIPA interfaces, configuration adapters and
SemVer facades. Compatibility copies use the game's `Mods/.compatibility`
directory, including its `Legacy` subfolder for old IPA plugins. This directory
is excluded from mod discovery; original plugin DLLs remain unchanged. Managed and native dependencies
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

BSIPA dependencies select the nearest recorded release profile for the plugin's
manifest game version, subject to the dependency range and binary API signatures.
The catalog records 80 upstream tags and four additional approved BeatMods
releases. For example, the game-era defaults select 4.0.5 for Beat Saber 1.11.1
and 4.1.3 for 1.12.2. A mod requiring a newer API is promoted to the first eligible
recorded release. An explicit `"misc": { "ipa-version": "4.1.6" }` takes precedence
and must match a recorded release, the binary contract and dependency range.
Without a manifest game version, selection uses the running game; without a known
game baseline, it uses the dependency range and binary evidence. Selection logs
include the chosen version and its evidence.

Profiles now choose the positional feature contract before 4.1.0 and the JSON
feature contract from 4.1.0 onward. Legacy arrays are ignored under later profiles,
as in those upstream releases. Cached member references retain historical feature
return types, the scheduler's original `TaskScheduler` return type and SemVer
getters. `PluginManager.AllPlugins` retains its historical enabled-plugins view.
Legacy profiles accept an explicit null manifest ID and substitute the plugin's
name; current profiles require a non-null ID. Profiles before 4.2.1 require a
manifest game version. Cache paths include the selected profile, and dependent
libraries use their own recorded profile or the requesting plugin's context.

When a discovered plugin selects a profile before 4.3.0, startup also restores
legacy game assembly access before those assemblies load. Eligible instance
methods become public and virtual; private fields become protected. The game
assemblies come from `GameAssemblies`, with `Assembly-CSharp.dll` as a fallback.
Original files are backed up under `IPA/Backups/LegacyCompatibility/<backup-id>`
with game-relative paths. Already-patched files are left alone. A game assembly
loaded too early is reported and requires a full game restart. Profiles from
4.3.0 onward do not request these patches. Changes persist in the game files;
restore the saved originals to undo them.

Missing optional XML serializer assemblies are logged at debug level. Explicit
assembly dependencies still report missing libraries as errors.

A known IPA binary without `dependsOn.BSIPA` gets an inferred loader dependency.
A profile uses shared adapters at verified API boundaries; it does not execute
a complete historical loader. See [the compatibility research](docs/articles/compatibility.md)
for sources, release coverage and limits. Future and unrecorded explicit profiles
are rejected.

Legacy version parsing accepts historical loose forms, including `1.8.0.1` as
`1.8.0-1`, while the SemVer facade's strict constructor remains strict. Legacy
feature arrays are parsed, including empty arrays, feature declarations, config
providers, parameter injectors and diagnostic features. The BSIPA 3 and early
BSIPA 4 feature facades preserve positional arguments and Boolean load/init
hooks. Cached type/member references select these contracts without changing
current feature APIs. New compatibility adapters belong at these binary-contract
boundaries; copying an old `IPA.Loader.dll` into a mod folder does not provide
isolated historical loader behavior.

`TidyUp` defaults to `true` in `UserData/Beat Saber IPA.json`. Before mod
loading, it moves legacy Plugins contents into Mods and identifies core mods
from embedded manifest IDs. `CoreMods` defaults to `BeatSaberMarkupLanguage`,
`SongCore`, `SiraUtil` and `BS Utils`; these go into `Mods/Core`.

Bundled loader dependencies now live in `IPA/CoreLibraries`. Mod-supplied
libraries go directly into `Mods/Libraries`, without per-library/version folders.
Different files with the same filename get a content hash suffix. Native asset
directories retain their relative layout. File lists in external and embedded
manifests are updated to the new locations.
With `TidyUp=true`, bare manifest metadata moves into `IPA/ModLibraryIndex.json`;
manifest files, XML documentation, debug symbols and text sidecars are backed up
before removal. Package IDs, versions, dependencies and feature data remain in
the index. The index is committed before sidecar removal. With `TidyUp=false`,
libraries and their sidecars are flattened into `Mods/Libraries`; indexed
manifests and archived library sidecars are restored there when switching modes.
Libraries without manifests get metadata inferred from their assembly ID and
informational/assembly version unless indexed metadata already supplies their
package identity. Library DLLs are not treated as plugins.
Legacy library-path getters in prepared mods target `Mods/Libraries`.

Historical dependencies use `IPA/CoreLibraries/Versions/<first>-<last>` when
adjacent recorded BSIPA releases have identical library inventories. A release
with its own inventory uses a single version name. Each historical DLL is stored
once; each range's `libraries.json` lists its shared canonical file paths.
NuGet and upstream provenance stays in the index. The package includes 65
historical DLL providers from upstream BSIPA 3/4 tags
and runtime NuGet dependencies. `CoreLibraryIndex.json` records source releases,
package versions and content hashes; `provider-releases.tsv` supplies bootstrap
routing without requiring a JSON library to load itself. Resolution prefers
exact assembly identity, then the requesting mod's BSIPA release, nearby files
and compatible assembly majors. Current loader dependencies remain at the core
library root. Already-loaded CLR assembly identities retain normal runtime
binding behavior; this does not provide separate runtimes per mod.

Tidy moves verify content before removing the source. Original manifests,
documentation and mod/loader PDB files are archived under
`IPA/Backups/TidyUp`, with an operation journal for rollback. Locked files remain
retryable duplicates until a later launch. Linked source trees are left alone.
Set `TidyUp` to `false` to retain sidecars and stop plugin/core cleanup; the flat
library layout still applies. Legacy Libs/Plugins paths remain readable. Normal
package builds use the committed historical archive.
The source archive and installable ZIP both store historical DLLs once. Old
NuGet/Upstream core layouts move to the TidyUp backup
after the replacement release layout is complete. File-less library manifests
can associate their DLLs by assembly identity, title or namespace; legacy plugin
entry points remain discoverable as plugins.
Old per-release DLL copies move to the backup only after their content hashes
match a verified canonical replacement. Unknown files remain in place.
Regenerate it after restoring packages, with upstream tag history present:
`python tools/build_core_libraries.py`; runtime projects, transitive packages,
licenses and package metadata are included, while Unity and development tools
are excluded.

BSIPA stores game data beneath `UserData/Hyperbolic Magnetism/Beat Saber`.
`Shared` contains portable PlayerPrefs, common graphics/player settings, colors,
custom difficulty labels and score stores. `Versions/<game-version>/Native`
contains the files that each game reads and writes. `Backups` retains immutable,
content-addressed copies of the original AppData files and subsequent saves.
Existing global AppData is imported without being changed.

Scoring eras are fingerprinted from native scoring code and serialized layouts;
uncertain formats stay separate. Native DTO metadata projects shared data into
the running game's fields and enum values. Unsupported difficulties,
characteristics, labels and unknown fields are retained in
`Shared/CustomDifficultyLabels.json`, while unsupported score records are kept
out of the native view. Characteristic getters register names supplied by mods.
Map metadata labels are also collected independently of game enum serialization.
Mods can use `IPA.Data.CustomDifficultyLabels.Set/Get` with their level IDs.

Graphics values with matching meanings and player color schemes are shared.
Legacy graphics preset indices remain version-specific because their numeric
indices refer to different preset assets. Shared writes are locked and atomic;
score maxima survive default/reset saves, and play-count deltas merge across
concurrent sessions. Concurrent copies of the same game version get separate
native sessions. Unknown mod save files remain in the native version directory.

Game/mod PlayerPrefs calls use `Shared/PlayerPrefs.json`. Existing registry
values are imported lazily on first read; subsequent writes and deletions go to
JSON. Unity's native player logs and engine caches retain their own paths.
Game assemblies are backed up under `IPA/Backups/GameDataRouting` before their
managed persistence calls are redirected. Restoring those binaries and the
original AppData files provides the rollback path. Full restart is required.

IPA provides desktop flight in menus and levels, including versions before 1.0.
Launch with `fpfc`, `-fpfc` or `--fpfc`, or set `EnableFPFC` in the IPA config.
`--no-fpfc` disables IPA flight. Controls: WASD move, Q descends, E ascends,
Shift moves three times faster, and Ctrl moves at quarter speed. Vertical flight
uses twice the normal movement speed (3 metres/second by default). Escape
pauses a running level or dismisses the selected text field without releasing
the cursor. Movement uses unscaled time, so flight also works while paused.
`FPFCMoveSpeed` (default 1.5 metres/second) and `FPFCLookSensitivity` (default 0.2)
tune movement and look. Text fields suppress flight and look controls while
keeping the cursor locked.
Camera binding preserves the game's world spawn pose. A 1.6 m fallback applies
only at world ground level, so standing-height parent rigs are not counted twice.

FPFC locks and hides the cursor whenever the game is focused and a camera is
active. UI clicks require captured input; the pointer always follows the centre
camera ray rather than a free screen cursor. Losing focus releases capture and
cancels pending clicks/drags. Refocusing restores capture automatically, ignoring
the transition click and mouse-warp delta.

A single camera-aligned pointer uses the game's existing world and curved UI
raycasters. Left click, dragging and scrolling work in menus and pause screens.
The laser and hit dot use the running game's native menu prefabs, including their
materials and newer laser fading. Controller hilts stay hidden during FPFC. The
beam begins at the camera's exact position and shares the UI input ray; there is
no controller offset or overlay crosshair. Native/SiraUtil camera controllers,
tracking and duplicate pointer visuals yield to IPA while active. Their prior
states are restored when the component shuts down. The input adapter supports
legacy Unity Input and the new Input System without bundling either engine
module.

Legacy mouse axes retain their configured scaling and receive
`FPFCLookSensitivity` once, with no extra backend multiplier. In the inspected
1.11.1 settings, MouseX/MouseY sensitivity is 1; removing IPA's previous 10x
multiplier reduces default legacy look speed tenfold without changing the saved
sensitivity or the new Input System path.

Plugin transactions now cancel opposite operations instead of reporting a state
change after returning to the original state. This prevents unchanged disabled-mod
config reloads from notifying mods such as BS Utils, which can restart the menu in
response. Actual plugin changes still notify their listeners. Debug logs record
`MenuTransitionsHelper.RestartGame` callers to identify any remaining extra loads.
The supplied player log shows one IPA initialization and two fresh-menu callbacks;
that alone does not identify the restart caller.

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
archive is `BSIPA-Meta/bin/x64/Release/BSIPA.zip`. Its root contains `IPA`,
`doorstop_config.ini` and `winhttp.dll`. Use `Platform=x86` for a 32-bit package.
Release packages exclude PDB files. The injector project alone produces managed
components and does not create this installable layout.

One managed build supplies the runtime compatibility profiles for all supported
game versions. `GameDirectory` supplies compilation references; it does not
select a version-specific package. Normal builds retain compiler diagnostics and
skip the full static analyzer pass. Use `-p:RunAnalyzersDuringBuild=true` for that
pass, which CI enables. Package copies and archive extraction are incremental;
an unchanged build keeps the existing ZIP.

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

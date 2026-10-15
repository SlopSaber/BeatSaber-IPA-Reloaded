# Historical BSIPA compatibility

Research date: 2026-10-14. This catalog covers all 80 tags returned by the upstream repository, all 54 published GitHub release entries, and four additional approved BeatMods distributions. Every local tag commit was checked against the upstream tag API. The catalog is embedded in the loader; startup does not query these services.

## Selection and evidence

The original binary reference identifies the assembly contract, not the BSIPA release: 3.x uses IPA.Loader 1.0, while 4.x and this branch use 2.0. The loader first honors an explicit recorded `misc.ipa-version`. Otherwise it uses the plugin manifest game version, falling back to the running game, and chooses the latest eligible profile at or below that game-era baseline. If the dependency range or required binary signatures exclude earlier releases, it chooses the earliest eligible later release. Family, range and signature constraints always apply.

The game baselines come from approved [BeatMods distributions](https://beatmods.com/api/v1/mod). The upstream self-manifest is recorded separately because its build target can lag the distributed game version: the [4.1.3 source manifest](https://github.com/nike4613/BeatSaber-IPA-Reloaded/blob/4.1.3/IPA.Loader/Loader/manifest.json) still names 1.11.1, whereas its approved distribution names 1.12.2. Late backports do not redefine an earlier API era: game 1.30.2 keeps its original 4.3.0 baseline rather than a later 4.3.6 backport.

Examples, when the mod dependency range and API signatures allow them:

| Plugin game target | Selected baseline |
| --- | --- |
| 1.11.1 | 4.0.5 |
| 1.12.2 | 4.1.3 |
| 1.13.2 | 4.1.4 |
| 1.13.4 | 4.1.6 |
| 1.16.3 | 4.2.0 |
| 1.19.0 | 4.2.1 |
| 1.29.1 | 4.2.2 |
| 1.40.8 | 4.3.5 |
| 1.44.0 | 4.3.7 |

A plugin declaring `^4.1.3` for game 1.11.1 selects 4.1.3 rather than 4.0.5. An exact 4.1.6 request selects 4.1.6. An impossible dependency, an unknown explicit release, or a contradictory binary contract is rejected with its constraints. Missing game-version fields require the optional-game-version contract introduced in 4.2.1.

## Adapted contracts

| Boundary | Evidence and implemented behavior |
| --- | --- |
| 3.12.0 | [Release](https://github.com/nike4613/BeatSaber-IPA-Reloaded/releases/tag/3.12.0): interface metadata and positional features use the retained BSIPA 3 facades. Older interface plugins retain their existing loader route. |
| 3.12.21 | [Release](https://github.com/nike4613/BeatSaber-IPA-Reloaded/releases/tag/3.12.21): retained feature callbacks accept both the earlier metadata callback and the later plugin-instance callback. |
| 4.0.2–4.0.5 | [Feature source](https://github.com/nike4613/BeatSaber-IPA-Reloaded/blob/4.0.5/IPA.Loader/Loader/Features/Feature.cs): positional arguments and Boolean load/init hooks use the early 4.x feature facade, including metadata getters that only mention the old feature return type. |
| 4.1.0 | [Release](https://github.com/nike4613/BeatSaber-IPA-Reloaded/releases/tag/4.1.0): JSON feature objects and void `BeforeInit` use the current feature contract. Legacy arrays are ignored, matching the [historical converter](https://github.com/nike4613/BeatSaber-IPA-Reloaded/blob/4.1.3/IPA.Loader/JsonConverters/FeaturesFieldConverter.cs). |
| 4.2.0 | [Release](https://github.com/nike4613/BeatSaber-IPA-Reloaded/releases/tag/4.2.0): Hive-returning binary signatures require at least 4.2.0. Historical SemVer return signatures retain the existing facade/getter bridges. |
| 4.2.1 | [Release](https://github.com/nike4613/BeatSaber-IPA-Reloaded/releases/tag/4.2.1): optional manifest game version and repeated feature definitions. Earlier profiles require the game version. |
| 4.3.0 | The scheduler getter returns `UnityMainThreadTaskScheduler` in [4.3.0](https://github.com/nike4613/BeatSaber-IPA-Reloaded/blob/4.3.0/IPA.Loader/Utilities/Async/UnityMainThreadTaskScheduler.cs), versus `TaskScheduler` in [4.1.6](https://github.com/nike4613/BeatSaber-IPA-Reloaded/blob/4.1.6/IPA.Loader/Utilities/Async/UnityMainThreadTaskScheduler.cs). Old references redirect to a getter with the original return type. New signatures require at least 4.3.0. |
| 4.3.3 | [Release](https://github.com/nike4613/BeatSaber-IPA-Reloaded/releases/tag/4.3.3): Task-returning enable/disable methods require at least 4.3.3 and use the existing asynchronous lifecycle implementation. |
| Legacy IDs | [4.0.5 manifest contract](https://github.com/nike4613/BeatSaber-IPA-Reloaded/blob/4.0.5/IPA.Loader/Loader/PluginManifest.cs) and [4.3.7 contract](https://github.com/nike4613/BeatSaber-IPA-Reloaded/blob/4.3.7/IPA.Loader/Loader/PluginManifest.cs) permit an explicitly null ID. Legacy profiles substitute the name; the current profile requires a non-null ID. An absent ID field remains invalid. |
| Plugin enumeration | [Historical manager](https://github.com/nike4613/BeatSaber-IPA-Reloaded/blob/4.0.5/IPA.Loader/Loader/PluginManager.cs): `AllPlugins` remains an alias of enabled plugins. |

The selected profile is passed to assembly rewriting and forms part of the cache path. A library with its own manifest/profile keeps that profile; a library without one can inherit the requester context, adjusted for its own binary signatures. Unrelated assemblies without an IPA.Loader reference bypass catalog discovery, including the bootstrap load of Newtonsoft.Json. Original plugin and library files are preserved.

## Findings from the supplied installation

Static inspection of the 1.11.1 plugin binaries found two missing non-generic loader signatures after accounting for generic type/method parameters: SongCore references `PluginManager.AllPlugins`, and BSML references the scheduler getter returning `TaskScheduler`. Both contracts now have compatibility bridges. No plugin code was executed for this inspection.

`NoHealthAndSafety` embeds an explicit null ID and a historical loose version string. The null ID previously failed deserialization before the existing name fallback could run. Legacy profile validation now performs that fallback, while retaining loose version parsing.

The supplied log separately reports absent INIFileParser, BeatSaverSharp, ChatCore, DynamicOpenVR and its action file. An IniParser manifest is metadata, not the assembly that BS Utils requires. These missing dependencies must be installed; profile selection does not supply them. CameraPlus and ScoreSaber null-reference traces do not establish a loader API cause and remain unverified runtime issues.

## Follow-up log: 2026.10.14.19.05.56

This log confirms the restored API signatures and legacy ID fallback progressed past initialization. It exposes a further historical contract: BSIPA before 4.3.0 modified game assemblies to support inherited fields, public reflection and virtual methods. The [4.0.5 virtualizer](https://github.com/nike4613/BeatSaber-IPA-Reloaded/blob/4.0.5/IPA.Injector/Virtualizer.cs) made eligible instance methods public/virtual and private fields protected; the [4.1.6-era implementation](https://github.com/nike4613/BeatSaber-IPA-Reloaded/blob/4.2.1/IPA.Injector/Virtualizer.cs) also handled nested types and `in` modifiers. [4.3.0 removed this mechanism](https://github.com/nike4613/BeatSaber-IPA-Reloaded/releases/tag/4.3.0).

| Log evidence | Confirmed cause | Compatibility change |
| --- | --- | --- |
| BSML field access failure at line 758 | `HMUI.ScrollView._viewport` is private; BSML accesses it from a derived component. | Restore protected field access. |
| SongCore level-pack failure at line 1171 | `BeatmapLevelPackCollectionSO._allBeatmapLevelPacks` is private; SongCore accesses it from its derived collection. | Restore protected field access. |
| SongBrowser method access failure at line 1585 | `LevelCollectionTableView.HandleDidSelectRowEvent` is private. | Restore public virtual instance methods. |
| Versus null Harmony target at line 585 | Its IL uses public instance reflection for private `LevelFilteringNavigationController.InitializeIfNeeded`. | Restore the public method surface. |
| PP Helper null Harmony target at line 669 | Its IL uses public reflection for private `GameplayModifiersPanelController.RefreshTotalMultiplierAndRankUI`, plus protected `Awake`. | Restore the public method surface. |
| Serializer critical messages at lines 855 and 858 | The generated `SongBrowser.XmlSerializers` assembly is an optional framework probe; settings loading subsequently succeeds. | Keep normal lookup, then log optional probe failure at debug level. Explicit binary references remain critical. |

Static inspection of the installed game metadata and plugin IL confirms the five access/reflection causes. The [framework serializer implementation](https://github.com/microsoft/referencesource/blob/main/System.Xml/System/Xml/Serialization/Compilation.cs) explicitly tolerates failure to load a guessed serializer assembly.

Before the bootstrap hook is installed, the loader examines plugin profiles and selects the newest profile that still requires legacy game patches. Newer-only installations do not request these changes. It patches configured game assemblies and the historical Assembly-CSharp fallback on disk, with a unique backup in `IPA/Backups/LegacyCompatibility`. Backups use the installation root rather than the launcher's working directory. Only changed assemblies are written, using a completed temporary file and atomic replacement. Loaded assemblies are reported rather than replaced. Value types, interfaces and static class layouts do not acquire virtual instance slots. The 4.1.6 modifier/final-method rules are enabled only for those later legacy profiles.

These changes do not execute game code during the build. They apply on the next full startup with the updated package. Restore originals from the logged backup directory to undo persistent game assembly changes.

The log also contains unavailable remote endpoints, a missing DynamicOpenVR action file, user/configuration messages and later mod null-reference failures. These are not evidence of further loader API failures. Some scene/song failures may be downstream of the confirmed access errors; their outcome needs a fresh game log after installing this build.

## Virtualizer history audit

The historical virtualizer has several separate contracts. Checking only whether
a method is virtual does not reproduce its metadata or upgrade behavior.

| Upstream change | Relevant behavior |
| --- | --- |
| [f1a6583b, September 2018](https://github.com/nike4613/BeatSaber-IPA-Reloaded/commit/f1a6583b) | Move virtualization into the loader; unseal eligible classes, expose eligible instance methods as public virtual new-slot methods, and make private fields protected. |
| [0168e3db, December 2019](https://github.com/nike4613/BeatSaber-IPA-Reloaded/commit/0168e3db) | Use the configured game assembly list rather than one hardcoded assembly. |
| [24643eb9, March 2021](https://github.com/nike4613/BeatSaber-IPA-Reloaded/commit/24643eb9) | Add parameter modifier handling and rebuild signatures with required modifiers inside optional modifiers. |
| [17809558, March 2021](https://github.com/nike4613/BeatSaber-IPA-Reloaded/commit/17809558) | Make nested private types public. |
| [8ad818e1, March 2021](https://github.com/nike4613/BeatSaber-IPA-Reloaded/commit/8ad818e1) | Stop adding an `out` parameter modreq because it breaks overrides. |
| [c6b9f05b, March 2021](https://github.com/nike4613/BeatSaber-IPA-Reloaded/commit/c6b9f05b) | Also process virtual-final methods, clear final, and explicitly set new-slot dispatch. |
| [634d8fe2, May 2023](https://github.com/nike4613/BeatSaber-IPA-Reloaded/commit/634d8fe2) | Remove the virtualizer and HarmonyProtector. The first containing release is 4.3.0. |

The nested-type, parameter-modifier and virtual-final changes first appear in
the recorded 4.1.6 release. The compatibility profiles retain that boundary and
the removal boundary at 4.3.0. `out` modreqs remain excluded.

The audit found that this branch set new-slot only for previously nonvirtual
methods, missing the upstream assignment for virtual-final methods. It also
wrapped the `in` modreq around existing optional modifiers rather than rebuilding
their historical order. Both differences are corrected.

A matching original backup is used to identify eligible methods after a prior
pass has cleared their original final/private flags. The earliest readable
LegacyCompatibility backup with the same module MVID and assembly identity
supplies the baseline; method token, declaring type, name and generic/parameter
counts are checked before using its metadata. Modifier order comes from that
baseline, avoiding repeated reordering on subsequent launches. Actual changes
are compared before writing, and the existing backup and loaded-assembly guards
remain in place. This reads original metadata; it does not restore old method
bodies over persistence routing or other current code.

Without a matching original, only the available current metadata can determine
eligibility. A clean game assembly or matching pre-virtualization backup is
needed to recover flags already erased by another patcher. Value types,
interfaces and static class layouts retain the current safety exclusions rather
than reproducing invalid metadata transformations.

Static inspection found no original reused-slot final methods in the installed
0.11.2 Assembly-CSharp baseline. The history audit therefore establishes
implementation gaps, not the cause of an unspecified mod failure. A concrete
mod, game version and error are still required for that attribution. Compilation
does not establish complete historical compatibility.

## Coverage limits

Recorded release versions are selection points, not 84 separate complete historical runtimes. Releases sharing the same relevant contract share adapters. The four untagged distributions are identified below; their API boundary is inferred from their release family rather than claimed as a source-complete audit. Existing fixes and security checks remain in the current loader. Removed game APIs, arbitrary Harmony differences, native runtime issues, historical bugs and missing third-party assets are not emulated.

Validation uses output-producing builds only. Compilation does not establish successful startup or lifecycle behavior for every historical mod. No automated tests or game launches are part of this change.

## Recorded release points

| Version | Source | Publication / provenance |
| --- | --- | --- |
| 4.3.7 | [tag 4.3.7](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/4.3.7) | 2026-01-11T20:07:47Z |
| 4.3.6 | [tag 4.3.6](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/4.3.6) | 2025-06-07T01:45:10Z |
| 4.3.5 | [tag 4.3.5](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/4.3.5) | 2024-08-09T19:10:30Z |
| 4.3.4 | [tag 4.3.4](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/4.3.4) | 2024-06-04T17:10:52Z |
| 4.3.3 | [tag 4.3.3](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/4.3.3) | 2024-03-18T20:39:28Z |
| 4.3.2 | [tag 4.3.2](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/4.3.2) | 2023-11-14T04:43:53Z |
| 4.3.1 | [tag 4.3.1](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/4.3.1) | 2023-11-13T03:35:28Z |
| 4.3.0 | [tag 4.3.0](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/4.3.0) | 2023-05-21T01:14:17Z |
| 4.2.2 | [BeatMods distribution 1405](https://beatmods.com/api/v1/mod) | approved distribution; family boundary inferred |
| 4.2.1 | [tag 4.2.1](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/4.2.1) | 2021-12-21T11:45:09Z |
| 4.2.0 | [tag 4.2.0](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/4.2.0) | 2021-07-20T21:11:59Z |
| 4.1.6 | [tag 4.1.6](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/4.1.6) | 2021-03-18T21:11:57Z |
| 4.1.4 | [tag 4.1.4](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/4.1.4) | 2021-01-22T04:44:16Z |
| 4.1.3 | [tag 4.1.3](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/4.1.3) | 2020-10-22T00:05:18Z |
| 4.1.2 | [tag 4.1.2](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/4.1.2) | 2020-09-03T00:08:35Z |
| 4.1.1 | [tag 4.1.1](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/4.1.1) | 2020-09-02T01:00:44Z |
| 4.1.0 | [tag 4.1.0](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/4.1.0) | 2020-08-31T07:29:18Z |
| 4.0.5 | [tag 4.0.5](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/4.0.5) | 2020-04-05T09:44:17Z |
| 4.0.4 | [tag 4.0.4](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/4.0.4) | 2020-04-04T05:13:45Z |
| 4.0.3 | [tag 4.0.3](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/4.0.3) | 2020-03-30T06:28:17Z |
| 4.0.2 | [tag 4.0.2](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/4.0.2) | 2020-03-26T22:52:30Z |
| 4.0.1 | [BeatMods distribution 553](https://beatmods.com/api/v1/mod) | approved distribution; family boundary inferred |
| 3.13.7 | [tag 3.13.7](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.13.7) | 2020-03-26T21:36:30Z |
| 3.13.6 | [BeatMods distribution 486](https://beatmods.com/api/v1/mod) | approved distribution; family boundary inferred |
| 3.13.5 | [tag 3.13.5](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.13.5) | 2019-12-21T21:11:40Z |
| 3.13.4 | [BeatMods distribution 427](https://beatmods.com/api/v1/mod) | approved distribution; family boundary inferred |
| 3.13.2 | [tag 3.13.2](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.13.2) | 2019-08-30T02:43:27Z |
| 3.13.1 | [tag 3.13.1](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.13.1) | 2019-08-30T01:11:05Z |
| 3.13.0 | [tag 3.13.0](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.13.0) | 2019-08-29T12:49:37Z |
| 3.12.24 | [tag 3.12.24](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.12.24) | 2019-07-25T18:54:40Z |
| 3.12.23 | [tag 3.12.23](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.12.23) | 2019-07-16T02:25:21Z |
| 3.12.22 | [tag 3.12.22](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.12.22) | 2019-06-13T19:03:10Z |
| 3.12.21 | [tag 3.12.21](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.12.21) | 2019-06-10T11:41:43Z |
| 3.12.20 | [tag 3.12.20](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.12.20) | 2019-05-22T20:49:56Z |
| 3.12.19 | [tag 3.12.19](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.12.19) | 2019-05-20T20:38:16Z |
| 3.12.18 | [tag 3.12.18](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.12.18) | 2019-05-17T23:04:48Z |
| 3.12.17 | [tag 3.12.17](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.12.17) | 2019-05-16T02:39:15Z |
| 3.12.16 | [tag 3.12.16](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.12.16) | 2019-05-14T00:51:59Z |
| 3.12.15 | [tag 3.12.15](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.12.15) | 2019-05-05T22:13:53Z |
| 3.12.14 | [tag 3.12.14](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.12.14) | 2019-04-23T00:38:55Z |
| 3.12.13 | [tag 3.12.13](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.12.13) | 2019-04-22T06:25:37Z |
| 3.12.12 | [tag 3.12.12](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.12.12) | 2019-04-15T01:48:26Z |
| 3.12.11 | [tag 3.12.11](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.12.11) | 2019-04-14T20:32:32Z |
| 3.12.10 | [tag 3.12.10](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.12.10) | 2019-04-11T01:40:43Z |
| 3.12.9 | [tag 3.12.9](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.12.9) | 2019-04-09T01:35:29Z |
| 3.12.8 | [tag 3.12.8](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.12.8) | 2019-04-06T17:39:37Z |
| 3.12.7 | [tag 3.12.7](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.12.7) | 2019-04-06T04:57:04Z |
| 3.12.6 | [tag 3.12.6](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.12.6) | 2019-04-05T23:06:46Z |
| 3.12.5 | [tag 3.12.5](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.12.5) | 2019-04-04T00:26:55Z |
| 3.12.4 | [tag 3.12.4](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.12.4) | 2019-04-03T03:30:04Z |
| 3.12.3 | [tag 3.12.3](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.12.3) | 2019-03-21T19:18:30Z |
| 3.12.2 | [tag 3.12.2](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.12.2) | 2019-02-20T03:03:25Z |
| 3.12.1 | [tag 3.12.1](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.12.1) | 2019-02-09T03:11:22Z |
| 3.12.0 | [tag 3.12.0](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.12.0) | 2019-02-09T02:51:09Z |
| 3.11.6 | [tag 3.11.6](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.11.6) | 2018-12-23T18:07:56Z |
| 3.11.5 | [tag 3.11.5](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.11.5) | 2018-12-04T06:24:42Z |
| 3.11.4 | [tag 3.11.4](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.11.4) | 2018-11-25T02:15:35Z |
| 3.11.3 | [tag 3.11.3](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.11.3) | 2018-11-17T03:44:41Z |
| 3.11.0 | [tag 3.11.0](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.11.0) | tagged source, no published release entry |
| 3.10.5 | [tag 3.10.5](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.10.5) | tagged source, no published release entry |
| 3.10.4 | [tag 3.10.4](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.10.4) | tagged source, no published release entry |
| 3.10.3 | [tag 3.10.3](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.10.3) | tagged source, no published release entry |
| 3.10.2 | [tag 3.10.2](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.10.2) | tagged source, no published release entry |
| 3.10.0 | [tag 3.10.0](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.10.0) | tagged source, no published release entry |
| 3.9.1 | [tag 3.9.1](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.9.1) | tagged source, no published release entry |
| 3.9.0 | [tag 3.9.0](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.9.0) | tagged source, no published release entry |
| 3.8.9 | [tag 3.8.9](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.8.9) | tagged source, no published release entry |
| 3.8.8 | [tag 3.8.8](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.8.8) | tagged source, no published release entry |
| 3.8.7 | [tag 3.8.7](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.8.7) | tagged source, no published release entry |
| 3.8.6 | [tag 3.8.6](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.8.6) | tagged source, no published release entry |
| 3.8.5 | [tag 3.8.5](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.8.5) | tagged source, no published release entry |
| 3.8.2 | [tag 3.8.2](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.8.2) | tagged source, no published release entry |
| 3.8.1 | [tag 3.8.1](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.8.1) | tagged source, no published release entry |
| 3.8.0 | [tag 3.8.0](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.8.0) | tagged source, no published release entry |
| 3.7.2 | [tag 3.7.2](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.7.2) | tagged source, no published release entry |
| 3.7.1 | [tag 3.7.1](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.7.1) | tagged source, no published release entry |
| 3.7.0 | [tag 3.7](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.7) | tagged source, no published release entry |
| 3.6.3 | [tag 3.6.3](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.6.3) | tagged source, no published release entry |
| 3.6.2 | [tag 3.6.2](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.6.2) | tagged source, no published release entry |
| 3.6.1 | [tag 3.6.1](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.6.1) | tagged source, no published release entry |
| 3.6.0 | [tag 3.6](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.6) | tagged source, no published release entry |
| 3.5.2 | [tag 3.5.2](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.5.2) | tagged source, no published release entry |
| 3.5.0 | [tag 3.5](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.5) | tagged source, no published release entry |
| 3.4.0 | [tag 3.4](https://github.com/nike4613/BeatSaber-IPA-Reloaded/tree/3.4) | tagged source, no published release entry |

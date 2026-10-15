# ![BSIPA](docs/images/banner_dark.svg) [![Build](https://github.com/nike4613/BeatSaber-IPA-Reloaded/workflows/Build/badge.svg)](https://github.com/nike4613/BeatSaber-IPA-Reloaded)

[ALL DOCUMENTATION HAS MOVED HERE](https://nike4613.github.io/BeatSaber-IPA-Reloaded/)

The v5 branch retains legacy IPA/BSIPA interfaces, configuration adapters and
SemVer facades. Compatibility copies use the game's `Plugins/.cache/Compatibility`
directory; original plugin DLLs remain unchanged. Managed and native dependencies
can use either `Libs` or `IPA/Libs`. Harmony 1 is packaged separately from Harmony 2
and receives its separate assembly identity when loaded through the compatibility
cache. These adapters do not guarantee compatibility with every historical mod
or game API.

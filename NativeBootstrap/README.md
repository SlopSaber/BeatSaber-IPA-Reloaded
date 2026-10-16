# BSIPA native desktop startup

This directory extends the pinned Unity Doorstop 4.5.0 source with FPFC startup
selection. The upstream LGPL-2.1 license and source ZIP accompany this directory
in install packages. CMake applies the entry-point changes and generates the
upstream proxy exports; the original source archive remains unchanged.

Build with CMake 3.21+ and Visual Studio's C++ desktop workload:

```powershell
cmake -S . -B build -A x64 -DDOORSTOP_SOURCE_DIR="path/to/UnityDoorstop-4.5.0"
cmake --build build --config Release
```

The normal BSIPA packaging build runs both steps automatically. Use `Win32`
instead of `x64` to build the 32-bit bootstrap.

Before Unity reads its startup settings, FPFC selects `-vrmode None` through
Doorstop's existing command-line hooks and redirects its boot configuration to a
process-specific copy. That copy retains other boot settings, disables native
VR, and omits the XR pre-initialization library. The original boot file and VR
runtime installation are unchanged. The temporary boot copy is deleted at
normal process shutdown; forced termination can leave it under `IPA/Boot`.

Explicit `fpfc`, `-fpfc`, and `--fpfc` arguments enable the mode. Otherwise the
bootstrap reads top-level Boolean `EnableFPFC` and `Regenerate` from the UTF-8
loader JSON. As with the managed configuration, regeneration resets the mode.
`--no-fpfc` wins over either source. The process-local `BSIPA_FPFC_NO_VR` marker
also activates conditional guards in the managed OpenXR/Oculus/OpenVR loaders.
Normal VR launches execute those loaders' original methods.

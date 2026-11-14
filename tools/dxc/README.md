# Fixed DXC toolchain

`scripts/setup-dxc.ps1` reuses a compiler only after every pinned x64 executable,
native library and license hash matches `manifest.json`. If none is available it
downloads the fixed official archive, checks its official size/SHA-256 and checks
all archive paths before extracting into the workspace's D: cache. It installs no
Windows SDK, changes no system/user PATH and preserves existing directories.
`-ForceInstall` skips the pre-existing shared compiler and exercises extraction
into the selected new controlled directory. It does not overwrite any directory.

The pinned release is [Microsoft DXC v1.8.2505.1](https://github.com/microsoft/DirectXShaderCompiler/releases/tag/v1.8.2505.1),
asset `dxc_2025_07_14.zip`. The release notes explicitly explain that its binary
version is `1.8.2505.32`. `manifest.json` records the asset URL and the official
GitHub release metadata digest retrieved on 2026-10-18, plus local file hashes.

Unmodified upstream license texts are preserved in `licenses/`: LICENSE-LLVM.txt
(University of Illinois/NCSA), LICENSE-MIT.txt and LICENSE-MS.txt (Microsoft DXC
terms). Upstream ReleaseNotes maps d3d12shader.h to MIT and the other package
files to LICENSE-LLVM; the archive also ships Microsoft terms. Keep all three
texts with any redistributed package and assess their terms for that package.
Only tooling/license metadata is committed here; compiler binaries stay in cache.

The optional `DxcShaderCompiler` adapter uses a hidden child process with an
argument list. Keep one instance at renderer initialization and reuse it. It
records actual executable/native-library hashes and `--version` even for an
explicit `MMD_DXC_PATH` or a PATH fallback. The cache identity includes exact
UTF-8 source bytes, canonical macros, entry point, profile, fixed flags and that
compiler identity. Successful results are cached in memory; external includes
are rejected. Sources, DXIL and compilation.json evidence stay in the configured
cache. Explicit `MMD_DXC_CACHE` or the constructor cache argument selects it.
The workspace default is `.dev-cache/modern-dotnet/dxc-compute`.

SM5.1 shaders still use their existing D3DCompiler route. Generating DXIL proves
compilation; it does not prove DXR availability, a valid PSO, dispatch or GI output.

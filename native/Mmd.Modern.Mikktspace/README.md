# Evaluated source-local MikkTSpace bridge

The unmodified upstream C implementation is pinned by commit and SHA256 in
`UPSTREAM.json`. MDA's small C ABI exports per-triangle-corner tangent/sign;
it never overwrites tangents using source vertex indices. Only evaluated local
positions, normalized current shading normals and selected effective UV enter
Mikk. World transform is applied once by the managed consumer afterward.

Build separately from the managed renderer, for the same process architecture:

```powershell
./build.ps1 -OutputDirectory D:/path/to/managed/output
```

By default the script uses `cl.exe` from PATH, or an exact MSVC compiler passed
with `-Compiler`. A standalone compiler can use explicit `-IncludeDirectories`
and `-LibraryDirectories` arrays (MSVC headers/libs plus Windows SDK UCRT/shared/um
headers and architecture-matching libraries). Existing INCLUDE/LIB environment
settings are also honored by MSVC. This direct route needs no CMake and uses the
static CRT. Object/import/debug files stay in the specified `-BuildDirectory`.

Select `-UseCMake` for the optional CMake route;
`-UseCMake -Generator Ninja -CCompiler D:/path/to/clang.exe` chooses that toolchain.
Both routes verify pinned inputs and install the runtime, notice and manifest.
Managed loading uses the exact `MMD_MIKKTSPACE_LIBRARY_PATH` file when set,
otherwise `mmd_mikktspace.dll` (or the platform shared library) beside the D3D12
assembly. A missing/invalid runtime raises a specific preflight error; it never
silently substitutes a rest or flat tangent. No third-party plugin/game assets
are included. ABI 1 limits vertices/corners to 1,000,000 each and validates
finite inputs, normalized normals, index bounds and every output corner.

This uses reference Mikk C, not Blender's C++ variant. Its output on
pathological degenerate cases is not claimed bitwise equivalent to Blender.

# Bullet compatibility backend

2026-10-26: production builds disable Bullet 2.75's optional process-global profiler with
`BT_NO_PROFILE=1`. Independent worlds may step concurrently; the profiler's shared
`CurrentNode` was observed crashing during parallel soft/rigid tests. This option removes
internal timing instrumentation, without changing solver flags, ABI, features or source.
Build the currently selected native inputs with:

```powershell
python scripts/build-bullet.py --output ../.dev-cache/bullet-no-profile-20261026/rigid/mmd_bullet.dll
python scripts/build-bullet-soft.py --output ../.dev-cache/bullet-no-profile-20261026/soft/mmd_bullet_soft.dll
```

The managed default paths and packaging hash gate select these outputs; old verified
DLLs remain in their historical directories. Each new build records the exact input
hashes, compiler command, profiler switch and output SHA in its own build-manifest.json.

Part of **MMD核心**, behind `ICharacterPhysicsBackend`; independent of GUI and rendering.

Build the native library from the vendored, unmodified Bullet 2.75 float32 core:

```powershell
python scripts/build-bullet.py
```

The script accepts `--compiler <zig.exe>` and `--output <mmd_bullet.dll>`. Default output is outside the source tree at `../.dev-cache/bullet-native-20261020/mmd_bullet.dll`. For another output location set the managed build property `-p:BulletNativeLibrary=<absolute DLL path>`. The DLL and its licenses propagate to consuming build/publish outputs.

Construct `BulletCharacterPhysicsBackend` and pass it to the existing `CharacterPhysicsSession` or `CharacterAnimationController.EnablePhysics` interface. The Bepu backend remains available. This batch does not change GUI defaults or silently switch existing projects.

Raw PMX coordinates and quaternion XYZW are preserved. Sphere, box and capsule, collision masks, friction, restitution, damping, and PMX joint types 0–5 map to Bullet. Unknown joint types are explicit diagnostics. Initialization prepares a candidate world; errors retain the previously published world. The session owns the fixed-step accumulator; native stepping does not add a second one.

Native ABI source: `../../native/Mmd.Modern.Bullet/`. Source archive identity, tool command and output hash are recorded by the build script. A Windows x64 DLL has been exercised; other platforms/toolchains are not verified. Missing native libraries cause an explicit loader error, with no silent Bepu fallback.

Remaining MMD compatibility includes full original world/constraint settings, cross-character collisions, original scheduling, and soft-body support. See `../../docs/CORE_COMPLETION_PLAN.md`.

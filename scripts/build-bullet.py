"""Build the vendored Bullet 2.75 core and the project's C ABI; no upstream scripts run."""
import argparse, hashlib, json, os, subprocess, time
from pathlib import Path
from bullet_soft_sources import prepare_soft_sources, prepared_compile_files, source_identity

root = Path(__file__).resolve().parent.parent
parser = argparse.ArgumentParser()
parser.add_argument('--compiler', type=Path, default=root.parent/'MMD932_20261014/research/Review/rebuild_dependencies/ziglang/zig.exe')
parser.add_argument('--output', type=Path, default=root.parent/'.dev-cache/shared-physics-20261021/mmd_bullet.dll')
args = parser.parse_args()
compiler = args.compiler.resolve()
output = args.output.resolve()
output.parent.mkdir(parents=True, exist_ok=True)
source = root/'third_party/bullet-2.75/src'
# The upstream Bullet 2.75 CMake build excludes its abandoned C API source.
files = sorted(p for directory in ('BulletDynamics','BulletCollision','LinearMath') for p in (source/directory).rglob('*.cpp') if p.name != 'Bullet-C-API.cpp')
soft_native=root/'native/Mmd.Modern.Bullet.SoftBody'
soft_upstream,soft_compat,patches=prepare_soft_sources(soft_native,output.parent)
files += sorted((soft_native/'upstream/BulletSoftBody').glob('*.cpp'))
files += [soft_native/'mmd_bullet_soft.cpp',root/'native/Mmd.Modern.Bullet/mmd_bullet.cpp']
compile_files=prepared_compile_files(files,soft_native,soft_upstream,soft_compat)
if not files or not compiler.is_file():
    raise SystemExit('Provide a Zig compiler and the vendored Bullet 2.75 sources.')
env = dict(os.environ, TEMP=str(output.parent), TMP=str(output.parent), ZIG_GLOBAL_CACHE_DIR=str(output.parent/'zig-global'), ZIG_LOCAL_CACHE_DIR=str(output.parent/'zig-local'))
# Zig defaults to -mcpu=native; a build host with wider SIMD would emit instructions that fail DllMain on user CPUs.
# Bullet 2.75's profiler has a process-global CurrentNode and is unsafe when independent worlds step concurrently.
command = [str(compiler),'c++','-target','x86_64-windows-gnu','-mcpu=baseline','-shared','-std=c++17','-O2','-DNDEBUG','-DBT_NO_PROFILE=1','-DBT_HAS_ALIGNED_ALLOCATOR','-DMMD_BULLET_RIGID_PARALLEL=1','-Wno-nullability-completeness','-Wno-c++11-narrowing','-fno-fast-math','-ffp-contract=off','-I',str(soft_upstream),'-I',str(soft_compat),'-I',str(source),*map(str,compile_files),'-o',str(output)]
input_before=source_identity(root,soft_compat)
start = time.monotonic()
with (output.parent/'compile.stdout.log').open('w',encoding='utf-8') as stdout, (output.parent/'compile.stderr.log').open('w',encoding='utf-8') as stderr:
    result = subprocess.run(command,env=env,stdout=stdout,stderr=stderr,timeout=900)
input_after=source_identity(root,soft_compat)
input_stable=input_before==input_after
manifest = dict(compilerExitCode=result.returncode,buildInputStable=input_stable,inputBefore=input_before,inputAfter=input_after,compiler=str(compiler),sourceFiles=[dict(path=str(p.relative_to(root)).replace('\\','/'),sha256=hashlib.sha256(p.read_bytes()).hexdigest()) for p in files+[soft_native/'mmd_soft_plane_collision.h',soft_native/'mmd_soft_attachment.h',root/'native/Mmd.Modern.Bullet/mmd_bullet.h',root/'native/Mmd.Modern.Bullet/mmd_bullet_parallel.h']],command=command,exitCode=result.returncode if input_stable else 1,elapsedSeconds=time.monotonic()-start,output=str(output),target='x86_64-windows-gnu',cpu='baseline',bulletVersion='2.75',scalarPrecision='float32',upstreamModified=False,coupledSoftBodies=True,**patches)
manifest['compiledSourceFiles']=[dict(path=str(p.resolve()),sha256=hashlib.sha256(p.read_bytes()).hexdigest()) for p in compile_files]
manifest['profilerDisabled']=True
if result.returncode == 0 and input_stable:
    manifest['sha256'] = hashlib.sha256(output.read_bytes()).hexdigest()
(output.parent/'build-manifest.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8')
print(json.dumps({k:v for k,v in manifest.items() if k not in ('sourceFiles','command','inputBefore','inputAfter')},indent=2))
if result.returncode:
    print((output.parent/'compile.stderr.log').read_text(encoding='utf-8',errors='replace')[-8000:])
if not input_stable: print('Build rejected: a compiler input changed during compilation.')
raise SystemExit(manifest['exitCode'])

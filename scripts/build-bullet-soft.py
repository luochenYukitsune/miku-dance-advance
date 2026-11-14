"""Build independent Bullet 2.75 soft solver; preserve verified rigid ABI4."""
import argparse,hashlib,json,os,subprocess,time
from pathlib import Path
r=Path(__file__).resolve().parent.parent
p=argparse.ArgumentParser();p.add_argument('--compiler',type=Path,default=r.parent/'MMD932_20261014/research/Review/rebuild_dependencies/ziglang/zig.exe');p.add_argument('--output',type=Path,default=r.parent/'.dev-cache/soft-core-completion-20261021/mmd_bullet_soft.dll');a=p.parse_args()
s=r/'third_party/bullet-2.75/src';n=r/'native/Mmd.Modern.Bullet.SoftBody';out=a.output.resolve();out.parent.mkdir(parents=True,exist_ok=True)
f=sorted(q for d in ('BulletDynamics','BulletCollision','LinearMath') for q in (s/d).rglob('*.cpp') if q.name!='Bullet-C-API.cpp')+sorted((n/'upstream/BulletSoftBody').glob('*.cpp'))+[n/'mmd_bullet_soft.cpp']
from bullet_soft_sources import prepare_soft_sources, prepared_compile_files, source_identity
compat_upstream,compat_soft,patches=prepare_soft_sources(n,out.parent)
compile_files=prepared_compile_files(f,n,compat_upstream,compat_soft)
env=dict(os.environ,TEMP=str(out.parent),TMP=str(out.parent),ZIG_GLOBAL_CACHE_DIR=str(out.parent/'zig-global'),ZIG_LOCAL_CACHE_DIR=str(out.parent/'zig-local'))
# The upstream profiler shares a process-global CurrentNode across independent solver worlds.
cmd=[str(a.compiler.resolve()),'c++','-shared','-std=c++17','-O2','-DNDEBUG','-DBT_NO_PROFILE=1','-DBT_HAS_ALIGNED_ALLOCATOR','-Wno-nullability-completeness','-Wno-c++11-narrowing','-fno-fast-math','-ffp-contract=off','-I',str(s),'-I',str(compat_upstream),'-I',str(compat_soft),*map(str,compile_files),'-o',str(out)]
input_before=source_identity(r,compat_soft)
t=time.monotonic()
with (out.parent/'stdout.log').open('w') as so,(out.parent/'stderr.log').open('w') as se: result=subprocess.run(cmd,env=env,stdout=so,stderr=se,timeout=900)
input_after=source_identity(r,compat_soft)
input_stable=input_before==input_after
m=dict(compilerExitCode=result.returncode,buildInputStable=input_stable,inputBefore=input_before,inputAfter=input_after,exitCode=result.returncode if input_stable else 1,seconds=time.monotonic()-t,output=str(out),abi=1,bulletVersion=275,scalarBytes=4,vendoredUpstreamModified=False,**patches,files=[dict(path=str(q.relative_to(r)).replace('\\','/'),sha256=hashlib.sha256(q.read_bytes()).hexdigest()) for q in f+[n/'mmd_soft_plane_collision.h']])
m['compiledSourceFiles']=[dict(path=str(q.resolve()),sha256=hashlib.sha256(q.read_bytes()).hexdigest()) for q in compile_files]
m['profilerDisabled']=True
if not result.returncode and input_stable:m['sha256']=hashlib.sha256(out.read_bytes()).hexdigest()
(out.parent/'build-manifest.json').write_text(json.dumps(m,indent=2));print(json.dumps({k:v for k,v in m.items() if k not in ('files','inputBefore','inputAfter')}))
if result.returncode:print((out.parent/'stderr.log').read_text()[-9000:])
if not input_stable:print('Build rejected: a compiler input changed during compilation.')
raise SystemExit(m['exitCode'])

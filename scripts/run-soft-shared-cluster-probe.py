"""Build/run C05 against real bridge-owned clusters. Root owns serial invocation.

Reuse the already prepared standalone compatibility cache, including the original
MMD contact-expression overlay. Never mutate vendored inputs or prepare a cache
during another build. The probe includes mmd_bullet_soft.cpp in its own unit.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import time

from bullet_soft_sources import prepared_compile_files, source_identity

root = Path(__file__).resolve().parent.parent
parser = argparse.ArgumentParser()
parser.add_argument('--compiler', type=Path, default=root.parent / 'MMD932_20261014/research/Review/rebuild_dependencies/ziglang/zig.exe')
parser.add_argument('--cache', type=Path, default=root.parent / '.dev-cache/soft-core-completion-20261021')
parser.add_argument('--output', type=Path)
args = parser.parse_args()
cache = args.cache.resolve()
native = root / 'native/Mmd.Modern.Bullet.SoftBody'
bullet = root / 'third_party/bullet-2.75/src'
compat = cache / 'compat/upstream'
soft = compat / 'BulletSoftBody'
probe = root / 'tests/native/soft_shared_cluster_probe.cpp'
assert (cache / 'build-manifest.json').is_file(), 'Build the standalone bridge first to prepare the cache.'
assert 'STATIC_PLANE_PROXYTYPE' in (soft / 'btSparseSDF.h').read_text()
solver = compat / 'BulletDynamics/ConstraintSolver/btSequentialImpulseConstraintSolver.cpp'
assert '(btScalar(-1.) / infoGlobal.m_timeStep) * (penetration * infoGlobal.m_erp)' in solver.read_text(), 'Missing verified float32 solver overlay.'
original_sources = sorted(path for directory in ('BulletDynamics', 'BulletCollision', 'LinearMath')
    for path in (bullet / directory).rglob('*.cpp') if path.name != 'Bullet-C-API.cpp')
original_sources += sorted((native / 'upstream/BulletSoftBody').glob('*.cpp')) + [probe]
sources = prepared_compile_files(original_sources, native, compat, soft)
executable = cache / 'soft_shared_cluster_probe.exe'
command = [str(args.compiler.resolve()), 'c++', '-std=c++17', '-O2', '-DNDEBUG',
    '-DBT_HAS_ALIGNED_ALLOCATOR', '-Wno-nullability-completeness', '-Wno-c++11-narrowing',
    '-fno-fast-math', '-ffp-contract=off', '-I', str(bullet), '-I', str(compat),
    '-I', str(soft), '-I', str(native), *map(str, sources), '-o', str(executable)]
environment = dict(os.environ, TEMP=str(cache), TMP=str(cache),
    ZIG_GLOBAL_CACHE_DIR=str(cache / 'zig-global'), ZIG_LOCAL_CACHE_DIR=str(cache / 'zig-local'))
before = source_identity(root, soft)
probe_before = hashlib.sha256(probe.read_bytes()).hexdigest()
started = time.monotonic()
with (cache / 'shared-cluster-probe.stdout.log').open('w') as stdout, (cache / 'shared-cluster-probe.stderr.log').open('w') as stderr:
    build = subprocess.run(command, env=environment, stdout=stdout, stderr=stderr, timeout=900)
stable = before == source_identity(root, soft) and probe_before == hashlib.sha256(probe.read_bytes()).hexdigest()
report = dict(buildExitCode=build.returncode, buildInputStable=stable, seconds=time.monotonic() - started,
    probeSha256=probe_before, bridgeSha256=hashlib.sha256((native / 'mmd_bullet_soft.cpp').read_bytes()).hexdigest(),
    compiledSourceFiles=[dict(path=str(path.resolve()), sha256=hashlib.sha256(path.read_bytes()).hexdigest()) for path in sources],
    solverOverlaySha256=hashlib.sha256(solver.read_bytes()).hexdigest())
if build.returncode == 0 and stable:
    run = subprocess.run([str(executable)], capture_output=True, text=True, timeout=30)
    report.update(runExitCode=run.returncode, stdout=run.stdout, stderr=run.stderr)
    if run.returncode == 0:
        report['evidence'] = json.loads(run.stdout)
elif build.returncode:
    report['buildError'] = (cache / 'shared-cluster-probe.stderr.log').read_text()[-9000:]
else:
    report['buildError'] = 'A compiler input changed during compilation.'
output = args.output or cache / 'shared-cluster-native-probe.json'
output.parent.mkdir(parents=True, exist_ok=True)
output.write_text(json.dumps(report, indent=2), encoding='utf-8')
print(json.dumps({key: value for key, value in report.items() if key != 'compiledSourceFiles'}, indent=2))
raise SystemExit(report.get('runExitCode', build.returncode or 1))

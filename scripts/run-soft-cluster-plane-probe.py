"""Build/run the native C03 probe after build-bullet-soft.py prepared its cache.

Root owns invocation; this script does not modify vendored Bullet sources.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import time

root = Path(__file__).resolve().parent.parent
parser = argparse.ArgumentParser()
parser.add_argument('--compiler', type=Path, default=root.parent / 'MMD932_20261014/research/Review/rebuild_dependencies/ziglang/zig.exe')
parser.add_argument('--cache', type=Path, default=root.parent / '.dev-cache/soft-core-completion-20261021')
args = parser.parse_args()
cache = args.cache.resolve()
native = root / 'native/Mmd.Modern.Bullet.SoftBody'
bullet = root / 'third_party/bullet-2.75/src'
compat = cache / 'compat/upstream'
probe = root / 'tests/native/soft_cluster_plane_probe.cpp'
assert (cache / 'build-manifest.json').is_file(), 'Build the soft bridge first to prepare the documented compatibility cache.'
assert 'STATIC_PLANE_PROXYTYPE' in (compat / 'BulletSoftBody/btSparseSDF.h').read_text()
sources = sorted(path for directory in ('BulletDynamics', 'BulletCollision', 'LinearMath')
                 for path in (bullet / directory).rglob('*.cpp') if path.name != 'Bullet-C-API.cpp')
sources += sorted((compat / 'BulletSoftBody').glob('*.cpp')) + [probe]
output = cache / 'soft_cluster_plane_probe.exe'
command = [str(args.compiler.resolve()), 'c++', '-std=c++17', '-O2', '-DNDEBUG',
           '-DBT_HAS_ALIGNED_ALLOCATOR', '-Wno-nullability-completeness', '-Wno-c++11-narrowing',
           '-fno-fast-math', '-ffp-contract=off', '-I', str(bullet), '-I', str(compat),
           '-I', str(compat / 'BulletSoftBody'), '-I', str(native), *map(str, sources), '-o', str(output)]
environment = dict(os.environ, TEMP=str(cache), TMP=str(cache),
                   ZIG_GLOBAL_CACHE_DIR=str(cache / 'zig-global'), ZIG_LOCAL_CACHE_DIR=str(cache / 'zig-local'))
started = time.monotonic()
with (cache / 'cluster-probe-build.stdout.log').open('w') as stdout, (cache / 'cluster-probe-build.stderr.log').open('w') as stderr:
    build = subprocess.run(command, env=environment, stdout=stdout, stderr=stderr, timeout=900)
report = dict(buildExitCode=build.returncode, seconds=time.monotonic() - started,
              helperSha256=hashlib.sha256((native / 'mmd_soft_plane_collision.h').read_bytes()).hexdigest(),
              probeSha256=hashlib.sha256(probe.read_bytes()).hexdigest())
if build.returncode == 0:
    run = subprocess.run([str(output)], capture_output=True, text=True, timeout=30)
    report.update(runExitCode=run.returncode, stdout=run.stdout, stderr=run.stderr)
    if run.returncode == 0:
        report['evidence'] = json.loads(run.stdout)
else:
    report['buildError'] = (cache / 'cluster-probe-build.stderr.log').read_text()[-9000:]
(cache / 'cluster-plane-native-probe.json').write_text(json.dumps(report, indent=2))
print(json.dumps(report, indent=2))
raise SystemExit(report.get('runExitCode', build.returncode))

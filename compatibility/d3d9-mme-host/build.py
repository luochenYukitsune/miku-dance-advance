#!/usr/bin/env python3
"""Build the research-only x64 D3D9/MME static archive from local pinned sources.

Usage: python build.py --zig <absolute zig.exe> --output <new absolute directory>
No download, link, executable launch, GPU call, or product registration is performed.
The validation-only no_ui.cpp is supplied for the final validation host to compile
and link separately; it is intentionally outside the 23-member static archive.
"""
import argparse
import concurrent.futures
import datetime
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import subprocess
import sys
import time

HERE = Path(__file__).resolve().parent


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def local_path(root, relative):
    part = PurePosixPath(relative)
    if part.is_absolute() or not part.parts or any(p in ('..', '.') for p in part.parts) or ':' in relative or '\\' in relative:
        raise ValueError('Invalid bundle-relative path: ' + relative)
    target = root.joinpath(*part.parts).resolve()
    if not target.is_relative_to(root.resolve()):
        raise ValueError('Path escapes its root: ' + relative)
    return target


def verify_inputs():
    manifest = json.loads((HERE / 'manifest.json').read_text(encoding='utf-8'))
    if manifest.get('schemaVersion') != 1:
        raise ValueError('Unsupported manifest schema')
    listed = set()
    for entry in manifest['files']:
        path = entry['path']
        if path in listed:
            raise ValueError('Duplicate input: ' + path)
        listed.add(path)
        source = local_path(HERE, path)
        if not source.is_file() or source.stat().st_size != entry['bytes'] or digest(source) != entry['sha256']:
            raise ValueError('Bundle input integrity failure: ' + path)
    actual = {p.relative_to(HERE).as_posix() for p in HERE.rglob('*') if p.is_file() and '__pycache__' not in p.parts and p.name != 'manifest.json'}
    if actual != listed:
        raise ValueError('Unlisted or missing bundle files: ' + repr(sorted(actual ^ listed)))
    return manifest, json.loads((HERE / 'UPSTREAM.json').read_text(encoding='utf-8'))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--zig', required=True, type=Path, help='Absolute path to the pinned local Zig executable')
    parser.add_argument('--output', required=True, type=Path, help='New absolute directory owned by this build; must not already exist')
    parser.add_argument('--global-cache', type=Path, help='Absolute shared Zig global cache; default: <output>/cache/global')
    parser.add_argument('--jobs', type=int, default=min(4, os.cpu_count() or 1), help='Concurrent compiler processes (1..16; default at most 4)')
    args = parser.parse_args()
    if not args.output.is_absolute() or not args.zig.is_absolute() or (args.global_cache and not args.global_cache.is_absolute()):
        parser.error('--zig, --output and --global-cache must use absolute paths')
    if not 1 <= args.jobs <= 16:
        parser.error('--jobs must be between 1 and 16')
    output = args.output.resolve()
    if output.exists() or output == HERE or output.is_relative_to(HERE) or HERE.is_relative_to(output):
        parser.error('--output must be a new directory outside the source bundle')
    zig = args.zig.resolve()
    if not zig.is_file():
        parser.error('--zig must name a local file')
    global_cache = args.global_cache.resolve() if args.global_cache else output / 'cache/global'
    if global_cache == HERE or global_cache.is_relative_to(HERE) or HERE.is_relative_to(global_cache):
        parser.error('--global-cache must not overlap the source bundle')
    output.mkdir(parents=True, exist_ok=False)
    summary = {
        'schemaVersion': 1, 'status': 'preflight', 'scope': 'static research foundation, not product-loaded',
        'startedUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
        'bundle': str(HERE), 'manifestSha256': digest(HERE / 'manifest.json'),
        'tool': str(zig), 'toolSha256': digest(zig), 'jobs': args.jobs,
        'output': str(output), 'commands': [],
        'limits': ['Static compilation/archive only; no link or execution',
                   'Validation instrumentation requires a separately linked validation_only/no_ui.cpp',
                   'The final host supplies MMD Exp* adapters and d3dx9_43/system link dependencies',
                   'No GUI, installer, product registration, physics, or rendering-engine selection is changed'],
    }
    try:
        manifest, configuration = verify_inputs()
        if summary['toolSha256'] != configuration['build']['zigSha256']:
            raise ValueError('Zig SHA256 differs from the pinned tool')
        summary['verifiedInputCount'] = len(manifest['files'])
        source_root = output / 'src'
        validation = output / 'validation_only'
        source_root.mkdir()
        validation.mkdir()
        # A single merged tree makes quoted includes select the altered headers
        # consistently even from otherwise unmodified upstream translation units.
        for layer in ('upstream', 'ALTERED'):
            prefix = layer + '/'
            for entry in manifest['files']:
                if entry['path'].startswith(prefix):
                    relative = entry['path'][len(prefix):]
                    target = local_path(source_root, relative)
                    target.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copyfile(local_path(HERE, entry['path']), target)
        for entry in manifest['files']:
            if entry['path'].startswith('validation_only/'):
                target = local_path(output, entry['path'])
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(local_path(HERE, entry['path']), target)
        local_cache = output / 'cache/local'
        local_cache.mkdir(parents=True)
        global_cache.mkdir(parents=True, exist_ok=True)
        environment = os.environ.copy()
        environment['ZIG_LOCAL_CACHE_DIR'] = str(local_cache)
        environment['ZIG_GLOBAL_CACHE_DIR'] = str(global_cache)
        summary['cache'] = {'local': str(local_cache), 'global': str(global_cache)}
        obj = output / 'obj'
        logs = output / 'logs'
        obj.mkdir()
        logs.mkdir()
        base = source_root / 'third_party/mmeffect'
        includes = [validation, base / 'include', base / 'src', base / 'src/mmhack', base / 'src/mmeffect', source_root / 'include']
        flags = configuration['build']['flags']
        include_flags = [str(value) for include in includes for value in ('-I', include)]
        summary['flags'] = flags
        summary['includes'] = [str(p) for p in includes]
        units = configuration['build']['translationUnits']
        if len(units) != 23 or len({u['object'] for u in units}) != 23:
            raise ValueError('Expected 23 unique archive members')
        objects = []
        plans = []
        for unit in units:
            source = local_path(output, unit['source'])
            target = local_path(obj, unit['object'])
            command = [str(zig), 'c++', *flags, *include_flags]
            if unit.get('validationInstrumentation'):
                command += ['-include', str(validation / 'no_ui.h')]
            command += ['-c', str(source), '-o', str(target)]
            record = {'argv': command, 'log': str(logs / (target.stem + '.log')),
                      'source': str(source), 'sourceSha256': digest(source), 'object': str(target)}
            summary['commands'].append(record)
            objects.append(target)
            plans.append(record)

        def compile_unit(record):
            started = time.monotonic()
            with Path(record['log']).open('wb') as stream:
                result = subprocess.run(record['argv'], cwd=output, env=environment, stdout=stream, stderr=subprocess.STDOUT)
            record.update(returncode=result.returncode, seconds=round(time.monotonic() - started, 3))
            if result.returncode == 0:
                record['objectSha256'] = digest(Path(record['object']))
            return result.returncode

        with concurrent.futures.ThreadPoolExecutor(max_workers=args.jobs) as executor:
            results = list(executor.map(compile_unit, plans))
        if any(results):
            raise RuntimeError('Compilation failed; see individual logs')
        archive = output / 'MMEffectResearch.a'
        archive_record = {'argv': [str(zig), 'ar', 'rcs', str(archive), *map(str, objects)], 'log': str(logs / 'archive.log')}
        summary['commands'].append(archive_record)
        with Path(archive_record['log']).open('wb') as stream:
            result = subprocess.run(archive_record['argv'], cwd=output, env=environment, stdout=stream, stderr=subprocess.STDOUT)
        archive_record['returncode'] = result.returncode
        if result.returncode:
            raise RuntimeError('Archive creation failed')
        summary.update(status='archive_created_not_linked_or_run', archive=str(archive), archiveSha256=digest(archive), archiveMemberCount=23)
    except Exception as exception:
        summary.update(status='failed', error=str(exception))
        print(str(exception), file=sys.stderr)
    finally:
        summary['finishedUtc'] = datetime.datetime.now(datetime.timezone.utc).isoformat()
        report = output / 'summary.json'
        report.write_text(json.dumps(summary, indent=2) + '\n', encoding='utf-8')
        print(report)
    return 0 if summary['status'] == 'archive_created_not_linked_or_run' else 1


if __name__ == '__main__':
    sys.exit(main())

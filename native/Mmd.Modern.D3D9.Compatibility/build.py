#!/usr/bin/env python3
"""Compile/link the Windows x64 capture/preview session DLL from pinned local inputs.

No downloads, target execution, GPU calls, installation or product registration.
Use an existing host build made by compatibility/d3d9-mme-host/build.py.
"""
import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import subprocess
import sys

HERE = Path(__file__).resolve().parent
HOST = HERE.parents[1] / 'compatibility' / 'd3d9-mme-host'
PINNED_ARCHIVE = '4bd74d68f7158bdc481fc9ebf999161ae63f9c2f486f4ec9264cbf459596bfcc'
PINNED_MANIFEST = '31326c0df052d1f7da8f21e8560ee647bc1627da4c482cd51f65a292e26655c9'


def digest(path):
    h = hashlib.sha256()
    with path.open('rb') as source:
        for block in iter(lambda: source.read(1024 * 1024), b''):
            h.update(block)
    return h.hexdigest()


def relative(root, name):
    p = PurePosixPath(name)
    if p.is_absolute() or not p.parts or any(x in ('.', '..') for x in p.parts) or ':' in name or '\\' in name:
        raise ValueError('Invalid host-relative path: ' + name)
    result = root.joinpath(*p.parts).resolve()
    if not result.is_relative_to(root.resolve()):
        raise ValueError('Path escapes host root: ' + name)
    return result


def verify_host(build, zig):
    if digest(HOST / 'manifest.json') != PINNED_MANIFEST:
        raise ValueError('Host source manifest differs from the verified pin')
    manifest = json.loads((HOST / 'manifest.json').read_text(encoding='utf-8'))
    configuration = json.loads((HOST / 'UPSTREAM.json').read_text(encoding='utf-8'))
    if manifest.get('schemaVersion') != 1:
        raise ValueError('Unsupported host manifest schema')
    listed = set()
    merged = {}
    for entry in manifest['files']:
        name = entry['path']
        if name in listed:
            raise ValueError('Duplicate host input: ' + name)
        listed.add(name)
        source = relative(HOST, name)
        if not source.is_file() or source.stat().st_size != entry['bytes'] or digest(source) != entry['sha256']:
            raise ValueError('Host bundle integrity failure: ' + name)
    actual = {p.relative_to(HOST).as_posix() for p in HOST.rglob('*') if p.is_file() and '__pycache__' not in p.parts and p.name != 'manifest.json'}
    if actual != listed:
        raise ValueError('Host source bundle contains missing/unlisted inputs')
    for layer in ('upstream', 'ALTERED'):
        for entry in manifest['files']:
            prefix = layer + '/'
            if entry['path'].startswith(prefix):
                merged['src/' + entry['path'][len(prefix):]] = entry['sha256']
    for name, expected in merged.items():
        if digest(relative(build, name)) != expected:
            raise ValueError('Merged host build source/header differs: ' + name)
    summary = json.loads((build / 'summary.json').read_text(encoding='utf-8'))
    archive = build / 'MMEffectResearch.a'
    if (summary.get('status') != 'archive_created_not_linked_or_run'
            or summary.get('manifestSha256') != PINNED_MANIFEST
            or summary.get('archiveMemberCount') != 23
            or summary.get('archiveSha256') != PINNED_ARCHIVE
            or digest(archive) != PINNED_ARCHIVE):
        raise ValueError('Host archive identity/status does not match the verified pin')
    if digest(zig) != configuration['build']['zigSha256']:
        raise ValueError('Zig executable differs from the pinned tool')
    return configuration, archive, len(listed), len(merged)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--zig', type=Path, required=True)
    parser.add_argument('--host-build', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--global-cache', type=Path)
    args = parser.parse_args()
    for value in (args.zig, args.host_build, args.output, args.global_cache):
        if value is not None and not value.is_absolute():
            parser.error('all paths must be absolute')
    zig, host_build, output = args.zig.resolve(), args.host_build.resolve(), args.output.resolve()
    if not zig.is_file() or not host_build.is_dir():
        parser.error('--zig and --host-build must name existing local inputs')
    protected = [HERE, HOST, host_build]
    if output.exists() or any(output == p or output.is_relative_to(p) or p.is_relative_to(output) for p in protected):
        parser.error('--output must be new and must not overlap input source/build directories')
    global_cache = args.global_cache.resolve() if args.global_cache else output / 'cache' / 'global'
    if any(global_cache == p or global_cache.is_relative_to(p) or p.is_relative_to(global_cache) for p in protected):
        parser.error('--global-cache must not overlap source/build input directories')
    output.mkdir(parents=True, exist_ok=False)
    report = {
        'schemaVersion': 1, 'status': 'preflight',
        'scope': 'Windows x64 capture/preview session, including scope4 FRM5/VIS6 original passes and FRM6/VIS7 media; no product acceptance implied',
        'startedUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
        'source': str(HERE), 'hostBuild': str(host_build), 'tool': str(zig),
        'commands': [],
        'limits': ['Not executed by this builder; no GPU validation implied',
                   'D3DX9_43 deployment is not validated or bundled',
                   'Native child-window Present; editor production factory/installer and complete EMM/dependency integration remain outside this builder',
                   'Typed accessory source/parent/material transport is implemented; its GPU acceptance is recorded separately, not implied by compilation',
                   'Scope4 FRM5/VIS6 supplies explicit original self-shadow, ground and outline passes; CLS v2 also supplies the original fixed-function model path; older packets cannot request these capabilities',
                   'Scope4 FRM6/VIS7 appends bounded same-sample decoded picture/video RGBA and original background rectangles; original 1024-square upload/filter and pre-model MME DrawPrimitive are used, with the established Classic frame-head blend policy',
                   'Bounded decoded picture/video background layers are supported only by explicit scope4 FRM6/VIS7; audio, accessory screen/AVI textures and transparent output backgrounds remain unsupported',
                   'GPU sampling, Present and legacy pixel verification are outside this build-only script',
                   'One active capture OR preview session; create/capture/present/resize/destroy on one fixed OS thread'],
    }
    try:
        configuration, archive, count, merged_count = verify_host(host_build, zig)
        report.update(hostInputCount=count, mergedHostInputCount=merged_count,
                      hostManifestSha256=PINNED_MANIFEST, hostArchiveSha256=PINNED_ARCHIVE,
                      toolSha256=digest(zig))
        sources = ['mda_d3d9_session.h', 'session.cpp', 'mda_d3d9_dependency.h', 'dependency.cpp', 'dependency_compiler_api.hpp', 'mda_d3d9_dependency_roots.h', 'dependency_roots.cpp', 'object_fx.hpp', 'effect_resource_map.hpp', 'owner_window.hpp', 'frame_packet.hpp', 'visual_packet.hpp', 'surface.fx', 'build.py']
        report['sourceInputs'] = [{'path': str(HERE / name), 'sha256': digest(HERE / name)} for name in sources]
        # Embed the project-owned ordinary shader. The separate opt-in object FX
        # configuration loads only its explicitly leased immutable file paths.
        generated = output / 'generated'
        generated.mkdir()
        shader = (HERE / 'surface.fx').read_text(encoding='utf-8-sig')
        delimiter = 'MDA9_SURFACE'
        if ')' + delimiter + '"' in shader:
            raise ValueError('Shader collides with the embedding delimiter')
        shader_header = generated / 'surface_source.hpp'
        shader_header.write_text('#pragma once\nstatic constexpr char kSurfaceSource[] = R"' + delimiter + '(' + shader + ')' + delimiter + '";\n', encoding='utf-8')
        local_cache = output / 'cache' / 'local'
        local_cache.mkdir(parents=True)
        global_cache.mkdir(parents=True, exist_ok=True)
        environment = os.environ.copy()
        environment['ZIG_LOCAL_CACHE_DIR'] = str(local_cache)
        environment['ZIG_GLOBAL_CACHE_DIR'] = str(global_cache)
        base = host_build / 'src' / 'third_party' / 'mmeffect'
        includes = [HERE, generated, base / 'include', base / 'src', base / 'src' / 'mmhack', base / 'src' / 'mmeffect', host_build / 'src' / 'include']
        flags = configuration['build']['flags']
        include_flags = [str(value) for directory in includes for value in ('-I', directory)]
        obj = output / 'session.o'
        dependency_obj = output / 'dependency.o'
        dependency_roots_obj = output / 'dependency_roots.o'
        dll = output / 'Mmd.Modern.D3D9.Compatibility.dll'

        def run(argv, name):
            log = output / (name + '.log')
            record = {'argv': argv, 'log': str(log)}
            report['commands'].append(record)
            with log.open('wb') as stream:
                completed = subprocess.run(argv, cwd=output, env=environment, stdout=stream, stderr=subprocess.STDOUT)
            record['returncode'] = completed.returncode
            if completed.returncode:
                raise RuntimeError(name + ' failed; see ' + str(log))

        run([str(zig), 'c++', *flags, *include_flags, '-c', str(HERE / 'session.cpp'), '-o', str(obj)], 'compile')
        report['objectSha256'] = digest(obj)
        run([str(zig), 'c++', *flags, *include_flags, '-c', str(HERE / 'dependency.cpp'), '-o', str(dependency_obj)], 'compile-dependency')
        report['dependencyObjectSha256'] = digest(dependency_obj)
        run([str(zig), 'c++', *flags, *include_flags, '-c', str(HERE / 'dependency_roots.cpp'), '-o', str(dependency_roots_obj)], 'compile-dependency-roots')
        report['dependencyRootsObjectSha256'] = digest(dependency_roots_obj)
        run([str(zig), 'c++', *flags, '-shared', str(obj), str(dependency_obj), str(dependency_roots_obj), '-Wl,--whole-archive', str(archive), '-Wl,--no-whole-archive',
             '-ld3dx9_43', '-ld3d9', '-luser32', '-lgdi32', '-lgdiplus', '-lshell32',
             '-lole32', '-lwinmm', '-lshlwapi', '-ladvapi32', '-o', str(dll)], 'link')
        report.update(status='dll_linked_not_executed', dll=str(dll), dllSha256=digest(dll),
                      generatedShaderSha256=digest(shader_header))
    except Exception as exception:
        report.update(status='failed', error=str(exception))
        print(str(exception), file=sys.stderr)
    finally:
        report['finishedUtc'] = datetime.datetime.now(datetime.timezone.utc).isoformat()
        summary_path = output / 'summary.json'
        summary_path.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
        print(summary_path)
    return 0 if report['status'] == 'dll_linked_not_executed' else 1


if __name__ == '__main__':
    sys.exit(main())

"""Build/run original CPU VIS1 v2/v3 fixtures against product readers; no D3D."""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys

ZIG_SHA256 = '086ce9d47ba42f33a514e1a6e04eb1d4a8fa1d75e0868e0213caad447c91e864'


def sha(path):
    digest = hashlib.sha256()
    with path.open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(chunk)
    return digest.hexdigest()


def utc():
    return datetime.now(timezone.utc).isoformat()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--zig', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--global-cache', required=True, type=Path)
    args = parser.parse_args()
    if os.name != 'nt':
        parser.error('Windows is required')
    for name in ('zig', 'output', 'global_cache'):
        if not getattr(args, name).is_absolute():
            parser.error(f'--{name.replace("_", "-")} must be an absolute path')
    if args.output.exists() or args.output.is_symlink():
        parser.error('--output must be a new directory')
    try:
        zig = args.zig.resolve(strict=True)
        output = args.output.resolve()
        global_cache = args.global_cache.resolve()
        if not zig.is_file():
            parser.error('--zig must be a file')
        if global_cache.exists() and not global_cache.is_dir():
            parser.error('--global-cache must be a directory')
        zig_sha = sha(zig)
        if zig_sha != ZIG_SHA256:
            parser.error(f'--zig SHA256 mismatch: {zig_sha}')
        script = Path(__file__).resolve(strict=True)
        protected = (script.parents[3], zig.parent)
        if any(candidate == root or candidate.is_relative_to(root) or root.is_relative_to(candidate)
               for candidate in (output, global_cache) for root in protected):
            parser.error('output/cache must not overlap the source repository or compiler directory')
        source = script.with_name('visual_uv_parser.cpp')
        native = script.parents[3] / 'native/Mmd.Modern.D3D9.Compatibility'
        frame_header = native / 'frame_packet.hpp'
        visual_header = native / 'visual_packet.hpp'
        record = dict(schemaVersion=1, status='running', startedUtc=utc(),
                      scope='CPU product FRM1/VIS1 readers with CryptoAPI SHA only; no D3D DLL, GPU, window, product process, installer or user assets',
                      zig=dict(path=str(zig), sha256=zig_sha), runnerSha256=sha(script),
                      sourceSha256=sha(source), framePacketHeaderSha256=sha(frame_header),
                      visualPacketHeaderSha256=sha(visual_header),
                      globalCache=str(global_cache), commands=[])
        output.mkdir(parents=True, exist_ok=False)
    except OSError as exception:
        parser.error(str(exception))
    env = os.environ.copy()
    env['ZIG_GLOBAL_CACHE_DIR'] = str(global_cache)
    env['ZIG_LOCAL_CACHE_DIR'] = str(output / 'cache')
    exe = output / 'visual_uv_parser.exe'
    commands = [('compile', [str(zig), 'c++', '-target', 'x86_64-windows-gnu',
                             '-std=c++17', '-O2', '-I', str(native), str(source),
                             '-ladvapi32', '-o', str(exe)]), ('run', [str(exe)])]
    exit_code = 1
    try:
        for label, argv in commands:
            log = output / (label + '.log')
            event = dict(name=label, argv=argv, cwd=str(output), log=log.name,
                         creationFlags='CREATE_NO_WINDOW', exitCode=None)
            record['commands'].append(event)
            with log.open('xb') as stream:
                result = subprocess.run(argv, cwd=output, env=env, stdout=stream,
                                        stderr=subprocess.STDOUT,
                                        creationflags=subprocess.CREATE_NO_WINDOW)
            event.update(exitCode=result.returncode, logSha256=sha(log))
            exit_code = result.returncode
            if exit_code:
                break
        record['status'] = 'passed' if exit_code == 0 else 'failed'
    except Exception as exception:
        record.update(status='failed', error=repr(exception))
        exit_code = 1
    finally:
        if exe.is_file():
            record['exeSha256'] = sha(exe)
        record['fixtures'] = [dict(path=p.name, bytes=p.stat().st_size, sha256=sha(p))
                              for p in sorted(output.glob('original-*.packet'))]
        record['completedUtc'] = utc()
        target = output / 'execution.json'
        target.write_text(json.dumps(record, indent=2) + '\n', encoding='utf-8')
        print(json.dumps(dict(status=record['status'], report=str(target))))
    return exit_code


if __name__ == '__main__':
    sys.exit(main())

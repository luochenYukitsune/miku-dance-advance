"""Explicit Windows x64 D3D9 session contract run using original tiny diagnostics.

Creates a hidden native device only when invoked with --dll and --output.
No build, installed-application discovery, user model, large packet or clock is
used. Output images are DIAGNOSTIC geometry, not normal-material/MME acceptance.
"""
from __future__ import annotations

import argparse
import ctypes as C
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import subprocess
import sys
import threading

import abi
from generate import PURPOSE, digest, generate

CANARY = 0xA7
DEFAULT_ERROR = object()


def utc():
    return datetime.now(timezone.utc).isoformat()


def fields(value):
    return {name: getattr(value, name) for name, _ in value._fields_
            if name != 'reserved'}


class Run:
    def __init__(self, args, report):
        self.args, self.report = args, report
        self.lib, self.handle = None, C.c_uint64()
        self.count, self.total, self.generation = 0, 0, 0
        self.images, self.reference_images = {}, {}

    def call(self, label, name, *arguments, want=abi.OK, error=DEFAULT_ERROR):
        storage = C.create_string_buffer(2048)
        if error is DEFAULT_ERROR:
            error = abi.init(abi.Error, utf8=C.addressof(storage), capacity=len(storage))
            normal_error = True
        else:
            normal_error = False
        status = getattr(self.lib, name)(*arguments, C.byref(error) if error is not None else None)
        event = dict(case=label, operation=name, status=status, expectedStatus=want,
                     error=storage.value.decode('utf-8', 'strict') if normal_error else None)
        if error is not None:
            event['errorResult'] = dict(status=error.status, requiredBytes=error.requiredBytes,
                                        capacity=error.capacity)
        self.report['events'].append(event)
        assert status == want, event
        if normal_error:
            assert error.status == status, event
            assert error.requiredBytes == len(storage.value) + 1, event
        return event

    def query(self):
        info = abi.init(abi.Abi)
        self.call('query', 'QueryAbi', C.byref(info))
        assert (info.abiVersion, info.pointerBits, info.maxWidth, info.maxHeight,
                info.supportedFeatures) == (1, 64, 4096, 4096, 1)
        self.report['abi'] = fields(info)
        for label, changes in [('query-version', dict(version=99)), ('query-size', dict(size=4))]:
            bad = abi.init(abi.Abi, **changes)
            self.call(label, 'QueryAbi', C.byref(bad), want=abi.ABI_MISMATCH)
        self.call('query-null', 'QueryAbi', None, want=abi.INVALID_ARGUMENT)
        self.call('query-optional-error', 'QueryAbi', C.byref(info), error=None)
        class Extended(C.Structure):
            _fields_ = [('value', abi.Abi), ('tail', C.c_ubyte * 16)]
        extra = Extended(abi.init(abi.Abi), (C.c_ubyte * 16)(*[CANARY] * 16))
        extra.value.size = C.sizeof(extra)
        self.call('query-larger-struct', 'QueryAbi', C.cast(C.byref(extra), C.POINTER(abi.Abi)))
        assert bytes(extra.tail) == bytes([CANARY]) * 16
        bad_error = abi.init(abi.Error, capacity=1)
        self.call('error-null-storage-positive-capacity', 'QueryAbi', C.byref(info),
                  want=abi.INVALID_ARGUMENT, error=bad_error)
        bad_error = abi.init(abi.Error, version=99)
        self.call('error-invalid-version', 'QueryAbi', C.byref(info),
                  want=abi.ABI_MISMATCH, error=bad_error)
        short = (C.c_ubyte * 12)(*[CANARY] * 12)
        short_error = abi.init(abi.Error, utf8=C.addressof(short), capacity=5)
        event = self.call('error-short-buffer-preserves-status', 'QueryAbi', None,
                          want=abi.INVALID_ARGUMENT, error=short_error)
        assert short_error.requiredBytes > 5 and short_error.status == abi.INVALID_ARGUMENT
        assert 0 in short[:5] and bytes(short[5:]) == bytes([CANARY]) * 7
        event['truncatedUtf8'] = bytes(short[:5]).split(b'\0', 1)[0].decode('utf-8', 'strict')

    def create(self, label):
        desc = abi.init(abi.Create, width=32, height=32)
        self.call(label, 'Create', C.byref(desc), C.byref(self.handle))
        assert self.handle.value
        self.count, self.generation = 0, 0

    def create_rejections(self):
        for label, changes, status in [
                ('create-zero-width', dict(width=0), abi.INVALID_ARGUMENT),
                ('create-oversize', dict(height=4097), abi.INVALID_ARGUMENT),
                ('create-reserved', dict(reserved=1), abi.INVALID_ARGUMENT),
                ('create-version', dict(version=99), abi.ABI_MISMATCH)]:
            desc = abi.init(abi.Create, width=32, height=32)
            for name, value in changes.items():
                setattr(desc, name, value)
            handle = C.c_uint64(123)
            self.call(label, 'Create', C.byref(desc), C.byref(handle), want=status)
            assert handle.value == 0
        desc = abi.init(abi.Create, width=32, height=32)
        self.call('create-null-output', 'Create', C.byref(desc), None, want=abi.INVALID_ARGUMENT)
        handle = C.c_uint64(123)
        self.call('create-null-description', 'Create', None, C.byref(handle), want=abi.INVALID_ARGUMENT)
        assert handle.value == 0

    def payload(self, fixture):
        buffers = []
        for key in ('frame', 'visual'):
            data = (self.args.output / 'fixtures' / fixture[key]['path']).read_bytes()
            assert digest(data) == fixture[key]['sha256'] and len(data) == fixture[key]['bytes']
            buffers.append((C.c_ubyte * len(data)).from_buffer_copy(data))
        width, height = fixture['width'], fixture['height']
        stride = width * 4 + 12
        required = (height - 1) * stride + width * 4
        output = (C.c_ubyte * (height * stride + 32))(*[CANARY] * (height * stride + 32))
        request = abi.init(abi.Capture, frameBytes=C.addressof(buffers[0]), frameLength=len(buffers[0]),
                           visualBytes=C.addressof(buffers[1]), visualLength=len(buffers[1]),
                           requiredFeatures=1, flags=1, renderOrder=1, width=width, height=height,
                           rgba=C.addressof(output), rgbaCapacity=required, rgbaStride=stride)
        return request, output, buffers

    def live_rejections(self, fixture):
        desc = abi.init(abi.Create, width=32, height=32)
        second = C.c_uint64(123)
        self.call('one-live-session', 'Create', C.byref(desc), C.byref(second), want=abi.BUSY)
        assert second.value == 0
        resize = abi.init(abi.Resize, width=64, height=32)
        self.call('stale-handle-resize', 'Resize', self.handle.value + 123, C.byref(resize),
                  want=abi.INVALID_HANDLE)
        for label, width, height in [('resize-zero', 0, 32), ('resize-oversize', 32, 4097)]:
            bad = abi.init(abi.Resize, width=width, height=height)
            self.call(label, 'Resize', self.handle.value, C.byref(bad), want=abi.INVALID_ARGUMENT)
        request, output, keepalive = self.payload(fixture)
        result = abi.init(abi.Result)
        initial = bytes(output)
        self.call('stale-handle-capture', 'Capture', self.handle.value + 123,
                  C.byref(request), C.byref(result), want=abi.INVALID_HANDLE)
        self.call('capture-null-description', 'Capture', self.handle.value,
                  None, C.byref(result), want=abi.INVALID_ARGUMENT)
        self.call('capture-null-result', 'Capture', self.handle.value,
                  C.byref(request), None, want=abi.INVALID_ARGUMENT)
        for label, changes in [('capture-result-version', dict(version=99)),
                               ('capture-result-size', dict(size=4))]:
            bad_result = abi.init(abi.Result, **changes)
            self.call(label, 'Capture', self.handle.value, C.byref(request),
                      C.byref(bad_result), want=abi.ABI_MISMATCH)
        assert bytes(output) == initial
        errors = []
        def wrong_thread():
            try:
                self.call('owner-thread-capture', 'Capture', self.handle.value, C.byref(request),
                          C.byref(result), want=abi.WRONG_THREAD)
                self.call('owner-thread-resize', 'Resize', self.handle.value, C.byref(resize),
                          want=abi.WRONG_THREAD)
                self.call('owner-thread-destroy', 'Destroy', self.handle.value, want=abi.WRONG_THREAD)
            except BaseException as exception:
                errors.append(exception)
        worker = threading.Thread(target=wrong_thread, daemon=True)
        worker.start()
        worker.join(timeout=10)
        assert not worker.is_alive() and not errors, errors
        assert bytes(output) == initial
        cases = [('unsupported-mme', 'requiredFeatures', 33, abi.UNSUPPORTED),
                 ('unsupported-unknown-feature', 'requiredFeatures', 1 | (1 << 40), abi.UNSUPPORTED),
                 ('explicit-isolated-scope-required', 'flags', 0, abi.UNSUPPORTED),
                 ('invalid-render-order', 'renderOrder', 99, abi.INVALID_ARGUMENT),
                 ('short-buffer', 'rgbaCapacity', request.rgbaCapacity - 1, abi.BUFFER_TOO_SMALL),
                 ('short-stride', 'rgbaStride', request.width * 4 - 1, abi.INVALID_ARGUMENT),
                 ('overflow-stride', 'rgbaStride', (1 << 64) - 1, abi.INVALID_ARGUMENT),
                 ('capture-version', 'version', 99, abi.ABI_MISMATCH),
                 ('short-frame-span', 'frameLength', 4, abi.INVALID_ARGUMENT),
                 ('short-visual-span', 'visualLength', 4, abi.INVALID_ARGUMENT),
                 ('null-output', 'rgba', 0, abi.INVALID_ARGUMENT),
                 ('packet-caller-size-mismatch', 'width', 31, abi.PACKET_INVALID)]
        for label, field, value, status in cases:
            bad = abi.Capture.from_buffer_copy(bytes(request))
            setattr(bad, field, value)
            self.call(label, 'Capture', self.handle.value, C.byref(bad), C.byref(result), want=status)
            assert bytes(output) == initial, label + ' wrote rejected output'
        malformed_frame = (C.c_ubyte * 208)()
        malformed_visual = (C.c_ubyte * len(keepalive[1])).from_buffer_copy(bytes(keepalive[1]))
        malformed_visual[20] ^= 1  # Corrupt only the VIS1 binding to the exact FRM1 SHA.
        for label, field, value, length in [
                ('malformed-frame-header', 'frameBytes', malformed_frame, 208),
                ('visual-sha-binding', 'visualBytes', malformed_visual, len(malformed_visual))]:
            bad = abi.Capture.from_buffer_copy(bytes(request))
            setattr(bad, field, C.addressof(value))
            setattr(bad, 'frameLength' if field == 'frameBytes' else 'visualLength', length)
            self.call(label, 'Capture', self.handle.value, C.byref(bad), C.byref(result),
                      want=abi.PACKET_INVALID)
            assert bytes(output) == initial, label + ' wrote rejected output'

    def capture(self, fixture, label):
        request, output, keepalive = self.payload(fixture)
        class Extended(C.Structure):
            _fields_ = [('value', abi.Result), ('tail', C.c_ubyte * 16)]
        extended = Extended(abi.init(abi.Result), (C.c_ubyte * 16)(*[CANARY] * 16))
        extended.value.size = C.sizeof(extended)
        result = extended.value
        event = self.call(label, 'Capture', self.handle.value, C.byref(request),
                          C.cast(C.byref(extended), C.POINTER(abi.Result)), want=fixture['expectedStatus'])
        event.update(fixture=fixture['id'], purpose=PURPOSE, result=fields(result))
        assert bytes(extended.tail) == bytes([CANARY]) * 16
        if fixture['expectedStatus']:
            assert bytes(output) == bytes([CANARY]) * len(output)
            event['rejectedOutputUntouched'] = True
            return
        self.count += 1
        self.total += 1
        assert (result.width, result.height, result.sourceFrame, result.completedCaptures,
                result.drawCalls, result.rgbaBytesRequired) == (
                    fixture['width'], fixture['height'], fixture['sourceFrame'], self.count,
                    fixture['expectedDrawCalls'], request.rgbaCapacity)
        assert result.geometryGeneration > self.generation
        self.generation = result.geometryGeneration
        assert result.flags & 1 and result.effectPassFailures == 0 and result.noUiDiagnostics == 0
        width, height, stride = request.width, request.height, request.rgbaStride
        data = b''.join(bytes(output[y * stride:y * stride + width * 4]) for y in range(height))
        assert all(value == CANARY for y in range(height)
                   for value in output[y * stride + width * 4:(y + 1) * stride])
        assert bytes(output[height * stride:]) == bytes([CANARY]) * 32
        assert all(data[offset] == 255 for offset in range(3, len(data), 4))
        clear = bytes([31, 59, 94, 255])
        changed_x = [pixel % width for pixel in range(width * height)
                     if data[pixel * 4:pixel * 4 + 4] != clear]
        if fixture['visible']:
            assert 0 < len(changed_x) < width * height
            event.update(nonClearPixels=len(changed_x), nonClearCentroidX=sum(changed_x) / len(changed_x))
        else:
            assert data == bytes(fixture['expectedClearRgba8']) * (width * height)
            event['exactOpaqueClear'] = True
        filename = 'diagnostic-' + label + '.rgba'
        (self.args.output / filename).write_bytes(data)
        event.update(rgbaPath=filename, rgbaSha256=digest(data), stride=stride,
                     paddingAndTailUntouched=True, noUiDiagnostics=result.noUiDiagnostics)
        previous = self.images.get(fixture['id'])
        if previous is not None:
            assert data == previous['data'], label + ': repeat/recreate differs'
            event['repeatByteIdentical'] = True
        self.images[fixture['id']] = dict(data=data, event=event)
        if self.args.reference_exe and fixture['referenceCanCompare']:
            self.compare_reference(fixture, data, event)

    def compare_reference(self, fixture, data, event):
        name = fixture['id']
        if name not in self.reference_images:
            directory = self.args.output / ('reference-' + name)
            directory.mkdir()
            fixture_root = self.args.output / 'fixtures'
            command = [str(self.args.reference_exe), '--surface-auto',
                       str(fixture_root / fixture['frame']['path']), str(directory / 'diagnostic.rgba'),
                       str(directory / 'report.json'), str(fixture_root / fixture['visual']['path']),
                       str(self.args.reference_shader)]
            completed = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                       timeout=60, creationflags=subprocess.CREATE_NO_WINDOW)
            (directory / 'stdout.log').write_bytes(completed.stdout)
            event['referenceProcess'] = dict(arguments=command, exitCode=completed.returncode)
            assert completed.returncode == 0, event
            self.reference_images[name] = (directory / 'diagnostic.rgba').read_bytes()
        assert data == self.reference_images[name], name + ': explicit reference pixels differ'
        event['referenceByteIdentical'] = True

    def execute(self):
        self.lib = abi.load(self.args.dll)
        self.query()
        self.create_rejections()
        self.create('create-first-session')
        first = self.handle.value
        manifest = self.report['fixtureManifest']
        fixtures = {row['id']: row for row in manifest['fixtures']}
        self.live_rejections(fixtures['triangle32-a'])
        for label, name in [('first32', 'triangle32-a'), ('repeat32', 'triangle32-a'),
                            ('move32', 'triangle32-b'), ('first64', 'triangle64-a'),
                            ('move64', 'triangle64-b'), ('all-hidden', 'hidden32'),
                            ('alpha-rejected', 'alpha32-reject')]:
            self.capture(fixtures[name], label)
        for size in (32, 64):
            a, b = [self.images[f'triangle{size}-{sample}'] for sample in ('a', 'b')]
            assert a['data'] != b['data'], 'metadata-only motion is not sufficient'
            assert b['event']['nonClearCentroidX'] > a['event']['nonClearCentroidX']
        resize = abi.init(abi.Resize, width=64, height=32)
        self.call('explicit-resize-64x32', 'Resize', self.handle.value, C.byref(resize))
        self.capture(fixtures['triangle32-a'], 'after-explicit-resize')
        self.call('destroy-first-session', 'Destroy', first)
        self.handle.value = 0
        self.call('destroy-stale-handle', 'Destroy', first, want=abi.INVALID_HANDLE)
        self.create('create-second-session')
        assert self.handle.value > first
        self.capture(fixtures['triangle32-a'], 'recreated-session')
        self.call('destroy-second-session', 'Destroy', self.handle.value)
        self.handle.value = 0
        self.report.update(status='passed', actualCaptures=self.total,
                           noUiDiagnostics=0, dynamicPairsDiffer=True,
                           dynamicCentroidsMoveRight=True,
                           referenceComparisons=sum('referenceByteIdentical' in event
                                                    for event in self.report['events']))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path, help='new directory; existing paths are refused')
    parser.add_argument('--reference-exe', type=Path, help='optional independent --surface-auto probe executable')
    parser.add_argument('--reference-shader', type=Path, help='optional existing baseline shader; requires --reference-exe')
    args = parser.parse_args()
    if bool(args.reference_exe) != bool(args.reference_shader):
        parser.error('--reference-exe and --reference-shader must be supplied together')
    if sys.flags.optimize:
        parser.error('run without -O: this verification script uses assertions')
    if os.name != 'nt' or C.sizeof(C.c_void_p) != 8:
        parser.error('Windows and 64-bit Python are required')
    for name in ('dll', 'reference_exe', 'reference_shader'):
        value = getattr(args, name)
        if value is not None:
            value = value.resolve(strict=True)
            if not value.is_file():
                parser.error(f'--{name.replace("_", "-")} must be a file')
            setattr(args, name, value)
    args.output = args.output.resolve()
    args.output.mkdir(parents=True, exist_ok=False)
    report = dict(schemaVersion=1, status='running', startedUtc=utc(), purpose=PURPOSE,
                  scope='Native isolated ordinary-surface capture ABI only; no product preview, MME, EMM, physics or performance acceptance',
                  noUi=dict(requested=True, contract='hidden HWND, no Present; diagnostic counter checked per capture',
                            desktopWindowObservation='not performed'),
                  dll=dict(path=str(args.dll), sha256=digest(args.dll.read_bytes())),
                  scripts={path.name: digest(path.read_bytes()) for path in Path(__file__).parent.glob('*.py')},
                  events=[], actualCaptures=0)
    run = Run(args, report)
    dll_directory = None
    exit_code = 1
    try:
        report['ctypesSizes'] = abi.sizes()
        report['fixtureManifest'] = generate(args.output / 'fixtures')
        report['fixtureManifestSha256'] = digest((args.output / 'fixtures' / 'manifest.json').read_bytes())
        if args.reference_exe:
            report['reference'] = {name: dict(path=str(getattr(args, name)), sha256=digest(getattr(args, name).read_bytes()))
                                   for name in ('reference_exe', 'reference_shader')}
        dll_directory = os.add_dll_directory(str(args.dll.parent))
        run.execute()
        exit_code = 0
    except BaseException as exception:
        report.update(status='failed', error=repr(exception))
    finally:
        if run.lib is not None and run.handle.value:
            try:
                run.call('finally-owner-destroy', 'Destroy', run.handle.value)
                run.handle.value = 0
            except BaseException as exception:
                report['cleanupError'] = repr(exception)
        if dll_directory is not None:
            dll_directory.close()
        report.update(completedUtc=utc(), actualCaptures=run.total,
                      finalLiveSessionHandle=run.handle.value)
        target = args.output / 'report.json'
        target.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
        print(json.dumps(dict(status=report['status'], events=len(report['events']),
                              actualCaptures=run.total, report=str(target))))
    return exit_code


if __name__ == '__main__':
    sys.exit(main())

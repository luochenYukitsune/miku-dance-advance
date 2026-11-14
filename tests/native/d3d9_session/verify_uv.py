"""Explicit serial D3D9 UV1 pixel diagnostics. Never builds or discovers Apps.

All fixture assets are tiny and original. Imports perform no IO or DLL loading.
Run without -O. The parent generates packets, then starts legacy and current
workers sequentially; each worker owns one hidden HAL session on its main OS
thread and preloads the exact official Microsoft 43 runtime pair by full path.
This is isolated ordinary-surface validation, not MME/product/performance proof.
"""
from __future__ import annotations

import argparse
import ctypes as C
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import struct
import subprocess
import sys
import zlib

import abi
import generate as base

HERE = Path(__file__).resolve().parent
NEW_DLL = Path('G:/MDA-CompatValidation/20261104/uv274/native-build01/Mmd.Modern.D3D9.Compatibility.dll')
OLD_DLL = Path('G:/MDA-CompatValidation/20261103/session253/build05/Mmd.Modern.D3D9.Compatibility.dll')
RUNTIME = Path('G:/MDA-CompatValidation/20261103/runtime267/package/build/native/release/bin/x64')
OLD_FIXTURES = Path('G:/MDA-CompatValidation/20261103/session253/generic-final/fixtures')
PINS = {
    'new': '89103e85df1f61c6d8a56768d046b8ace5ee191de41faf0402304d3cea03604d',
    'legacy': 'b00d9a391466bde0afc469519594b4795e3e5100dcf65ca01cddadc6b68da766',
    'D3DX9_43.dll': '36b84dfa0ef9d3ff3549a7ad54d2a8032bd22c879219ee1a959137c4ef8786c4',
    'D3DCompiler_43.dll': 'ffedb064adc25328b24dcf145b04045a867a5574c931516d7845babf2a08937f',
}
CANARY = 0xA7
SIZE = 32
ATLAS = bytes([255, 0, 0, 255, 0, 255, 0, 255,
               0, 0, 255, 255, 255, 255, 0, 255])
DIFFUSE = bytes([96, 128, 160, 255]) * 4
PURPOSE = 'DIAGNOSTIC original UV1 texel-center/alpha geometry; not normal model, MME or product preview acceptance'
UV1 = (.25, .25, 13., -17.)
INACTIVE = ((.75, .25, 23., -29.), (.25, .75, 31., -37.), (.75, .75, 41., -43.))
ZERO = (0., 0., 0., 0.)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def utc():
    return datetime.now(timezone.utc).isoformat()


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2) + '\n', encoding='utf-8')


def pinned(path, expected):
    path = path.resolve(strict=True)
    actual = digest(path.read_bytes())
    if actual != expected:
        raise RuntimeError(f'identity mismatch {path}: {actual} != {expected}')
    return dict(path=str(path), bytes=path.stat().st_size, sha256=actual)


def encode(*, mode=3, diffuse=True, additive=False, version=2,
           visual_version=None, uv1=UV1, inactive=INACTIVE, channels=1,
           normal=(0., 0., -1.), sphere_normals=None, base_uvs=None,
           eg=(1., 1., 1., 1.), toon=(1., 1., 1., 1.),
           specular=(0., 0., 0., 1.), tex_mul=(1., 1., 1., 1.),
           tex_add=ZERO, sph_mul=(1., 1., 1., 1.), sph_add=ZERO,
           patterned_diffuse=False, sphere_alpha=255):
    """Serialize the existing generic triangle contract, adding two 2x2 maps.

    No renderer is implemented here. The UV1 sample is the exact center of the
    red top-left atlas texel; UV0 and UV2..4 select three other colors. The
    ordinary mode1 reference projects a raw sphere normal to the same texel.
    """
    visual_version = visual_version or (3 if mode == 3 and version == 2 else 2)
    positions = ((-.65, -.55, .5), (0., .65, .5), (.65, -.55, .5))
    base_uvs = base_uvs or ((.75, .75),) * 3
    sphere_normals = sphere_normals or ((-.5, .5, -1.),) * 3
    textures = []
    diffuse_index = sphere_index = -1
    if diffuse:
        diffuse_index = len(textures)
        textures.append(ATLAS if patterned_diffuse else DIFFUSE)
    if mode:
        sphere_index = len(textures)
        atlas = bytearray(ATLAS)
        for index in range(3, len(atlas), 4):
            atlas[index] = sphere_alpha
        textures.append(bytes(atlas))
    frame = bytearray(b'MDA9FRM1' + base.u32(version, SIZE, SIZE, 1, 3, 3, 1, len(textures)))
    frame += struct.pack('<d', .5) + base.f32(.5 / 30.)
    frame += base.f32(*base.IDENTITY) * 2 + base.f32(0., 0., -1.) + base.f32(*base.CLEAR)
    frame += struct.pack('<Q', base.OBJECT_ID) + base.u32(0, 1, 1)
    frame += base.string('diagnostic-uv1-triangle.pmx') + base.string('Original UV1 atlas diagnostic') + base.u32(0, 0)
    for position, uv in zip(positions, base_uvs):
        frame += base.f32(*position, *normal, *uv)
    frame += base.u32(0, 1, 2)
    frame += struct.pack('<Q', base.OBJECT_ID) + base.u32(0, 0, 3, 3)
    frame += base.f32(1., 1., 1., 1., 0., 0., 0., 0., 0., 0., 1.)
    frame += struct.pack('<4i', diffuse_index, -1, sphere_index, mode)
    for texture in textures:
        frame += base.u32(2, 2, len(texture)) + texture
    uv_offset = len(frame)
    if version == 2:
        frame += base.u32(channels, 3)
        for unused in positions:
            for vector in (uv1, *inactive):
                frame += base.f32(*vector)
    frame = bytes(frame)
    visual = bytearray(b'MDA9VIS1' + base.u32(visual_version, 1, 3))
    visual += hashlib.sha256(frame).digest() + base.f32(0., 0., -3.) + base.f32(*base.IDENTITY)
    opaque = int(eg[3] == 1. and sphere_alpha == 255 and (not diffuse or mode != 3 or toon[3] == 1. or normal == (0., 0., -1.)))
    visual += struct.pack('<Q', base.OBJECT_ID) + base.u32(0, opaque | (2 if additive else 0))
    visual += base.f32(*eg, *specular, *toon, 1. / 255.)
    visual += base.f32(*tex_mul, *tex_add, *sph_mul, *sph_add)
    for raw in sphere_normals:
        visual += base.f32(*normal, *raw)
    return frame, bytes(visual), uv_offset


def rebound(frame, visual):
    return visual[:20] + hashlib.sha256(frame).digest() + visual[52:]


def save_fixture(root, rows, name, *, group='uv', expected=0, features=None,
                 compare=None, rgba=None, clear=False, **options):
    frame, visual, uv_offset = encode(**options)
    directory = root / name
    directory.mkdir()
    row = dict(id=name, group=group, width=SIZE, height=SIZE, sourceFrame=.5,
               expectedStatus=expected, requiredFeatures=features or (3 if options.get('version', 2) == 2 else 1),
               expectedDrawCalls=1, compareWith=compare, expectedCenterRgba=rgba,
               expectedAllClear=clear, uvTrailerOffset=uv_offset,
               options=options, purpose=PURPOSE)
    for key, filename, data in [('frame', 'frame.packet', frame), ('visual', 'frame.visual', visual)]:
        (directory / filename).write_bytes(data)
        row[key] = dict(path=f'{name}/{filename}', bytes=len(data), sha256=digest(data))
    rows.append(row)
    return row


def mutate_fixture(root, rows, name, source, transform, *, expected=abi.PACKET_INVALID, features=None):
    frame = (root / source['frame']['path']).read_bytes()
    visual = (root / source['visual']['path']).read_bytes()
    frame, visual = transform(frame, visual)
    directory = root / name
    directory.mkdir()
    row = dict(source, id=name, group='reject', expectedStatus=expected,
               requiredFeatures=source['requiredFeatures'] if features is None else features,
               compareWith=None, expectedCenterRgba=None, expectedAllClear=False)
    for key, filename, data in [('frame', 'frame.packet', frame), ('visual', 'frame.visual', visual)]:
        (directory / filename).write_bytes(data)
        row[key] = dict(path=f'{name}/{filename}', bytes=len(data), sha256=digest(data))
    rows.append(row)
    return row


def generate(root):
    root.mkdir()
    base_manifest = base.generate(root / 'base')
    # Confirm these regenerated packets really are the previously checked input
    # bytes. Historical pixels are deliberately not reused as a current result.
    historical = json.loads((OLD_FIXTURES / 'manifest.json').read_text(encoding='utf-8'))
    old_rows = {row['id']: row for row in historical['fixtures']}
    rows = []
    for row in base_manifest['fixtures']:
        for key in ('frame', 'visual'):
            assert row[key]['sha256'] == old_rows[row['id']][key]['sha256'], row['id']
            pinned(OLD_FIXTURES / old_rows[row['id']][key]['path'], row[key]['sha256'])
        updated = dict(row, id='generic-' + row['id'], group='legacy', requiredFeatures=1,
                       compareWith=None, expectedCenterRgba=None, expectedAllClear=not row['visible'])
        for key in ('frame', 'visual'):
            updated[key] = dict(row[key], path='base/' + row[key]['path'])
        rows.append(updated)
    varying = ((-.9, .7, -.65), (.8, .2, -1.25), (-.4, -.8, -.90))
    for mode in range(3):
        for diffuse in (False, True):
            save_fixture(root, rows, f'legacy-mode{mode}-diffuse{int(diffuse)}',
                         group='legacy', version=1, mode=mode, diffuse=diffuse,
                         patterned_diffuse=True, sphere_normals=varying,
                         base_uvs=((.15, .25), (.75, .15), (.35, .85)),
                         eg=(.35, .2, .5, 1.), specular=(.1, .2, .3, 7.),
                         toon=(.65, .75, .85, .4), tex_mul=(.9, 1., .8, .6),
                         tex_add=(.1, 0., .05, .1), sph_mul=(1., .8, .7, .75),
                         sph_add=(0., .1, .05, .1))
    # Legacy sphere projection is an actual GPU control for the atlas texel;
    # simple independently known RGBA at interior pixels also checks sampling.
    for diffuse in (False, True):
        suffix = f'd{int(diffuse)}'
        control = 'control-multiply-' + suffix
        save_fixture(root, rows, control, mode=1, version=1, diffuse=diffuse,
                     rgba=[96 if diffuse else 255, 0, 0, 255])
        multiply = save_fixture(root, rows, 'uv-multiply-' + suffix, diffuse=diffuse,
                                compare=control, rgba=[96 if diffuse else 255, 0, 0, 255])
        save_fixture(root, rows, 'uv-additive-' + suffix, diffuse=diffuse, additive=True,
                     compare='control-add-d1' if diffuse else control,
                     rgba=[255, 128, 160, 255] if diffuse else [255, 0, 0, 255])
        save_fixture(root, rows, 'uv-inactive-zero-' + suffix, diffuse=diffuse,
                     inactive=(ZERO,) * 3, uv1=(.25, .25, -211., 223.),
                     compare=multiply['id'])
        save_fixture(root, rows, 'uv-active-four-' + suffix, diffuse=diffuse,
                     channels=4, compare=multiply['id'])
        save_fixture(root, rows, 'uv-green-' + suffix, diffuse=diffuse,
                     uv1=(.75, .25, 13., -17.),
                     rgba=[0, 128 if diffuse else 255, 0, 255])
        save_fixture(root, rows, 'uv-blue-' + suffix, diffuse=diffuse,
                     uv1=(.25, .75, 13., -17.),
                     rgba=[0, 0, 160 if diffuse else 255, 255])
    save_fixture(root, rows, 'control-add-d1', mode=2, version=1, diffuse=True,
                 rgba=[255, 128, 160, 255])
    dark = (1., 0., 0.)  # dot(N,L)=0; Toon RGB stays white to isolate alpha.
    save_fixture(root, rows, 'control-alpha-half', version=1, mode=1,
                 normal=dark, eg=(1., 1., 1., .5))
    save_fixture(root, rows, 'uv-toon-half', normal=dark, toon=(1., 1., 1., .5),
                 compare='control-alpha-half')
    save_fixture(root, rows, 'uv-toon-zero', normal=dark, toon=(1., 1., 1., 0.), clear=True)
    save_fixture(root, rows, 'uv-no-diffuse-toon-zero', diffuse=False, normal=dark,
                 toon=(1., 1., 1., 0.), compare='control-multiply-d0')
    save_fixture(root, rows, 'uv-no-diffuse-toon-half', diffuse=False, normal=dark,
                 toon=(1., 1., 1., .5), compare='control-multiply-d0')
    save_fixture(root, rows, 'control-raw-alpha', version=1, mode=1, sphere_alpha=128,
                 sph_mul=(1., 1., 1., 0.), sph_add=(0., 0., 0., 0.))
    save_fixture(root, rows, 'uv-raw-alpha', sphere_alpha=128,
                 sph_mul=(1., 1., 1., 0.), sph_add=(0., 0., 0., 0.),
                 compare='control-raw-alpha')
    save_fixture(root, rows, 'control-raw-alpha-toon-half', version=1, mode=1,
                 normal=dark, sphere_alpha=128, eg=(1., 1., 1., .5))
    save_fixture(root, rows, 'uv-raw-alpha-toon-half', normal=dark, sphere_alpha=128,
                 toon=(1., 1., 1., .5), compare='control-raw-alpha-toon-half')
    main = next(row for row in rows if row['id'] == 'uv-multiply-d1')
    trailer = main['uvTrailerOffset']
    def changed(offset, value):
        def change(frame, visual):
            frame = frame[:offset] + value + frame[offset + len(value):]
            return frame, rebound(frame, visual)
        return change
    mutate_fixture(root, rows, 'reject-uv-channels-zero', main, changed(trailer, base.u32(0)))
    mutate_fixture(root, rows, 'reject-uv-channels-five', main, changed(trailer, base.u32(5)))
    mutate_fixture(root, rows, 'reject-uv-vertex-count', main, changed(trailer + 4, base.u32(2)))
    mutate_fixture(root, rows, 'reject-uv-nonfinite', main, changed(trailer + 8, struct.pack('<I', 0x7fc00000)))
    mutate_fixture(root, rows, 'reject-uv-truncated', main,
                   lambda f, v: (f[:-1], rebound(f[:-1], v)))
    mutate_fixture(root, rows, 'reject-v2-feature-omitted', main, lambda f, v: (f, v),
                   expected=abi.UNSUPPORTED, features=1)
    mutate_fixture(root, rows, 'reject-unsupported-feature', main, lambda f, v: (f, v),
                   expected=abi.UNSUPPORTED, features=3 | 4)
    mutate_fixture(root, rows, 'reject-v2-visual-v2', main,
                   lambda f, v: (f, v[:8] + base.u32(2) + v[12:]))
    save_fixture(root, rows, 'reject-mode3-missing-uv', group='reject', version=1,
                 mode=3, diffuse=True, expected=abi.UNSUPPORTED)
    for row in rows:
        row['phase'] = 'pixels' if (row['group'] == 'legacy' or
            row['id'].startswith(('control-multiply-', 'uv-multiply-', 'uv-additive-',
                                   'uv-inactive-', 'uv-active-', 'uv-green-', 'uv-blue-')) or
            row['id'] == 'control-add-d1') else 'full'
    manifest = dict(schemaVersion=1, purpose=PURPOSE, generatedUtc=utc(), fixtures=rows,
                    atlas=dict(width=2, height=2, order='top-down RGBA8', rgba=list(ATLAS)),
                    baseInputByteIdentityVerified=True,
                    knownSampling=dict(uv1=[.25, .25], uv0=[.75, .75],
                                       flipY=[.25, .75], inactiveUvwPreserved=True))
    write_json(root / 'manifest.json', manifest)
    return manifest


def png(path, rgba, width, height):
    """Lossless viewable artifact only; no resize/edit or visual verdict."""
    def chunk(kind, data):
        return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data) & 0xffffffff)
    scanlines = b''.join(b'\0' + rgba[y * width * 4:(y + 1) * width * 4] for y in range(height))
    path.write_bytes(b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>2I5B', width, height, 8, 6, 0, 0, 0)) +
                     chunk(b'IDAT', zlib.compress(scanlines)) + chunk(b'IEND', b''))


class Worker:
    def __init__(self, args, report):
        self.args, self.report = args, report
        self.lib, self.handle = None, C.c_uint64()
        self.count = self.generation = 0
        self.images = {}
        self.runtime_handles = []

    def call(self, label, name, *arguments, want=0, snapshot=None):
        text = C.create_string_buffer(2048)
        error = abi.init(abi.Error, utf8=C.addressof(text), capacity=len(text))
        status = getattr(self.lib, name)(*arguments, C.byref(error))
        event = dict(case=label, operation=name, status=status, expectedStatus=want,
                     error=text.value.decode('utf-8', 'strict'),
                     errorStatus=error.status, requiredErrorBytes=error.requiredBytes)
        if snapshot is not None:
            event['result'] = {field: getattr(snapshot, field) for field, unused in snapshot._fields_}
        self.report['events'].append(event)
        assert status == want and error.status == status, event
        assert error.requiredBytes == len(text.value) + 1, event
        return event

    def preload(self):
        kernel = C.WinDLL('kernel32', use_last_error=True)
        kernel.LoadLibraryExW.argtypes = [C.c_wchar_p, C.c_void_p, C.c_uint32]
        kernel.LoadLibraryExW.restype = C.c_void_p
        kernel.GetModuleFileNameW.argtypes = [C.c_void_p, C.c_wchar_p, C.c_uint32]
        kernel.GetModuleFileNameW.restype = C.c_uint32
        kernel.GetModuleHandleW.argtypes = [C.c_wchar_p]
        kernel.GetModuleHandleW.restype = C.c_void_p
        self.kernel = kernel
        for name in ('D3DCompiler_43.dll', 'D3DX9_43.dll'):
            path = (self.args.runtime / name).resolve(strict=True)
            pinned(path, PINS[name])
            handle = kernel.LoadLibraryExW(str(path), None, 0x00000100 | 0x00000800)
            if not handle:
                raise C.WinError(C.get_last_error())
            self.runtime_handles.append(handle)
        self.runtime_paths('preload')

    def runtime_paths(self, phase):
        modules = []
        for name in ('D3DCompiler_43.dll', 'D3DX9_43.dll'):
            handle = self.kernel.GetModuleHandleW(name)
            path = C.create_unicode_buffer(32768)
            assert handle and self.kernel.GetModuleFileNameW(handle, path, len(path))
            actual = Path(path.value).resolve(strict=True)
            assert actual == (self.args.runtime / name).resolve(strict=True), (name, actual)
            modules.append(pinned(actual, PINS[name]))
        self.report.setdefault('loadedRuntime', []).append(dict(phase=phase, modules=modules))

    def create(self, label):
        self.call(label, 'Create', C.byref(abi.init(abi.Create, width=SIZE, height=SIZE)), C.byref(self.handle))
        assert self.handle.value
        self.count = self.generation = 0
        self.runtime_paths(label)

    def capture(self, row, label=None):
        label = label or row['id']
        buffers = []
        for key in ('frame', 'visual'):
            data = (self.args.output / 'fixtures' / row[key]['path']).read_bytes()
            assert digest(data) == row[key]['sha256'] and len(data) == row[key]['bytes']
            buffers.append((C.c_ubyte * len(data)).from_buffer_copy(data))
        width, height = row['width'], row['height']
        stride = width * 4 + 12
        required = (height - 1) * stride + width * 4
        storage = (C.c_ubyte * (height * stride + 32))(*([CANARY] * (height * stride + 32)))
        request = abi.init(abi.Capture, frameBytes=C.addressof(buffers[0]), frameLength=len(buffers[0]),
                           visualBytes=C.addressof(buffers[1]), visualLength=len(buffers[1]),
                           requiredFeatures=row['requiredFeatures'], flags=1, renderOrder=1,
                           width=width, height=height, rgba=C.addressof(storage),
                           rgbaCapacity=required, rgbaStride=stride)
        result = abi.init(abi.Result)
        result_before = bytes(result)
        event = self.call(label, 'Capture', self.handle.value, C.byref(request), C.byref(result),
                          want=row['expectedStatus'], snapshot=result)
        event['fixture'] = row['id']
        if row['expectedStatus']:
            assert bytes(storage) == bytes([CANARY]) * len(storage)
            assert bytes(result) == result_before
            event.update(rejectedOutputAndResultUntouched=True,
                         completedCapturesBefore=self.count, geometryGenerationBefore=self.generation)
            return
        self.count += 1
        assert (result.width, result.height, result.sourceFrame, result.completedCaptures,
                result.geometryGeneration, result.drawCalls, result.rgbaBytesRequired) == (
                    width, height, row['sourceFrame'], self.count,
                    self.generation + 1, row['expectedDrawCalls'], required)
        self.generation = result.geometryGeneration
        assert result.flags & 1 and not result.effectPassFailures and not result.noUiDiagnostics
        # The session caller activates its ordinary SM3 effect before entering
        # MmeHostDrawIndexedPrimitive. With no MME pass active, callbacks.cpp
        # passes that draw through unchanged and increments rawFallbackDraws.
        # This counter means caller-controlled shader pass-through, not FFP.
        assert (result.effectDispatches, result.effectPassDraws, result.rawFallbackDraws) == (
            0, 0, row['expectedDrawCalls']), (label, 'ordinary caller-controlled shader pass-through counters')
        rgba = b''.join(bytes(storage[y * stride:y * stride + width * 4]) for y in range(height))
        assert all(value == CANARY for y in range(height)
                   for value in storage[y * stride + width * 4:(y + 1) * stride])
        assert bytes(storage[height * stride:]) == bytes([CANARY]) * 32
        assert all(rgba[index] == 255 for index in range(3, len(rgba), 4))
        clear = bytes([31, 59, 94, 255])
        if row.get('expectedAllClear'):
            assert rgba == clear * (width * height), label
        else:
            coverage = sum(rgba[index:index + 4] != clear for index in range(0, len(rgba), 4))
            assert 0 < coverage < width * height, (label, coverage)
            event['nonClearPixels'] = coverage
        center = list(rgba[((height // 2) * width + width // 2) * 4:((height // 2) * width + width // 2) * 4 + 4])
        if row.get('expectedCenterRgba') is not None:
            assert center == row['expectedCenterRgba'], (label, center, row['expectedCenterRgba'])
        previous = self.images.get(row['id'])
        if previous is not None:
            assert previous == rgba, label + ': repeat/reset/recreate changed pixels'
            event['repeatByteIdentical'] = True
        self.images[row['id']] = rgba
        filename = label + '.rgba'
        target = self.args.output / self.args.worker
        (target / filename).write_bytes(rgba)
        png(target / (label + '.png'), rgba, width, height)
        event.update(rgbaPath=filename, rgbaSha256=digest(rgba), centerRgba=center,
                     paddingAndTailUntouched=True)

    def execute(self, manifest):
        self.preload()
        dll = self.args.dll if self.args.worker == 'new' else self.args.legacy_dll
        self.lib = abi.load(dll)
        query = abi.init(abi.Abi)
        self.call('query', 'QueryAbi', C.byref(query))
        assert (query.abiVersion, query.pointerBits, query.maxWidth, query.maxHeight,
                query.supportedFeatures) == (1, 64, 4096, 4096, 3 if self.args.worker == 'new' else 1)
        self.report['supportedFeatures'] = query.supportedFeatures
        self.create('create-first-session')
        selected = [row for row in manifest['fixtures']
                    if (row['group'] == 'legacy' or self.args.worker == 'new') and
                    (self.args.phase == 'full' or row['phase'] == 'pixels')]
        for row in selected:
            self.capture(row)
        if self.args.worker == 'new':
            for row in selected:
                if row.get('compareWith'):
                    assert self.images[row['id']] == self.images[row['compareWith']], row['id'] + ': actual GPU control pixels differ'
                    self.report['comparisons'].append(dict(fixture=row['id'], reference=row['compareWith'], byteIdentical=True))
            # Distinct actual atlas samples guard against metadata-only mutation.
            for suffix in ('d0', 'd1'):
                assert len({self.images['uv-' + name + '-' + suffix] for name in ('multiply', 'green', 'blue')}) == 3
            if self.args.phase == 'full':
                representatives = [next(row for row in selected if row['id'] == name)
                                   for name in ('uv-multiply-d1', 'uv-multiply-d0', 'uv-additive-d1')]
                for row in representatives:
                    self.call(row['id'] + '-explicit-resize-away', 'Resize', self.handle.value,
                              C.byref(abi.init(abi.Resize, width=64, height=48)))
                    self.capture(row, row['id'] + '-after-reset-back')  # Actual Reset 64x48 -> 32x32, then exact baseline pixels.
                    self.call(row['id'] + '-explicit-resize-back', 'Resize', self.handle.value,
                              C.byref(abi.init(abi.Resize, width=SIZE, height=SIZE)))
                    self.capture(row, row['id'] + '-after-same-size-resize')
                self.call('destroy-first-session', 'Destroy', self.handle.value)
                self.handle.value = 0
                self.create('create-second-session')
                for row in representatives:
                    self.capture(row, row['id'] + '-after-destroy-recreate')
        self.call('destroy-final-session', 'Destroy', self.handle.value)
        self.handle.value = 0
        self.runtime_paths('after-destroy')
        self.report['status'] = 'passed'


def worker(args):
    target = args.output / args.worker
    target.mkdir(exist_ok=False)
    report = dict(status='running', startedUtc=utc(), purpose=PURPOSE, phase=args.phase, events=[], comparisons=[],
                  ordinaryDrawCounters='session caller activates ordinary SM3 shader; host has no MME pass and passes draw through unchanged: dispatch=0, passDraws=0, rawFallbackDraws=drawCalls. This is not evidence of FFP.',
                  dll=pinned(args.dll if args.worker == 'new' else args.legacy_dll, PINS[args.worker]))
    run = Worker(args, report)
    try:
        report['ctypesSizes'] = abi.sizes()
        manifest = json.loads((args.output / 'fixtures' / 'manifest.json').read_text(encoding='utf-8'))
        report['fixtureManifestSha256'] = digest((args.output / 'fixtures' / 'manifest.json').read_bytes())
        run.execute(manifest)
    except BaseException as error:
        report.update(status='failed', error=repr(error))
    finally:
        if run.lib is not None and run.handle.value:
            try:
                run.call('finally-owner-destroy', 'Destroy', run.handle.value)
                run.handle.value = 0
            except BaseException as error:
                report['cleanupError'] = repr(error)
        report.update(completedUtc=utc(), finalLiveSessionHandle=run.handle.value,
                      captures=sum(event.get('operation') == 'Capture' and event.get('status') == 0 for event in report['events']))
        write_json(target / 'report.json', report)
    return 0 if report['status'] == 'passed' else 1


def parent(args):
    args.output.mkdir(parents=True, exist_ok=False)
    report = dict(schemaVersion=1, status='preparing', startedUtc=utc(), purpose=PURPOSE,
                  phase=args.phase,
                  scope='Tiny isolated ordinary-surface D3D9 HAL/SM3 pixels; full phase also checks explicit Reset. No MME, GUI, preview, installer, clock or performance claim',
                  noUi=dict(contract='hidden owner HWND and no Present, native diagnostic counters checked',
                            desktopWindowObservation='not performed'),
                  scripts={p.name: digest(p.read_bytes()) for p in HERE.glob('*.py')}, workers=[])
    try:
        report['inputs'] = {key: pinned(path, PINS[key]) for key, path in [('new', args.dll), ('legacy', args.legacy_dll)]}
        report['runtime'] = {name: pinned(args.runtime / name, PINS[name]) for name in ('D3DX9_43.dll', 'D3DCompiler_43.dll')}
        report['runtimeProvenance'] = 'Microsoft.DXSDK.D3DX 9.29.952.8, runtime267/completion.json; original LICENSE.txt and NOTICE.md retained at package root'
        manifest = generate(args.output / 'fixtures')
        report['fixtureCount'] = len(manifest['fixtures'])
        report['fixtureManifestSha256'] = digest((args.output / 'fixtures' / 'manifest.json').read_bytes())
        for mode in ('legacy', 'new'):
            command = [sys.executable, str(HERE / 'verify_uv.py'), '--worker', mode,
                       '--output', str(args.output), '--dll', str(args.dll),
                       '--legacy-dll', str(args.legacy_dll), '--runtime', str(args.runtime),
                       '--phase', args.phase]
            completed = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                       timeout=120, creationflags=subprocess.CREATE_NO_WINDOW)
            (args.output / (mode + '-stdout.log')).write_bytes(completed.stdout)
            report['workers'].append(dict(mode=mode, arguments=command, exitCode=completed.returncode))
            assert completed.returncode == 0, (mode, completed.returncode)
        legacy_report = json.loads((args.output / 'legacy' / 'report.json').read_text(encoding='utf-8'))
        new_report = json.loads((args.output / 'new' / 'report.json').read_text(encoding='utf-8'))
        report['legacyPixelComparisons'] = []
        for row in manifest['fixtures']:
            if row['group'] != 'legacy' or row['expectedStatus'] != 0:
                continue
            old = (args.output / 'legacy' / (row['id'] + '.rgba')).read_bytes()
            new = (args.output / 'new' / (row['id'] + '.rgba')).read_bytes()
            assert old == new, row['id'] + ': current pixels differ from explicit legacy DLL same input'
            report['legacyPixelComparisons'].append(dict(fixture=row['id'], byteIdentical=True,
                                                        inputFrameSha256=row['frame']['sha256'], inputVisualSha256=row['visual']['sha256'],
                                                        rgbaSha256=digest(new)))
        report.update(status='passed', actualLegacyCaptures=legacy_report['captures'],
                      actualNewCaptures=new_report['captures'],
                      uvControlComparisons=len(new_report['comparisons']),
                      visualReview='PENDING: inspect purpose-labelled PNGs before any normal-model or broader visual claim')
    except BaseException as error:
        report.update(status='failed', error=repr(error))
    finally:
        report['completedUtc'] = utc()
        write_json(args.output / 'report.json', report)
        print(json.dumps(dict(status=report['status'], report=str(args.output / 'report.json'))))
    return 0 if report['status'] == 'passed' else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', required=True, type=Path, help='NEW directory; parent refuses existing output')
    parser.add_argument('--dll', default=NEW_DLL, type=Path)
    parser.add_argument('--legacy-dll', default=OLD_DLL, type=Path)
    parser.add_argument('--runtime', default=RUNTIME, type=Path)
    parser.add_argument('--phase', choices=['pixels', 'full'], default='full',
                        help='pixels: old/new mode0..2 and UV1 atlas; full: also ToonW/raw alpha/rejections/Reset/recreate')
    parser.add_argument('--worker', choices=['legacy', 'new'], help=argparse.SUPPRESS)
    args = parser.parse_args()
    if sys.flags.optimize or os.name != 'nt' or C.sizeof(C.c_void_p) != 8:
        parser.error('requires Windows, x64 Python, and assertions enabled (no -O)')
    for name in ('output', 'dll', 'legacy_dll', 'runtime'):
        setattr(args, name, getattr(args, name).resolve())
    return worker(args) if args.worker else parent(args)


if __name__ == '__main__':
    sys.exit(main())

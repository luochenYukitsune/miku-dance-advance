"""Original, CPU-only synthetic FRM1/VIS1 diagnostic fixtures; no user assets.

The small serializer/readback algorithm is preserved from the validated tiny
session fixture. It produces inputs, not a copy of the native renderer as an
image oracle. Importing this module performs no IO or graphics work.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
from pathlib import Path
import struct
import sys

IDENTITY = (1., 0., 0., 0., 0., 1., 0., 0., 0., 0., 1., 0., 0., 0., 0., 1.)
OBJECT_ID = 0x1000000000000101
CLEAR = (.12, .23, .37, 1.)
DIFFUSE = (.72, .28, .12, 1.)
AMBIENT = (.10, .07, .03)
NORMALS = ((0., 0., -.65), (0., 0., -1.25), (0., 0., -.90))
PURPOSE = 'DIAGNOSTIC synthetic geometry only; not normal-material or MME visual acceptance'


def digest(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def u32(*values: int) -> bytes:
    return struct.pack('<' + 'I' * len(values), *values)


def f32(*values: float) -> bytes:
    assert all(math.isfinite(value) for value in values)
    return struct.pack('<' + 'f' * len(values), *values)


def string(value: str) -> bytes:
    encoded = value.encode('utf-8')
    assert b'\0' not in encoded
    return u32(len(encoded)) + encoded


def encode(width: int, source_frame: float, offset: float, *, hidden=False, alpha=1.):
    vertices = [] if hidden else [(-.65 + offset, -.55, .5), (offset, .65, .5), (.65 + offset, -.55, .5)]
    material_count = int(not hidden)
    frame = bytearray(b'MDA9FRM1' + u32(1, width, width, 1, len(vertices), len(vertices), material_count, 0))
    frame += struct.pack('<d', source_frame) + f32(source_frame / 30.)
    frame += f32(*IDENTITY) + f32(*IDENTITY) + f32(0., 0., -1.) + f32(*CLEAR[:3], alpha)
    frame += struct.pack('<Q', OBJECT_ID) + u32(0, material_count, int(not hidden))
    frame += string('diagnostic-session-triangle.pmx') + string('Original fixture triangle') + u32(0, 0)
    for position in vertices:
        frame += f32(*position, 0., 0., -1., 0., 0.)
    if vertices:
        frame += u32(0, 1, 2)
        frame += struct.pack('<Q', OBJECT_ID) + u32(0, 0, 3, 3)
        frame += f32(*DIFFUSE, 0., 0., 0., *AMBIENT, 0.)
        frame += struct.pack('<iiii', -1, -1, -1, 0)
    frame = bytes(frame)
    visual = bytearray(b'MDA9VIS1' + u32(2, material_count, len(vertices)))
    visual += hashlib.sha256(frame).digest() + f32(0., 0., -3.) + f32(*IDENTITY)
    if vertices:
        visual += struct.pack('<Q', OBJECT_ID) + u32(0, 1)
        visual += f32(*(min(DIFFUSE[i] + AMBIENT[i], 1.) for i in range(3)), 1.)
        visual += f32(0., 0., 0., .1)
        visual += f32(1., 1., 1., 1.) + f32(1. / 255.)
        visual += f32(1., 1., 1., 1., 0., 0., 0., 0.) * 2
        for normal in NORMALS:
            visual += f32(*normal, *normal)
    return frame, bytes(visual), vertices


class Reader:
    def __init__(self, data: bytes):
        self.data, self.offset = data, 0

    def raw(self, count: int) -> bytes:
        assert 0 <= count <= len(self.data) - self.offset
        result = self.data[self.offset:self.offset + count]
        self.offset += count
        return result

    def unpack(self, pattern: str):
        return struct.unpack('<' + pattern, self.raw(struct.calcsize('<' + pattern)))

    def text(self) -> str:
        count, = self.unpack('I')
        result = self.raw(count).decode('utf-8', errors='strict')
        assert '\0' not in result
        return result

    def end(self):
        assert self.offset == len(self.data), (self.offset, len(self.data))


def verify(frame: bytes, visual: bytes, *, width: int, source_frame: float, positions, hidden: bool, alpha: float):
    """Read complete serialized inputs back; not a renderer/image oracle."""
    r = Reader(frame)
    assert r.raw(8) == b'MDA9FRM1'
    version, w, h, objects, vertices, indices, materials, textures = r.unpack('8I')
    assert (version, w, h, objects, vertices, indices, materials, textures) == (1, width, width, 1, len(positions), len(positions), int(not hidden), 0)
    actual_frame, = r.unpack('d')
    seconds, = r.unpack('f')
    assert actual_frame == source_frame and abs(seconds - source_frame / 30.) < 1e-8
    assert r.unpack('16f') == IDENTITY and r.unpack('16f') == IDENTITY
    assert r.unpack('3f') == (0., 0., -1.)
    clear = r.unpack('4f')
    assert all(abs(clear[i] - CLEAR[i]) < 1e-7 for i in range(3)) and clear[3] == alpha
    assert r.offset == 208
    assert r.unpack('Q3I') == (OBJECT_ID, 0, materials, int(not hidden))
    assert r.text() == 'diagnostic-session-triangle.pmx' and r.text() == 'Original fixture triangle'
    assert r.unpack('2I') == (0, 0)
    for position in positions:
        actual = r.unpack('8f')
        assert all(abs(actual[i] - position[i]) < 1e-7 for i in range(3))
        assert actual[3:] == (0., 0., -1., 0., 0.)
    if positions:
        assert r.unpack('3I') == (0, 1, 2)
        assert r.unpack('Q4I') == (OBJECT_ID, 0, 0, 3, 3)
        material = r.unpack('11f')
        assert material[3] == 1. and material[4:7] == (0., 0., 0.) and material[-1] == 0.
        assert r.unpack('4i') == (-1, -1, -1, 0)
    r.end()
    r = Reader(visual)
    assert r.raw(8) == b'MDA9VIS1' and r.unpack('3I') == (2, materials, vertices)
    assert r.raw(32) == hashlib.sha256(frame).digest()
    assert r.unpack('3f') == (0., 0., -3.) and r.unpack('16f') == IDENTITY
    assert r.offset == 128
    if positions:
        assert r.unpack('Q2I') == (OBJECT_ID, 0, 1)
        colors = r.unpack('12f')
        assert colors[3] == 1. and colors[4:7] == (0., 0., 0.) and colors[8:] == (1., 1., 1., 1.)
        cutoff, = r.unpack('f')
        assert abs(cutoff - 1. / 255.) < 1e-9
        assert r.unpack('16f') == (1., 1., 1., 1., 0., 0., 0., 0.) * 2
        for expected in NORMALS:
            actual = r.unpack('6f')
            assert all(abs(actual[i] - (expected * 2)[i]) < 1e-7 for i in range(6))
    r.end()
    assert len(visual) == 128 + materials * 132 + vertices * 24
    return dict(completeReadback=True, sha256Binding=True, rawNormalsPreserved=True)


def generate(output: Path):
    """Generate only small original fixtures in a new caller-chosen directory."""
    output.mkdir(parents=True, exist_ok=False)
    rows = []
    cases = [('triangle32-a', 32, .5, -.12, False, 1.),
             ('triangle32-b', 32, 1.5, .12, False, 1.),
             ('triangle64-a', 64, .5, -.12, False, 1.),
             ('triangle64-b', 64, 1.5, .12, False, 1.),
             ('hidden32', 32, 2.5, .12, True, 1.),
             ('alpha32-reject', 32, .5, -.12, False, .25)]
    for name, width, source_frame, offset, hidden, alpha in cases:
        frame, visual, positions = encode(width, source_frame, offset, hidden=hidden, alpha=alpha)
        checks = verify(frame, visual, width=width, source_frame=source_frame, positions=positions, hidden=hidden, alpha=alpha)
        directory = output / name
        directory.mkdir()
        row = dict(id=name, width=width, height=width, sourceFrame=source_frame,
                   objectId=OBJECT_ID, visible=not hidden, vertexPositions=positions,
                   expectedStatus=-6 if alpha < 1. else 0,
                   expectedDrawCalls=0 if hidden else 1,
                   expectedClearRgba8=[31, 59, 94, 255] if hidden else None,
                   referenceCanCompare=not hidden and alpha == 1., selfCheck=checks)
        for key, filename, data in [('frame', 'frame.packet', frame), ('visual', 'frame.visual', visual)]:
            (directory / filename).write_bytes(data)
            row[key] = dict(path=f'{name}/{filename}', bytes=len(data), sha256=digest(data))
        rows.append(row)
    manifest = dict(schemaVersion=1, status='cpu_serialized_and_self_checked',
                    purpose=PURPOSE, fixtures=rows,
                    sourceSha256=digest(Path(__file__).read_bytes()),
                    contracts=dict(frame='MDA9FRM1 v1 little-endian', visual='MDA9VIS1 v2 little-endian'))
    (output / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
    return manifest


if __name__ == '__main__':
    if sys.flags.optimize:
        raise RuntimeError('Run without -O: this verification script uses assertions')
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    manifest = generate(args.output.resolve())
    print(json.dumps(dict(status=manifest['status'], fixtures=len(manifest['fixtures']))))

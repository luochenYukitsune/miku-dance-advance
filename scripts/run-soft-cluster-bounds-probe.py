"""Focused native cluster bounds checks. Builds nothing; never sends overflow cases to baseline DLLs."""
import argparse
from contextlib import contextmanager
import ctypes as c
import hashlib
import json
import math
from pathlib import Path


class Vec(c.Structure):
    _fields_ = [(name, c.c_float) for name in ('x', 'y', 'z')]


def floats(values):
    return (c.c_float * len(values))(*values)


def ints(values=()):
    return (c.c_int * len(values))(*values)


def settings():
    return floats([1, .02, 1, .01, 0, 0, 0, 0, .2, 0, 1, .1, 1, .7, 1, .1, .5, .5, .5, .5, 1, 1, 1])


def options(shape=0, flags=2, requested=0):
    return ints([shape, flags, 0, requested, 0, 0, 0, 0, 4, 0, 4])


class API:
    def __init__(self, path, coupled):
        self.path, self.coupled = path.resolve(), coupled
        self.dll = c.CDLL(str(self.path))
        ptr, fp, ip = c.c_void_p, c.POINTER(c.c_float), c.POINTER(c.c_int)

        def bind(name, arguments, result=c.c_int):
            function = getattr(self.dll, name)
            function.argtypes, function.restype = arguments, result
            return function

        self.error = bind('mmd_soft_error', [], c.c_char_p)
        self.add = bind('mmd_soft_add', [ptr, fp, c.c_int, ip, c.c_int, ip, fp, ip, c.c_int, ip, c.c_int])
        self.shared = bind('mmd_soft_add_shared', [ptr, fp, c.c_int, c.c_int, ip, ip, c.c_int, ip, ip, c.c_int, ip, fp, ip, c.c_int, ip, c.c_int])
        self.sample = bind('mmd_soft_sample', [ptr, c.c_int, fp, fp, c.c_int])
        if coupled:
            self.owner_error = bind('mmd_bullet_last_error', [], c.c_char_p)
            self.create = bind('mmd_bullet_create_coupled', [Vec, c.c_int, c.c_float, c.POINTER(ptr)])
            self.attach = bind('mmd_bullet_soft_create', [ptr, ip, c.c_int, c.POINTER(ptr)])
            self.release = bind('mmd_bullet_soft_destroy', [ptr, ptr])
            self.destroy = bind('mmd_bullet_destroy', [ptr], None)
            self.step = bind('mmd_bullet_step', [ptr, c.c_float])
        else:
            self.create = bind('mmd_soft_create', [fp, c.c_int, c.c_float, c.POINTER(ptr)])
            self.destroy = bind('mmd_soft_destroy', [ptr], None)
            self.step = bind('mmd_soft_step', [ptr, c.c_float])

    def check(self, status):
        assert status == 0, (status, self.error(), self.owner_error() if self.coupled else None)

    @contextmanager
    def world(self):
        owner, soft = c.c_void_p(), c.c_void_p()
        try:
            if self.coupled:
                self.check(self.create(Vec(0, 0, 0), 0, 0, c.byref(owner)))
                self.check(self.attach(owner, ints(), 0, c.byref(soft)))
            else:
                self.check(self.create(floats([0, 0, 0]), 0, 0, c.byref(owner)))
                soft = owner
            yield owner, soft
        finally:
            try:
                if self.coupled and soft:
                    self.check(self.release(owner, soft))
            finally:
                if owner:
                    self.destroy(owner)

    def read(self, soft, count, index=0):
        positions, normals = (c.c_float * (count * 3))(), (c.c_float * (count * 3))()
        self.check(self.sample(soft, index, positions, normals, count))
        assert all(math.isfinite(v) for v in positions) and all(math.isfinite(v) for v in normals)
        return bytes(positions) + bytes(normals)

    def small(self):
        snapshots = []
        with self.world() as (owner, soft):
            self.check(self.add(soft, floats([0, 1, 0, 1, 1, 0, 0, 1, 1]), 3, ints([0, 1, 2]), 3,
                                options(requested=2), settings(), ints(), 0, ints(), 0))
            snapshots.append(self.read(soft, 3))
            for _ in range(3):
                self.check(self.step(owner, 1 / 120))
                snapshots.append(self.read(soft, 3))
        return b''.join(snapshots)

    def check_connectivity_rejection(self, faces, requested, nodes):
        with self.world() as (_, soft):
            points = floats([value for i in range(nodes) for value in (float(i % 3 == 1), 1., float(i % 3 == 2))])
            triangles = ints([0, 1, 2] * faces)
            status = self.add(soft, points, nodes, triangles, len(triangles), options(requested=requested),
                              settings(), ints(), 0, ints(), 0)
            assert status != 0 and b'Cluster connectivity exceeds int32 storage' in self.error()
            # A rejected candidate must not publish a soft-body slot or poison the world.
            self.check(self.add(soft, floats([0, 1, 0, 1, 1, 0, 0, 1, 1]), 3, ints([0, 1, 2]), 3,
                                options(requested=100_000), settings(), ints(), 0, ints(), 0))
            assert self.read(soft, 3)
            probe = (c.c_float * 9)()
            assert self.sample(soft, 1, probe, probe, 3) != 0

    def check_large_seed(self, shared):
        count = 71_889
        points = floats([value for i in range(count) for value in (i / count, float(i % 7) * .01, float(i % 11) * .01)])
        with self.world() as (_, soft):
            if shared:
                # A large rope and a two-node rope share the last nodes. No quadratic
                # triangle-link construction is needed to exercise the same seed path.
                nodes = ints(list(range(count)) + [count - 2, count - 1])
                opts = ints(list(options(shape=1, requested=5)) * 2)
                values = floats(list(settings()) * 2)
                self.check(self.shared(soft, points, count, 2, ints([0, count, count + 2]), nodes, count + 2,
                                       ints([0, 0, 0]), ints(), 0, opts, values, ints(), 0, ints(), 0))
            else:
                self.check(self.add(soft, points, count, ints(), 0, options(shape=1, requested=5),
                                    settings(), ints(), 0, ints(), 0))
            actual = self.read(soft, count)
            assert actual[:len(bytes(points))] == bytes(points)


def run(path, coupled, baseline):
    api = API(path, coupled)
    small = api.small()
    if baseline:
        # The old library only receives a tiny, previously defined safe fixture.
        assert API(baseline, coupled).small() == small
    for faces in (46_341, 65_536):
        api.check_connectivity_rejection(faces, requested=0, nodes=3)
    api.check_connectivity_rejection(1, requested=46_340, nodes=46_340)
    api.check_connectivity_rejection(1, requested=-1, nodes=3)
    api.check_large_seed(shared=False)
    api.check_large_seed(shared=True)
    return dict(path=str(api.path), sha256=hashlib.sha256(path.read_bytes()).hexdigest(), coupled=coupled,
                passed=True, checks=['zero-request face limit', '65536-square overflow rejection',
                                     'positive-request master-cluster bound', 'negative request rejection', 'failure leaves world usable',
                                     'large positive request clamps to actual small node count',
                                     '71889-node single seed', '71889-node shared-source seed',
                                     'finite outputs and unchanged initial node positions'],
                safeBaselineByteComparison=bool(baseline))


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--standalone', type=Path, required=True)
    parser.add_argument('--coupled', type=Path, required=True)
    parser.add_argument('--baseline-standalone', type=Path)
    parser.add_argument('--baseline-coupled', type=Path)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    result = dict(passed=False, scope='CPU native boundary regression only; no GPU, no timing/solver optimization.')
    try:
        result['libraries'] = [run(args.standalone, False, args.baseline_standalone),
                               run(args.coupled, True, args.baseline_coupled)]
        result['passed'] = True
    except Exception as error:
        result['error'] = repr(error)
    args.output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(result, indent=2))
    raise SystemExit(0 if result['passed'] else 1)

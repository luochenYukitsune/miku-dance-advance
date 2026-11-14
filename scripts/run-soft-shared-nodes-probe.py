"""Run C05 ABI contract checks against an already built bridge; never compiles.

Root owns invocation. Optional --old-library proves ABI1 ordinary calls still work.
"""
import argparse
import ctypes as c
import hashlib
import json
from pathlib import Path

root = Path(__file__).resolve().parent.parent
parser = argparse.ArgumentParser()
parser.add_argument('--library', type=Path, default=root.parent / '.dev-cache/soft-core-completion-20261021/mmd_bullet_soft.dll')
parser.add_argument('--old-library', type=Path)
parser.add_argument('--output', type=Path, default=root / 'artifacts/stage-20261021-core-completion/shared-nodes-native-probe.json')
args = parser.parse_args()
F, I, P = c.POINTER(c.c_float), c.POINTER(c.c_int), c.c_void_p


def floats(values):
    return (c.c_float * len(values))(*values)


def ints(values):
    return (c.c_int * len(values))(*values)


def bind(path, shared):
    dll = c.CDLL(str(path.resolve()))
    signatures = {
        'mmd_soft_abi': ([], c.c_int), 'mmd_soft_error': ([], c.c_char_p),
        'mmd_soft_create': ([F, c.c_int, c.c_float, c.POINTER(P)], c.c_int),
        'mmd_soft_destroy': ([P], None),
        'mmd_soft_add': ([P, F, c.c_int, I, c.c_int, I, F, I, c.c_int, I, c.c_int], c.c_int),
        'mmd_soft_sample': ([P, c.c_int, F, F, c.c_int], c.c_int),
        'mmd_soft_step': ([P, c.c_float], c.c_int),
    }
    if shared:
        signatures.update({
            'mmd_soft_features': ([], c.c_int),
            'mmd_soft_add_shared': ([P, F, c.c_int, c.c_int, I, I, c.c_int, I, I, c.c_int, I, F, I, c.c_int, I, c.c_int], c.c_int),
            'mmd_soft_topology': ([P, c.c_int, I, F, c.c_int], c.c_int),
        })
    for name, (arguments, result) in signatures.items():
        function = getattr(dll, name)
        function.argtypes, function.restype = arguments, result
    assert dll.mmd_soft_abi() == 1
    return dll


def check(dll, status):
    assert status == 0, dll.mmd_soft_error().decode()


def create(dll):
    world = P()
    check(dll, dll.mmd_soft_create(floats([0, -9.8, 0]), 0, 0, c.byref(world)))
    return world


def sample(dll, world, count):
    positions, normals = floats([0] * (count * 3)), floats([0] * (count * 3))
    check(dll, dll.mmd_soft_sample(world, 0, positions, normals, count))
    return list(positions)


points = [0, 3, 0, 1, 3, 0, 0, 3, 1, 1, 3, 1]
options = [0, 0, 2, 0, 0, 0, 65535, 0, 4, 0, 4]
values = [3, .02, 1, .01, 0, 0, 0, 0, .2, 0, 1, .1, 1, .7, 1, .1, .5, .5, .5, .5, .2, .3, .4]
other_values = [6, *values[1:20], .8, .7, .6]
base = dict(points=points, count=4, node_offsets=[0, 3, 6], nodes=[0, 1, 2, 1, 3, 2],
            triangle_offsets=[0, 3, 6], triangles=[0, 1, 2, 0, 1, 2],
            options=options * 2, values=values + other_values, pins=[])


def add_shared(dll, world, payload):
    return dll.mmd_soft_add_shared(world, floats(payload['points']), payload['count'], 2,
        ints(payload['node_offsets']), ints(payload['nodes']), len(payload['nodes']),
        ints(payload['triangle_offsets']), ints(payload['triangles']), len(payload['triangles']),
        ints(payload['options']), floats(payload['values']), ints(payload['pins']), len(payload['pins']), None, 0)


dll = bind(args.library, True)
assert dll.mmd_soft_features() & 4
report = dict(library=str(args.library.resolve()), sha256=hashlib.sha256(args.library.read_bytes()).hexdigest(),
              abi=dll.mmd_soft_abi(), features=dll.mmd_soft_features(), negativeCases=[])
world = create(dll)
try:
    check(dll, add_shared(dll, world, base))
    counts, masses = ints([0] * 4), floats([0] * 4)
    check(dll, dll.mmd_soft_topology(world, 0, counts, masses, 4))
    assert list(counts) == [4, 2, 6, 2], list(counts)
    assert list(masses) == [1, 3, 3, 2], list(masses)
    report.update(topology=list(counts), masses=list(masses))
    before = sample(dll, world, 4)
    cases = {
        'nonmonotone_offsets': dict(node_offsets=[0, 4, 3]),
        'duplicate_source_node': dict(nodes=[0, 1, 2, 1, 1, 2]),
        'out_of_range_node': dict(nodes=[0, 1, 2, 1, 4, 2]),
        'out_of_range_triangle': dict(triangles=[0, 1, 2, 0, 1, 3]),
        'nonfinite_point': dict(points=[float('nan'), *points[1:]]),
        'nonfinite_mass': dict(values=[float('inf'), *base['values'][1:]]),
        'unowned_union_node': dict(nodes=[0, 1, 2, 0, 1, 2]),
        'invalid_pin': dict(pins=[4]),
    }
    conflict = list(base['values']); conflict[24] = .5
    cases['bodywide_margin_conflict'] = dict(values=conflict)
    cluster = list(base['options']); cluster[1] = 2
    cases['mixed_cluster_collision_switch'] = dict(options=cluster)
    for name, changes in cases.items():
        payload = dict(base, **changes)
        assert add_shared(dll, world, payload) != 0, name
        reason = dll.mmd_soft_error().decode()
        assert sample(dll, world, 4) == before, name
        assert dll.mmd_soft_topology(world, 1, counts, masses, 4) != 0, name
        report['negativeCases'].append(dict(name=name, reason=reason, oldWorldUnchanged=True, noPartialBody=True))
    for _ in range(60):
        check(dll, dll.mmd_soft_step(world, 1 / 120))
    assert all(sample(dll, world, 4)[i] < 2.9 for i in (1, 4, 7, 10))
    report['falls'] = True
finally:
    dll.mmd_soft_destroy(world)

if args.old_library:
    old = bind(args.old_library, False)
    world = create(old)
    try:
        check(old, old.mmd_soft_add(world, floats(points[:9]), 3, ints([0, 1, 2]), 3,
            ints(options), floats(values), None, 0, None, 0))
        for _ in range(10):
            check(old, old.mmd_soft_step(world, 1 / 120))
        assert sample(old, world, 3)[1] < 3
        report['oldAbi1Ordinary'] = dict(path=str(args.old_library.resolve()),
            sha256=hashlib.sha256(args.old_library.read_bytes()).hexdigest(), passed=True)
    finally:
        old.mmd_soft_destroy(world)

args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps(report, indent=2), encoding='utf-8')
print(json.dumps(report, indent=2))

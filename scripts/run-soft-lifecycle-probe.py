"""Validate C04 atomic rigid-proxy ABI against a prebuilt bridge. Never builds."""
import argparse
import ctypes as c
import hashlib
import json
from pathlib import Path

root = Path(__file__).resolve().parent.parent
parser = argparse.ArgumentParser()
parser.add_argument('--library', type=Path, default=root.parent / '.dev-cache/soft-core-completion-20261021/mmd_bullet_soft.dll')
parser.add_argument('--output', type=Path, default=root / 'artifacts/stage-20261021-core-completion/soft-lifecycle-native-probe.json')
args = parser.parse_args()
F, I, P = c.POINTER(c.c_float), c.POINTER(c.c_int), c.c_void_p
dll = c.CDLL(str(args.library.resolve()))
signatures = {
    'mmd_soft_abi': ([], c.c_int), 'mmd_soft_features': ([], c.c_int), 'mmd_soft_error': ([], c.c_char_p),
    'mmd_soft_create': ([F, c.c_int, c.c_float, c.POINTER(P)], c.c_int), 'mmd_soft_destroy': ([P], None),
    'mmd_soft_add_collider': ([P, c.c_int, c.c_int, c.c_int, F, F], c.c_int),
    'mmd_soft_add': ([P, F, c.c_int, I, c.c_int, I, F, I, c.c_int, I, c.c_int], c.c_int),
    'mmd_soft_collider_poses': ([P, F, c.c_int], c.c_int),
    'mmd_soft_step': ([P, c.c_float], c.c_int), 'mmd_soft_sample': ([P, c.c_int, F, F, c.c_int], c.c_int),
}
for name, (arguments, result) in signatures.items():
    function = getattr(dll, name)
    function.argtypes, function.restype = arguments, result


def floats(values):
    return (c.c_float * len(values))(*values)


def ints(values):
    return (c.c_int * len(values))(*values)


def check(status):
    assert status == 0, dll.mmd_soft_error().decode()


def sample(world):
    points, normals = floats([0] * 9), floats([0] * 9)
    check(dll.mmd_soft_sample(world, 0, points, normals, 3))
    return list(points)


def create(anchored):
    world = P()
    check(dll.mmd_soft_create(floats([0, -9.8, 0]), 0, 0, c.byref(world)))
    try:
        for pose in ([0, 3, 0, 0, 0, 0, 1], [100, 100, 100, 0, 0, 0, 1]):
            check(dll.mmd_soft_add_collider(world, 0, 0, 0, floats([.1, 0, 0]), floats(pose)))
        check(dll.mmd_soft_add(world, floats([0, 3, 0, 1, 3, 0, 0, 3, 1]), 3,
            ints([0, 1, 2]), 3, ints([0, 0, 2, 0, 0, 0, 65535, 0, 4, 0, 4]),
            floats([1, .02, 1, .01, 0, 0, 0, 0, .2, 0, 1, .1, 1, .7, 1, .1, .5, .5, .5, .5, 1, 1, 1]),
            None, 0, ints([0, 0]) if anchored else None, 1 if anchored else 0))
        return world
    except BaseException:
        dll.mmd_soft_destroy(world)
        raise


assert dll.mmd_soft_abi() == 1 and dll.mmd_soft_features() & 8
report = dict(library=str(args.library.resolve()), sha256=hashlib.sha256(args.library.read_bytes()).hexdigest(),
    abi=dll.mmd_soft_abi(), features=dll.mmd_soft_features(), negativeCases=[])
# Compare full subsequent trajectories, so a failed batch cannot quietly leave proxy 0 changed.
invalid = {
    'wrong_count': ([2, 3, 0, 0, 0, 0, 1], 1),
    'null_payload': (None, 2),
    'last_nonfinite_position': ([2, 3, 0, 0, 0, 0, 1, float('nan'), 100, 100, 0, 0, 0, 1], 2),
    'last_nonfinite_rotation': ([2, 3, 0, 0, 0, 0, 1, 100, 100, 100, 0, 0, float('inf'), 1], 2),
    'last_zero_rotation': ([2, 3, 0, 0, 0, 0, 1, 100, 100, 100, 0, 0, 0, 0], 2),
    'last_nonunit_rotation': ([2, 3, 0, 0, 0, 0, 1, 100, 100, 100, 0, 0, 0, 2], 2),
}
for name, (payload, count) in invalid.items():
    changed, control = create(True), create(True)
    try:
        for _ in range(20):
            check(dll.mmd_soft_step(changed, 1 / 120)); check(dll.mmd_soft_step(control, 1 / 120))
        before = sample(changed)
        assert dll.mmd_soft_collider_poses(changed, floats(payload) if payload is not None else None, count) != 0, name
        reason = dll.mmd_soft_error().decode()
        assert sample(changed) == before, name
        for _ in range(40):
            check(dll.mmd_soft_step(changed, 1 / 120)); check(dll.mmd_soft_step(control, 1 / 120))
            assert sample(changed) == sample(control), name
        report['negativeCases'].append(dict(name=name, reason=reason, subsequentTrajectoryExact=True))
    finally:
        dll.mmd_soft_destroy(changed); dll.mmd_soft_destroy(control)

# A successful batch is not a soft reset: free cloth retains its exact falling trajectory.
changed, control = create(False), create(False)
try:
    for _ in range(30):
        check(dll.mmd_soft_step(changed, 1 / 120)); check(dll.mmd_soft_step(control, 1 / 120))
    before = sample(changed)
    check(dll.mmd_soft_collider_poses(changed, floats([200, 100, 100, 0, 0, 0, 1, 300, 100, 100, 0, 0, 0, 1]), 2))
    assert sample(changed) == before
    for _ in range(30):
        check(dll.mmd_soft_step(changed, 1 / 120)); check(dll.mmd_soft_step(control, 1 / 120))
        assert sample(changed) == sample(control)
    assert sample(changed)[1] < before[1]
    report['successfulBatchRetainsNodeVelocityAndTrajectory'] = True
finally:
    dll.mmd_soft_destroy(changed); dll.mmd_soft_destroy(control)

world = create(True)
try:
    check(dll.mmd_soft_collider_poses(world, floats([2, 3, 0, 0, 0, 0, 1, 100, 100, 100, 0, 0, 0, 1]), 2))
    for _ in range(120):
        check(dll.mmd_soft_step(world, 1 / 120))
    result = sample(world)
    assert abs(result[0] - 2) < .03, result
    report['anchorFollowsCommittedProxy'] = True
finally:
    dll.mmd_soft_destroy(world)

args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps(report, indent=2), encoding='utf-8')
print(json.dumps(report, indent=2))

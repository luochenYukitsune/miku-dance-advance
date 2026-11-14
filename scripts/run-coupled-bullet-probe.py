"""Actual same-world coupling probe; never builds native code."""
import argparse
import ctypes as c
import json
from pathlib import Path

class Vec(c.Structure):
    _fields_ = [(name, c.c_float) for name in ('x', 'y', 'z')]
class Pose(c.Structure):
    _fields_ = [('position', Vec)] + [(name, c.c_float) for name in ('qx', 'qy', 'qz', 'qw')]
class Body(c.Structure):
    _fields_ = [(name, c.c_int) for name in ('shape', 'mode', 'group', 'mask')] + [('pose', Pose), ('size', Vec)] + [(name, c.c_float) for name in ('mass', 'ld', 'ad', 'restitution', 'friction')]

def run(path):
    dll = c.CDLL(str(path))
    def bind(name, types, restype=c.c_int):
        f = getattr(dll, name); f.argtypes = types; f.restype = restype; return f
    ptr, fp, ip = c.c_void_p, c.POINTER(c.c_float), c.POINTER(c.c_int)
    features = bind('mmd_bullet_features', [])
    assert features() & 1, 'missing real coupled-world feature'
    create = bind('mmd_bullet_create_coupled', [Vec, c.c_int, c.c_float, c.POINTER(ptr)])
    destroy = bind('mmd_bullet_destroy', [ptr], None)
    addbody = bind('mmd_bullet_add_body', [ptr, c.POINTER(Body)])
    attach = bind('mmd_bullet_soft_create', [ptr, ip, c.c_int, c.POINTER(ptr)])
    release = bind('mmd_bullet_soft_destroy', [ptr, ptr])
    add = bind('mmd_soft_add', [ptr, fp, c.c_int, ip, c.c_int, ip, fp, ip, c.c_int, ip, c.c_int])
    targets = bind('mmd_soft_targets', [ptr, c.c_int, fp, c.c_int])
    step = bind('mmd_bullet_step', [ptr, c.c_float])
    sample = bind('mmd_soft_sample', [ptr, c.c_int, fp, fp, c.c_int])
    bodypose = bind('mmd_bullet_get_body_pose', [ptr, c.c_int, c.POINTER(Pose)])
    error = bind('mmd_bullet_last_error', [], c.c_char_p)
    serror = bind('mmd_soft_error', [], c.c_char_p)
    def check(status):
        assert status == 0, (error(), serror())
    def f(*xs): return (c.c_float * len(xs))(*xs)
    def i(*xs): return (c.c_int * len(xs))(*xs)
    world, soft = ptr(), ptr()
    try:
        check(create(Vec(0, 0, 0), 0, 0, c.byref(world)))
        b = Body(0, 1, 0, 0, Pose(Vec(0, 3, 0), 0, 0, 0, 1), Vec(.1, .1, .1), 1, 0, 0, 0, .5)
        check(addbody(world, c.byref(b)))
        check(attach(world, i(0), 1, c.byref(soft)))
        opts = i(1, 0, 0, 0, 0, 0, 0, 0, 4, 0, 4)
        vals = f(1, .02, 1, .01, 0, 0, 0, 0, .2, 0, 1, .1, 1, .7, 1, .1, .5, .5, .5, .5, 1, 1, 1)
        check(add(soft, f(2, 3, 0, 3, 3, 0), 2, i(), 0, opts, vals, i(1), 1, i(0, 0), 1))
        check(step(world, 1 / 120))
        observations = []
        for n in range(12):
            check(targets(soft, 0, f(2, 3, 0, 4, 3, 0), 2)); check(step(world, 1 / 120))
            p = Pose(); check(bodypose(world, 0, c.byref(p)))
            out, norms = f(*([0] * 6)), f(*([0] * 6)); check(sample(soft, 0, out, norms, 2))
            observations.append({'step': n + 1, 'actualRigidX': p.position.x, 'anchoredNodeX': out[0]})
        assert abs(observations[-1]['actualRigidX']) > .001, 'anchor impulse never reaches actual rigid'
        return {'dll': str(path), 'features': features(), 'observations': observations}
    finally:
        if soft: check(release(world, soft))
        if world: destroy(world)

if __name__ == '__main__':
    p = argparse.ArgumentParser(); p.add_argument('--dll', type=Path, required=True); p.add_argument('--output', type=Path, required=True); a = p.parse_args()
    try: result = dict(passed=True, **run(a.dll))
    except Exception as e: result = dict(passed=False, error=str(e))
    a.output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8'); print(json.dumps(result, indent=2))
    raise SystemExit(0 if result['passed'] else 1)

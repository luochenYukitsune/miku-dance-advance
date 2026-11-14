"""Native public ABI checks for actual bodies, Near, lifetime, and suspended history.
Run only after root's serialized build has finished. No compiler is launched.
"""
import argparse
import ctypes as c
import json
import math
from pathlib import Path
from runpy import run_path

common = run_path(str(Path(__file__).with_name('run-coupled-bullet-probe.py')))
Vec, Pose, Body = (common[name] for name in ('Vec', 'Pose', 'Body'))

class Joint(c.Structure):
    _fields_ = [(name, c.c_int) for name in ('body_a', 'body_b', 'type')] + [(name, Pose) for name in ('frame_a', 'frame_b')] + [(name, Vec) for name in ('linear_lower', 'linear_upper', 'angular_lower', 'angular_upper', 'linear_spring', 'angular_spring')]

class API:
    def __init__(self, path):
        self.dll = c.CDLL(str(path)); ptr, fp, ip = c.c_void_p, c.POINTER(c.c_float), c.POINTER(c.c_int)
        def bind(name, args, result=c.c_int):
            f = getattr(self.dll, name); f.argtypes = args; f.restype = result; return f
        self.create = bind('mmd_bullet_create_coupled', [Vec, c.c_int, c.c_float, c.POINTER(ptr)])
        self.destroy = bind('mmd_bullet_destroy', [ptr], None)
        self.addbody = bind('mmd_bullet_add_body', [ptr, c.POINTER(Body)])
        self.addjoint = bind('mmd_bullet_add_joint', [ptr, c.POINTER(Joint)])
        self.removebody = bind('mmd_bullet_remove_body', [ptr, c.c_int])
        self.attach = bind('mmd_bullet_soft_create', [ptr, ip, c.c_int, c.POINTER(ptr)])
        self.release = bind('mmd_bullet_soft_destroy', [ptr, ptr])
        self.add = bind('mmd_soft_add', [ptr, fp, c.c_int, ip, c.c_int, ip, fp, ip, c.c_int, ip, c.c_int])
        self.targets = bind('mmd_soft_targets', [ptr, c.c_int, fp, c.c_int])
        self.step = bind('mmd_bullet_step', [ptr, c.c_float])
        self.sample = bind('mmd_soft_sample', [ptr, c.c_int, fp, fp, c.c_int])
        self.pose = bind('mmd_bullet_get_body_pose', [ptr, c.c_int, c.POINTER(Pose)])
        self.velocity = bind('mmd_bullet_get_body_velocity', [ptr, c.c_int, c.POINTER(Vec), c.POINTER(Vec)])
        self.impulse = bind('mmd_bullet_apply_impulse', [ptr, c.c_int, Vec, Vec])
        self.kinematic = bind('mmd_bullet_set_body_kinematic', [ptr, c.c_int, c.c_int])
        self.near = bind('mmd_soft_attached_near', [ptr, ip, c.c_int])
        self.freeze = bind('mmd_soft_attached_frozen', [ptr, c.c_int])
        self.rebase = bind('mmd_soft_attached_rebase', [ptr, fp])
        self.softstep = bind('mmd_soft_step', [ptr, c.c_float])
        self.gravity = bind('mmd_soft_gravity', [ptr, fp])
        self.proxy = bind('mmd_soft_collider_pose', [ptr, c.c_int, fp])
        self.error = bind('mmd_bullet_last_error', [], c.c_char_p)
        self.serror = bind('mmd_soft_error', [], c.c_char_p)
    def check(self, result):
        assert result == 0, (self.error(), self.serror())
    def world(self, gravity=(0, 0, 0)):
        p = c.c_void_p(); self.check(self.create(Vec(*gravity), 0, 0, c.byref(p))); return p
    def binding(self, world, slots):
        p = c.c_void_p(); self.check(self.attach(world, ints(*slots), len(slots), c.byref(p))); return p
    def rigid(self, world, mode=1, position=(0, 3, 0), mask=0, radius=.1):
        desc = Body(0, mode, 0, mask, Pose(Vec(*position), 0, 0, math.sin(.2), math.cos(.2)), Vec(radius, radius, radius), 1, 0, 0, 0, .5)
        self.check(self.addbody(world, c.byref(desc)))
    def rope(self, binding, points=(2, 3, 0, 3, 3, 0), anchored=True, pins=(1,), mask=0, mass=1):
        opts = ints(1, 0, 0, 0, 0, 0, mask, 0, 4, 0, 4)
        parameters = values(); parameters[0] = mass
        self.check(self.add(binding, floats(*points), 2, ints(), 0, opts, parameters, ints(*pins), len(pins), ints(0, 0) if anchored else ints(), int(anchored)))
    def cloth(self, binding, mask=1, flags=0, height=0, drag=0):
        parameters = values(); parameters[4] = drag
        self.check(self.add(binding, floats(-.2, height, 0, .2, height, 0, 0, height, .3), 3, ints(0, 1, 2), 3,
                            ints(0, flags, 0, 1, 0, 0, mask, 0, 4, 0, 4), parameters, ints(), 0, ints(), 0))
    def positions(self, binding, count=2, index=0):
        out, normals = floats(*([0] * (count * 3))), floats(*([0] * (count * 3)))
        self.check(self.sample(binding, index, out, normals, count)); return list(out)
    def state(self, world):
        p, v, a = Pose(), Vec(), Vec(); self.check(self.pose(world, 0, c.byref(p))); self.check(self.velocity(world, 0, c.byref(v), c.byref(a)))
        return dict(position=[p.position.x, p.position.y, p.position.z], rotation=[p.qx, p.qy, p.qz, p.qw], linear=[v.x, v.y, v.z], angular=[a.x, a.y, a.z])

def floats(*xs): return (c.c_float * len(xs))(*xs)
def ints(*xs): return (c.c_int * len(xs))(*xs)
def values(): return floats(1, .02, 1, .01, 0, 0, 0, 0, .2, 0, 1, .1, 1, .7, 1, .1, .5, .5, .5, .5, 1, 1, 1)

def contracts(api):
    observations = {}
    for mode in (1, 2):
        worlds = [api.world(), api.world()]; bindings = []
        try:
            for w in worlds:
                api.rigid(w, mode=mode); b = api.binding(w, [0]); bindings.append(b); api.rope(b)
                api.check(api.impulse(w, 0, Vec(.2, 0, 0), Vec(0, 0, 0)))
            api.check(api.near(bindings[0], ints(0, 0, 0, 0), 1))
            initial = api.state(worlds[0]); api.positions(bindings[0]); assert api.state(worlds[0]) == initial
            for w in worlds: api.check(api.step(w, 1 / 120))
            near, control = (api.state(w) for w in worlds)
            assert near['position'] == api.positions(bindings[0])[:3]
            assert near['linear'] == control['linear'] and near['angular'] == control['angular']
            assert near['rotation'] == control['rotation']
            before_sample = api.state(worlds[0]); api.positions(bindings[0]); assert api.state(worlds[0]) == before_sample
            api.check(api.near(bindings[0], ints(), 0)); api.check(api.step(worlds[0], 1 / 120))
            after = api.state(worlds[0])
            expected = [near['position'][j] + near['linear'][j] / 120 for j in range(3)]
            assert max(abs(a - b) for a, b in zip(after['position'], expected)) < 2e-5
            observations[f'nearMode{mode}'] = dict(first=near, noNearControl=control, following=after)
        finally:
            for w, b in zip(worlds, bindings): api.check(api.release(w, b))
            for w in worlds: api.destroy(w)

    w = api.world(); b = None
    try:
        api.rigid(w); b = api.binding(w, [0]); api.rope(b)
        assert api.removebody(w, 0) != 0
        assert api.softstep(b, 1 / 120) != 0 and api.gravity(b, floats(0, 0, 0)) != 0
        assert api.proxy(b, 0, floats(0, 3, 0, 0, 0, 0, 1)) != 0
        observations['borrowedOwnerOperationsRejected'] = True
        api.check(api.release(w, b)); b = None; api.check(api.removebody(w, 0))
    finally:
        if b: api.check(api.release(w, b))
        api.destroy(w)

    worlds = [api.world((0, -9.8, 0)), api.world((0, -9.8, 0))]; bindings = []
    try:
        for w in worlds:
            b = api.binding(w, []); bindings.append(b); api.rope(b, anchored=False, pins=())
        for _ in range(8):
            for w in worlds: api.check(api.step(w, 1 / 120))
        before = api.positions(bindings[0]); api.check(api.freeze(bindings[0], 1))
        for _ in range(24): api.check(api.step(worlds[0], 1 / 120))
        assert api.positions(bindings[0]) == before
        api.check(api.freeze(bindings[0], 0))
        for w in worlds: api.check(api.step(w, 1 / 120))
        assert api.positions(bindings[0]) == api.positions(bindings[1])
        observations['frozenHistoryResumeExact'] = True
        before = api.positions(bindings[0]); assert api.rebase(bindings[0], floats(float('nan'), 0, 0, 0, 0, 0, 1)) != 0
        assert api.positions(bindings[0]) == before
        # A pure translation checks both displayed and previous positions, preserving velocity.
        api.check(api.rebase(bindings[0], floats(11, 7, -3, 0, 0, 0, 1)))
        for w in worlds: api.check(api.step(w, 1 / 120))
        actual, reference = api.positions(bindings[0]), api.positions(bindings[1])
        expected = [v + (11, 7, -3)[j % 3] for j, v in enumerate(reference)]
        assert max(abs(a - b) for a, b in zip(actual, expected)) < 3e-5
        observations['rebaseContinuesHistory'] = dict(actual=actual, expected=expected)
    finally:
        for w, b in zip(worlds, bindings): api.check(api.release(w, b))
        for w in worlds: api.destroy(w)
    return observations

def contact_contracts(api):
    observations = {}
    for mode in (0, 1):
        outcomes = []
        for mask in (0, 1):
            w = api.world(); b = None
            try:
                api.rigid(w, mode=mode, position=(0, -.5, 0), mask=1, radius=.35)
                b = api.binding(w, []); api.cloth(b, mask=mask)
                api.check(api.step(w, 1 / 120)); api.check(api.impulse(w, 0, Vec(0, 2, 0), Vec(0, 0, 0)))
                for _ in range(30): api.check(api.step(w, 1 / 120))
                outcomes.append(dict(rigid=api.state(w), cloth=api.positions(b, count=3)))
            finally:
                if b: api.check(api.release(w, b))
                api.destroy(w)
        if mode == 1:
            assert abs(outcomes[1]['rigid']['linear'][1] - outcomes[0]['rigid']['linear'][1]) > .01, 'cloth never reacts on actual finite-mass rigid'
            assert max(abs(a - b) for a, b in zip(outcomes[1]['cloth'], outcomes[0]['cloth'])) > .01, 'actual rigid never deforms cloth'
            assert max(abs(v) for v in outcomes[1]['rigid']['angular']) > .01, 'off-center soft contact never rotates the actual rigid'
        else:
            assert outcomes[0]['rigid'] == outcomes[1]['rigid']
        observations[f'contactMode{mode}'] = dict(masked=outcomes[0], enabled=outcomes[1])
    return observations

def frozen_contact_contracts(api):
    observations = {}
    for flags in (0, 2):
        for before_first_step in (False, True):
            outcomes = []
            for mask in (0, 1):
                w = api.world(); b = None
                try:
                    api.rigid(w, position=(0, -.5, 0), mask=1, radius=.35)
                    b = api.binding(w, []); api.cloth(b, mask=mask, flags=flags)
                    if not before_first_step: api.check(api.step(w, 1 / 120))
                    before = api.positions(b, count=3); api.check(api.freeze(b, 1))
                    api.check(api.impulse(w, 0, Vec(0, 2, 0), Vec(0, 0, 0)))
                    for _ in range(30): api.check(api.step(w, 1 / 120))
                    assert api.positions(b, count=3) == before, 'frozen collider nodes moved'
                    outcomes.append(dict(rigid=api.state(w), cloth=api.positions(b, count=3)))
                    api.check(api.freeze(b, 0)); api.check(api.step(w, 1 / 120))
                    assert all(math.isfinite(v) for v in api.positions(b, count=3)), 'unfreezing contact leaves nonfinite nodes'
                finally:
                    if b: api.check(api.release(w, b))
                    api.destroy(w)
            assert abs(outcomes[1]['rigid']['linear'][1] - outcomes[0]['rigid']['linear'][1]) > .01, f'frozen soft collider does not react on moving actual rigid: flags={flags}, beforeFirstStep={before_first_step}'
            observations[f'frozenContactFlags{flags}BeforeFirst{before_first_step}'] = dict(masked=outcomes[0], enabled=outcomes[1])
    return observations

def joint_contracts(api):
    outcomes = []
    for jointed in (False, True):
        w = api.world(); b = None
        try:
            api.rigid(w); api.rigid(w, mode=0)
            if jointed:
                frame = Pose(Vec(0, 0, 0), 0, 0, 0, 1)
                joint = Joint(0, 1, 0, frame, frame, *[Vec(0, 0, 0) for _ in range(6)])
                api.check(api.addjoint(w, c.byref(joint)))
            b = api.binding(w, [0]); api.rope(b, mass=.01); api.check(api.step(w, 1 / 120))
            for _ in range(12):
                api.check(api.targets(b, 0, floats(2, 3, 0, 4, 3, 0), 2)); api.check(api.step(w, 1 / 120))
            outcomes.append(api.state(w))
        finally:
            if b: api.check(api.release(w, b))
            api.destroy(w)
    assert abs(outcomes[0]['position'][0]) > .1
    assert abs(outcomes[1]['position'][0]) < abs(outcomes[0]['position'][0]) * .5, 'actual rigid joint never constrains soft anchor reaction'
    return dict(anchorWithActualRigidJoint=dict(unconstrained=outcomes[0], jointed=outcomes[1]))

def shared_soft_contact_contracts(api):
    observations = {}
    for flags in (0, 2):
        outcomes = []
        for mask in (0, 1):
            w = api.world((0, -9.8, 0)); bindings = []
            try:
                fixed = api.binding(w, []); bindings.append(fixed); api.cloth(fixed, flags=flags, mask=mask)
                falling = api.binding(w, []); bindings.append(falling); api.cloth(falling, flags=flags, height=.5)
                api.check(api.freeze(fixed, 1)); before = api.positions(fixed, count=3)
                for _ in range(60): api.check(api.step(w, 1 / 120))
                assert api.positions(fixed, count=3) == before, 'other binding moves frozen soft surface'
                outcomes.append(api.positions(falling, count=3))
                assert all(math.isfinite(v) for v in outcomes[-1])
            finally:
                for b in bindings: api.check(api.release(w, b))
                api.destroy(w)
        assert max(abs(a - b) for a, b in zip(*outcomes)) > .1, f'cross-binding soft contact ignored: flags={flags}'
        observations[f'sharedSoftContactFlags{flags}'] = dict(masked=outcomes[0], enabled=outcomes[1])
    return observations

def coincident_soft_contracts(api):
    observations = {}
    for flags in (0, 2):
        w = api.world((0, -1, 0)); bindings = []
        try:
            for _ in range(2):
                b = api.binding(w, []); bindings.append(b); api.cloth(b, flags=flags)
            for _ in range(16):
                api.check(api.step(w, 1 / 120))
                for b in bindings: assert all(math.isfinite(v) for v in api.positions(b, count=3)), 'coincident soft contacts produce nonfinite nodes'
            observations[f'coincidentClothFlags{flags}'] = [api.positions(b, count=3) for b in bindings]
        finally:
            for b in bindings: api.check(api.release(w, b))
            api.destroy(w)
    return observations

def legacy_rigid_contract(current, legacy):
    """Only ABI4 pre-existing exports: old clients keep the old creation path."""
    traces = []
    for path in (current, legacy):
        dll = c.CDLL(str(path)); ptr = c.c_void_p
        def bind(name, args, result=c.c_int):
            f = getattr(dll, name); f.argtypes=args; f.restype=result; return f
        create = bind('mmd_bullet_create',[Vec,c.c_int,c.c_float,c.POINTER(ptr)])
        destroy = bind('mmd_bullet_destroy',[ptr],None)
        add = bind('mmd_bullet_add_body',[ptr,c.POINTER(Body)])
        impulse = bind('mmd_bullet_apply_impulse',[ptr,c.c_int,Vec,Vec])
        step = bind('mmd_bullet_step',[ptr,c.c_float])
        pose = bind('mmd_bullet_get_body_pose',[ptr,c.c_int,c.POINTER(Pose)])
        world=ptr(); trace=[]
        try:
            # Contact arithmetic has its own original-MMD golden comparison.
            # Isolate the unchanged ABI4 creation/integration path here.
            assert create(Vec(0,-9.8,0),0,0,c.byref(world))==0
            body=Body(0,1,0,65535,Pose(Vec(0,3,0),0,0,0,1),Vec(.2,.2,.2),1,.03,.03,.5,.6)
            assert add(world,c.byref(body))==0
            assert impulse(world,0,Vec(.2,2,.1),Vec(.1,.2,.3))==0
            for _ in range(256):
                assert step(world,1/120)==0
                sample=Pose(); assert pose(world,0,c.byref(sample))==0; trace.append(bytes(sample))
            traces.append(trace)
        finally:
            if world:destroy(world)
    assert traces[0]==traces[1], 'ordinary ABI4 creation changed rigid trajectory'
    return dict(legacyAbi4OrdinaryNoContactTrajectoryExact=dict(frames=256, legacyDll=str(legacy)))

def frozen_preinit_drag_contract(api):
    worlds = [api.world((0,-9.8,0)),api.world((0,-9.8,0))]; bindings=[]
    try:
        for w in worlds:
            b=api.binding(w,[]); bindings.append(b)
            parameters=values();parameters[4]=1
            api.check(api.add(b,floats(-.2,5,0,.2,5,0,0,5,.3),3,ints(0,1,2),3,
                              ints(0,0,0,1,0,0,0,0,4,0,4),parameters,ints(0),1,ints(),0))
            # Targets can change before the first boundary. Lazy constants must
            # be captured from this geometry, not the initial model-rest area.
            api.check(api.targets(b,0,floats(-.2,5.3,0,.2,5,0,0,5,.3),3))
        api.check(api.freeze(bindings[0],1))
        for _ in range(20):api.check(api.step(worlds[0],1/120))
        api.check(api.freeze(bindings[0],0))
        for _ in range(60):
            for w in worlds:api.check(api.step(w,1/120))
        actual,control=[api.positions(b,count=3) for b in bindings]
        assert actual==control, f'freeze before first step changes aerodynamic node area: {actual} versus {control}'
        return dict(frozenBeforeFirstStepAerodynamicHistoryExact=True)
    finally:
        for w,b in zip(worlds,bindings):api.check(api.release(w,b))
        for w in worlds:api.destroy(w)

if __name__ == '__main__':
    p = argparse.ArgumentParser(); p.add_argument('--dll', type=Path, required=True); p.add_argument('--output', type=Path, required=True); p.add_argument('--legacy-rigid-dll', type=Path); a = p.parse_args()
    try:
        api = API(a.dll); observations = contracts(api); observations.update(contact_contracts(api)); observations.update(frozen_contact_contracts(api)); observations.update(joint_contracts(api)); observations.update(shared_soft_contact_contracts(api)); observations.update(coincident_soft_contracts(api))
        observations.update(frozen_preinit_drag_contract(api))
        if a.legacy_rigid_dll: observations.update(legacy_rigid_contract(a.dll,a.legacy_rigid_dll))
        report = dict(passed=True, observations=observations)
    except Exception as e: report = dict(passed=False, error=str(e)); import traceback; report['traceback'] = traceback.format_exc()
    a.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8'); print(json.dumps(report, indent=2)); raise SystemExit(0 if report['passed'] else 1)

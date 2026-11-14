"""Isolate native heap failures in a child process; no native build occurs."""
import argparse
import json
from pathlib import Path
import runpy
import subprocess
import sys

def child(dll):
    common = runpy.run_path(str(Path(__file__).with_name('run-coupled-bullet-contracts.py')))
    api = common['API'](dll); floats, ints, values = (common[n] for n in ('floats', 'ints', 'values'))
    worlds = [api.world((0, -9.8, 0)), api.world((0, -9.8, 0))]; bindings = []
    try:
        for w in worlds:
            b = api.binding(w, []); bindings.append(b)
            api.check(api.add(b, floats(0, 5, 0, 1, 5, 0, 0, 5, 1), 3, ints(0, 1, 2), 3,
                              ints(0, 2, 0, 1, 0, 0, 0, 0, 4, 0, 4), values(), ints(), 0, ints(), 0))
        for _ in range(8):
            for w in worlds: api.check(api.step(w, 1 / 120))
        before = api.positions(bindings[0], count=3); api.check(api.freeze(bindings[0], 1))
        for _ in range(20): api.check(api.step(worlds[0], 1 / 120))
        assert api.positions(bindings[0], count=3) == before
        print('Frozen cluster positions preserved; restoring history.', flush=True)
        api.check(api.freeze(bindings[0], 0))
        print('History restored; advancing next owner step.', flush=True)
        for w in worlds: api.check(api.step(w, 1 / 120))
        assert api.positions(bindings[0], count=3) == api.positions(bindings[1], count=3)
        print('Restored cluster history matches unadvanced control exactly.', flush=True)
    finally:
        for w, b in zip(worlds, bindings): api.check(api.release(w, b))
        for w in worlds: api.destroy(w)

if __name__ == '__main__':
    p = argparse.ArgumentParser(); p.add_argument('--dll', type=Path, required=True); p.add_argument('--output', type=Path); p.add_argument('--child', action='store_true'); a = p.parse_args()
    if a.child:
        child(a.dll); raise SystemExit(0)
    result = subprocess.run([sys.executable, __file__, '--dll', str(a.dll), '--child'], capture_output=True, text=True, timeout=30)
    report = dict(passed=result.returncode == 0, exitCode=result.returncode, stdout=result.stdout, stderr=result.stderr)
    if a.output: a.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(report, indent=2)); raise SystemExit(0 if report['passed'] else 1)

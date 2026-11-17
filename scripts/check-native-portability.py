"""Check that every native PE in a published MDA directory runs on a clean Windows 10 x64 machine.

1. Every import (including delay imports) resolves beside the file or to a Windows inbox DLL. Redistributable
   runtimes (VC++, legacy D3DX, MinGW) count only when shipped beside the file: CI machines have them installed.
2. DLLs compiled by Zig use only the x86-64 baseline instruction set. Zig defaults to the build machine's CPU,
   and CI runners are not user CPUs. MSVC output is not scanned: its CRT dispatches wider code at run time.
"""
import argparse, os, re, struct, sys
from pathlib import Path

ZIG_BUILT = ('mmd_bullet.dll', 'mmd_bullet_soft.dll', 'tools/classic/Mmd.Modern.D3D9.Compatibility.dll')
REDISTRIBUTABLE = re.compile(r'^(vcruntime\d+(_\d+)?d?|msvcp\d+(_\w+)?d?|vcomp\d+d?|concrt\d+d?|vccorlib\d+d?|mfc\d+\w*|msvcr\d+d?|ucrtbased'
                             r'|d3dx9_\d+|d3dx10(_\d+)?|d3dx11_\d+|d3dcompiler_(3\d|4[0-6])|xinput1_[123]|xaudio2_[0-7]|x3daudio1_\d+|xactengine\d_\d+'
                             r'|libgcc\w*|libstdc\+\+\w*|libwinpthread\w*|libc\+\+\w*|libunwind\w*|libomp\w*)\.dll$', re.I)
# Plain SSE2 code, plus tzcnt: it is encoded as rep bsf, which older CPUs execute with the same result for nonzero input.
BASELINE_GROUPS = {'JUMP', 'CALL', 'RET', 'INT', 'IRET', 'BRANCH_RELATIVE', 'PRIVILEGE', 'MODE64', 'NOT64BITMODE', 'CMOV', 'FPU', 'SSE1', 'SSE2'}


class Pe:
    def __init__(self, path):
        self.data = d = path.read_bytes()
        if len(d) < 64 or d[:2] != b'MZ': raise ValueError('not a PE file')
        pe = struct.unpack_from('<I', d, 0x3c)[0]
        if d[pe:pe + 4] != b'PE\0\0': raise ValueError('not a PE file')
        self.machine = struct.unpack_from('<H', d, pe + 4)[0]
        nsec, optsz = struct.unpack_from('<H', d, pe + 6)[0], struct.unpack_from('<H', d, pe + 20)[0]
        opt = pe + 24
        self.directories = opt + (112 if struct.unpack_from('<H', d, opt)[0] == 0x20b else 96)
        self.sections = [struct.unpack_from('<8sIIII', d, opt + optsz + i * 40) + struct.unpack_from('<I', d, opt + optsz + i * 40 + 36) for i in range(nsec)]

    def directory(self, index): return struct.unpack_from('<II', self.data, self.directories + index * 8)

    def offset(self, rva):
        for _, size, va, raw_size, raw, _ in self.sections:
            if va <= rva < va + max(size, raw_size): return rva - va + raw
        raise ValueError(f'RVA {rva:#x} is outside every section')

    def string(self, rva):
        start = self.offset(rva)
        return self.data[start:self.data.index(b'\0', start)].decode('ascii')

    @property
    def managed(self): return self.directory(14)[0] != 0

    def imports(self):
        for index, size, name_field in ((1, 20, 12), (13, 32, 4)):
            rva = self.directory(index)[0]
            if not rva: continue
            cursor = self.offset(rva)
            while (name := struct.unpack_from('<I', self.data, cursor + name_field)[0]):
                yield self.string(name)
                cursor += size

    def code(self):
        for _, size, va, raw_size, raw, flags in self.sections:
            if flags & 0x20000000: yield va, self.data[raw:raw + min(size, raw_size)]


def wider_instructions(pe):
    import capstone
    from capstone import x86
    groups = {getattr(x86, n): n[8:] for n in dir(x86) if n.startswith('X86_GRP_')}
    disassembler = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    disassembler.detail = disassembler.skipdata = True
    found = {}
    for va, code in pe.code():
        for instruction in disassembler.disasm(code, va):
            if instruction.id == 0 or instruction.mnemonic == 'tzcnt': continue
            tags = {groups.get(g, str(g)) for g in instruction.groups} | ({instruction.mnemonic.upper()} if instruction.mnemonic in ('lzcnt', 'popcnt', 'movbe') else set())
            for group in tags - BASELINE_GROUPS:
                found.setdefault(group, f'{instruction.address:#x} {instruction.mnemonic} {instruction.op_str}')
    return found


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument('published', type=Path)
    root = parser.parse_args().published.resolve()
    system = Path(os.environ.get('SystemRoot', r'C:\Windows')) / 'System32'
    inbox = {p.name.lower() for p in system.iterdir()} if system.is_dir() else None
    failures, checked = [], 0
    for path in sorted(p for p in root.rglob('*') if p.is_file() and p.suffix.lower() in ('.dll', '.exe')):
        relative = path.relative_to(root).as_posix()
        try: pe = Pe(path)
        except ValueError: continue
        if pe.managed: continue
        checked += 1
        if pe.machine != 0x8664: failures.append(f'{relative}: not an x64 image (machine {pe.machine:#x})')
        beside = {p.name.lower() for p in path.parent.iterdir()}
        for name in pe.imports():
            lower = name.lower()
            if lower in beside or (not REDISTRIBUTABLE.match(lower) and (lower.startswith(('api-ms-win-', 'ext-ms-')) or inbox is None or lower in inbox)):
                continue
            failures.append(f'{relative}: {name} is neither shipped beside it nor part of Windows')
    for relative in ZIG_BUILT:
        path = root / relative
        if not path.is_file(): failures.append(f'{relative}: missing'); continue
        for group, example in sorted(wider_instructions(Pe(path)).items()):
            failures.append(f'{relative}: uses {group} beyond the x86-64 baseline, e.g. {example}')
    for failure in failures: print('::error::' + failure if os.environ.get('GITHUB_ACTIONS') else 'FAIL ' + failure)
    print(f'Checked {checked} native images and {len(ZIG_BUILT)} Zig-built DLLs: {len(failures)} problem(s).')
    return 1 if failures else 0


if __name__ == '__main__':
    sys.exit(main())

#!/usr/bin/env python3
"""Scan a real MME effect corpus and extract the compatibility contract.

Read-only analysis: decodes Shift-JIS effect sources, pulls out the
technique annotations, MMDPass values, standard semantics, sampler state,
render state and MME render-target directives actually used in the wild.

Usage:
  python tools/corpus_scan.py <corpus-dir> [--json out.json]
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

FX_EXTS = {".fx", ".fxsub", ".fxh", ".fxm", ".inc"}

# fx_2_0 declaration with an explicit semantic attached.
SEMANTIC_RE = re.compile(
    r"^\s*(?P<type>[A-Za-z_][\w]*)\s+(?P<name>[A-Za-z_]\w*)\s*:\s*(?P<sem>[A-Z_][A-Z0-9_]*)\s*;",
    re.M,
)
# Global uniform without semantic (still binds by name in MME).
UNIFORM_RE = re.compile(
    r"^\s*(?P<type>float4x4|float4x3|float4|float3x3|float3|float2|float|int4|int|bool|half|half4|matrix)"
    r"\s+(?P<name>[A-Za-z_]\w*)\s*(?:=\s*(?P<default>[^;]+))?;",
    re.M,
)
TECH_RE = re.compile(r"\btechnique\s+(?P<name>[A-Za-z_]\w*)\s*(?:<(?P<ann>[^>]*)>)?\s*\{", re.S)
PASS_RE = re.compile(r"\bpass\s+(?P<name>[A-Za-z_]\w*)\s*(?:<(?P<ann>[^>]*)>)?\s*\{", re.S)
ANN_RE = re.compile(r"(?P<type>string|bool|int|float)\s+(?P<name>[A-Za-z_]\w*)\s*=\s*(?P<value>[^;]+);")
SAMPLER_STATE_RE = re.compile(r"\bsampler(?:1D|2D|3D|CUBE)?\s+(?P<name>[A-Za-z_]\w*)\s*=\s*sampler_state\s*\{(?P<body>[^}]*)\}", re.S)
STATE_ASSIGN_RE = re.compile(r"^\s*(?P<key>[A-Za-z_]\w*)\s*=\s*(?P<value>[^;]+);", re.M)
COMPILE_RE = re.compile(r"compile\s+(?P<profile>(?:vs|ps)_\d+_\d+)", re.I)
INCLUDE_RE = re.compile(r'#include\s+"(?P<path>[^"]+)"')
TEXTURE_DECL_RE = re.compile(r"\btexture\s+(?P<name>[A-Za-z_]\w*)\s*;")
DIRECTIVE_RE = re.compile(r"\b(RenderColorTarget\d|RenderDepthStencilTarget|ClearSetColor|ClearSetDepth|Clear|SetRenderTarget|BeginMmdDraw|EndMmdDraw)\b")


def decode(raw: bytes) -> tuple[str, str]:
    for enc in ("utf-8-sig", "cp932"):
        try:
            return raw.decode(enc), enc
        except UnicodeDecodeError:
            continue
    return raw.decode("cp932", errors="replace"), "cp932(replace)"


def strip_comments(text: str) -> str:
    text = re.sub(r"/\*.*?\*/", lambda m: "\n" * m.group(0).count("\n"), text, flags=re.S)
    return re.sub(r"//[^\n]*", "", text)


def scan_file(path: Path) -> dict:
    text, enc = decode(path.read_bytes())
    clean = strip_comments(text)
    lines = clean.splitlines()

    semantics: dict[str, str] = {}
    uniforms: list[str] = []
    for m in SEMANTIC_RE.finditer(clean):
        semantics[m.group("sem")] = f'{m.group("type")} {m.group("name")}'
    for m in UNIFORM_RE.finditer(clean):
        uniforms.append(f'{m.group("type")} {m.group("name")}')

    techniques = []
    for tm in TECH_RE.finditer(clean):
        ann = {a.group("name"): a.group("value").strip() for a in ANN_RE.finditer(tm.group("ann") or "")}
        # slice the technique body by brace matching
        body_start = tm.end() - 1
        depth, i = 0, body_start
        while i < len(clean):
            if clean[i] == "{":
                depth += 1
            elif clean[i] == "}":
                depth -= 1
                if depth == 0:
                    break
            i += 1
        body = clean[body_start:i]
        passes = []
        for pm in PASS_RE.finditer(body):
            passes.append({
                "name": pm.group("name"),
                "annotations": {a.group("name"): a.group("value").strip() for a in ANN_RE.finditer(pm.group("ann") or "")},
            })
        techniques.append({
            "name": tm.group("name"),
            "annotations": ann,
            "passes": passes,
            "profiles": sorted(set(p.group("profile").lower() for p in COMPILE_RE.finditer(body))),
        })

    samplers = {}
    for sm in SAMPLER_STATE_RE.finditer(clean):
        st = {s.group("key"): s.group("value").strip() for s in STATE_ASSIGN_RE.finditer(sm.group("body"))}
        if st:
            samplers[sm.group("name")] = st

    return {
        "path": str(path),
        "encoding": enc,
        "bytes": path.stat().st_size,
        "lines": len(lines),
        "includes": [m.group("path") for m in INCLUDE_RE.finditer(text)],
        "semantics": semantics,
        "uniforms": sorted(set(uniforms)),
        "textures": sorted(set(m.group("name") for m in TEXTURE_DECL_RE.finditer(clean))),
        "samplers": samplers,
        "directives": sorted(set(m.group(1) for m in DIRECTIVE_RE.finditer(clean))),
        "techniques": techniques,
    }


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("corpus")
    ap.add_argument("--json")
    args = ap.parse_args(argv)

    root = Path(args.corpus)
    files = sorted(p for p in root.rglob("*") if p.suffix.lower() in FX_EXTS)
    results = [scan_file(p) for p in files]

    agg = {
        "mmdpass": {},
        "technique_annotations": {},
        "semantics": {},
        "profiles": {},
        "directives": {},
        "sampler_keys": {},
        "encodings": {},
    }
    for r in results:
        agg["encodings"][r["encoding"]] = agg["encodings"].get(r["encoding"], 0) + 1
        for k in r["semantics"]:
            agg["semantics"][k] = agg["semantics"].get(k, 0) + 1
        for d in r["directives"]:
            agg["directives"][d] = agg["directives"].get(d, 0) + 1
        for sk in r["samplers"]:
            agg["sampler_keys"][sk] = agg["sampler_keys"].get(sk, 0) + 1
        for t in r["techniques"]:
            for p in t["profiles"]:
                agg["profiles"][p] = agg["profiles"].get(p, 0) + 1
            for ak, av in t["annotations"].items():
                agg["technique_annotations"][ak] = agg["technique_annotations"].get(ak, 0) + 1
            mp = t["annotations"].get("MMDPass") or t["annotations"].get("MMDPASS")
            if mp:
                key = mp.strip().strip('"')
                agg["mmdpass"][key] = agg["mmdpass"].get(key, 0) + 1

    payload = {"root": str(root), "file_count": len(results), "aggregate": agg, "files": results}
    if args.json:
        Path(args.json).write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")

    print(f"files={len(results)} encoding={agg['encodings']}")
    print(f"techniques={sum(len(r['techniques']) for r in results)}")
    print("\nMMDPass values:")
    for k, v in sorted(agg["mmdpass"].items(), key=lambda kv: -kv[1]):
        print(f"  {v:3d}  {k}")
    print("\nTechnique annotations:")
    for k, v in sorted(agg["technique_annotations"].items(), key=lambda kv: -kv[1]):
        print(f"  {v:3d}  {k}")
    print("\nShader profiles:")
    for k, v in sorted(agg["profiles"].items()):
        print(f"  {v:3d}  {k}")
    print("\nDirectives:")
    for k, v in sorted(agg["directives"].items(), key=lambda kv: -kv[1]):
        print(f"  {v:3d}  {k}")
    print("\nSemantics:")
    for k, v in sorted(agg["semantics"].items(), key=lambda kv: -kv[1]):
        print(f"  {v:3d}  {k}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))

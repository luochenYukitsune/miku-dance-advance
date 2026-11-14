#!/usr/bin/env python3
"""Extract the MME compatibility contract from a real effect corpus.

Unlike corpus_scan.py this understands the fx_2_0 annotation grammar properly:
annotation values may be a *concatenation of string literals that contain
semicolons* (that is how MME encodes its render-target "Script"), so a naive
`[^;]+` split truncates them.

Outputs a JSON contract plus a markdown report.
"""
from __future__ import annotations

import argparse
import json
import re
from collections import Counter, defaultdict
from pathlib import Path

FX_EXTS = {".fx", ".fxsub", ".fxh", ".fxm", ".inc"}


def decode(raw: bytes) -> str:
    for enc in ("utf-8-sig", "cp932"):
        try:
            return raw.decode(enc)
        except UnicodeDecodeError:
            pass
    return raw.decode("cp932", errors="replace")


def strip_comments(text: str) -> str:
    out, i, n = [], 0, len(text)
    while i < n:
        if text.startswith("/*", i):
            j = text.find("*/", i + 2)
            j = n if j < 0 else j + 2
            out.append("\n" * text.count("\n", i, j))
            i = j
        elif text.startswith("//", i):
            j = text.find("\n", i)
            i = n if j < 0 else j
        elif text[i] == '"':
            j = i + 1
            while j < n and text[j] != '"':
                j += 2 if text[j] == "\\" else 1
            out.append(text[i : j + 1])
            i = j + 1
        else:
            out.append(text[i])
            i += 1
    return "".join(out)


def match_brace(text: str, open_idx: int) -> int:
    depth, i, n = 0, open_idx, len(text)
    while i < n:
        c = text[i]
        if c == '"':
            i += 1
            while i < n and text[i] != '"':
                i += 2 if text[i] == "\\" else 1
        elif c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
            if depth == 0:
                return i
        i += 1
    return n - 1


def brace_depth(text: str, pos: int) -> int:
    depth = 0
    for i in range(pos):
        c = text[i]
        if c == "{":
            depth += 1
        elif c == "}":
            depth = max(0, depth - 1)
    return depth


def parse_annotations(body: str) -> dict[str, str]:
    """Parse `< string A = "x" "y"; bool B = true; >` into {'A': 'xy', 'B': 'true'}."""
    res: dict[str, str] = {}
    i, n = 0, len(body)
    pat = re.compile(r"\s*(string|bool|int|float)\s+([A-Za-z_]\w*)\s*=")
    while i < n:
        m = pat.match(body, i)
        if not m:
            break
        i = m.end()
        parts = []
        # value: string-literal concatenation, or a bare literal up to ';'
        while True:
            while i < n and body[i] in " \t\r\n":
                i += 1
            if i < n and body[i] == '"':
                j = i + 1
                while j < n and body[j] != '"':
                    j += 2 if body[j] == "\\" else 1
                parts.append(body[i + 1 : j])
                i = j + 1
                continue
            break
        if parts:
            res[m.group(2)] = "".join(parts)
        else:
            j = body.find(";", i)
            j = n if j < 0 else j
            res[m.group(2)] = body[i:j].strip()
            i = j
        j = body.find(";", i)
        i = n if j < 0 else j + 1
    return res


TECH_RE = re.compile(r"\btechnique\s+([A-Za-z_]\w*)", re.S)
PASS_RE = re.compile(r"\bpass\s+([A-Za-z_]\w*)\s*(?:<([^>]*)>)?\s*\{")
GLOBAL_RE = re.compile(
    r"^\s*(?P<type>[A-Za-z_]\w*)\s+(?P<name>[A-Za-z_]\w*)\s*:\s*(?P<sem>[A-Z_][A-Z0-9_]*)\s*(?:=[^;]*)?;",
    re.M,
)
TEX_RE = re.compile(r"^\s*texture\s+([A-Za-z_]\w*)\s*;", re.M)
TEXFILE_RE = re.compile(r'string\s+\w*[Ff]ile\w*\s*=\s*"([^"]+)"')
SCRIPT_CMD_RE = re.compile(r"\s*([A-Za-z_]\w*)\s*=\s*([^;]+);")
PROFILE_RE = re.compile(r"compile\s+((?:vs|ps)_\d+_\d+)", re.I)
INCLUDE_RE = re.compile(r'#include\s+"([^"]+)"')


def scan(path: Path, root: Path) -> dict:
    text = decode(path.read_bytes())
    clean = strip_comments(text)

    globals_ = {}
    member_sems = {}
    for m in GLOBAL_RE.finditer(clean):
        decl = f'{m.group("type")} {m.group("name")}'
        if brace_depth(clean, m.start()) == 0:
            globals_.setdefault(m.group("sem"), []).append(decl)
        else:
            member_sems.setdefault(m.group("sem"), []).append(decl)

    techniques = []
    for tm in TECH_RE.finditer(clean):
        j = tm.end()
        while j < len(clean) and clean[j] in " \t\r\n":
            j += 1
        annotations = {}
        if j < len(clean) and clean[j] == "<":
            depth, k = 1, j + 1
            while k < len(clean) and depth:
                if clean[k] == '"':
                    k += 1
                    while k < len(clean) and clean[k] != '"':
                        k += 2 if clean[k] == "\\" else 1
                elif clean[k] == "<":
                    depth += 1
                elif clean[k] == ">":
                    depth -= 1
                k += 1
            annotations = parse_annotations(clean[j + 1 : k - 1])
            j = k
        ob = clean.find("{", j)
        cb = match_brace(clean, ob)
        body = clean[ob : cb + 1]
        passes = []
        for pm in PASS_RE.finditer(body):
            passes.append({"name": pm.group(1), "annotations": parse_annotations(pm.group(2) or "")})
        techniques.append({
            "name": tm.group(1),
            "annotations": annotations,
            "passes": passes,
            "profiles": sorted({p.group(1).lower() for p in PROFILE_RE.finditer(body)}),
        })

    return {
        "path": str(path.relative_to(root)),
        "globals": globals_,
        "vertex_inputs": member_sems,
        "textures": sorted(set(TEX_RE.findall(clean))),
        "texture_files": sorted(set(TEXFILE_RE.findall(clean))),
        "includes": sorted(set(INCLUDE_RE.findall(text))),
        "techniques": techniques,
    }


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("corpus")
    ap.add_argument("--json", required=True)
    ap.add_argument("--md", required=True)
    a = ap.parse_args()

    root = Path(a.corpus)
    files = [scan(p, root) for p in sorted(root.rglob("*")) if p.suffix.lower() in FX_EXTS]

    pass_counter: Counter = Counter()
    ann_counter: Counter = Counter()
    global_sem: Counter = Counter()
    global_names: dict[str, Counter] = defaultdict(Counter)
    input_sem: Counter = Counter()
    input_names: dict[str, Counter] = defaultdict(Counter)
    cmd_counter: Counter = Counter()
    cmd_args: dict[str, Counter] = defaultdict(Counter)
    prof_counter: Counter = Counter()
    tech_without_pass: list[str] = []
    scripts: list[dict] = []

    for f in files:
        for sem, decls in f["globals"].items():
            global_sem[sem] += 1
            for d in decls:
                global_names[sem][d.split()[-1]] += 1
        for sem, decls in f["vertex_inputs"].items():
            input_sem[sem] += 1
            for d in decls:
                input_names[sem][d.split()[-1]] += 1
        for t in f["techniques"]:
            for k in t["annotations"]:
                ann_counter[k] += 1
            for p in t["profiles"]:
                prof_counter[p] += 1
            mp = t["annotations"].get("MMDPass")
            if mp is None:
                tech_without_pass.append(f'{f["path"]}::{t["name"]}')
            else:
                pass_counter[mp.replace('"', "").strip()] += 1
            script = t["annotations"].get("Script")
            if script:
                cmds = [{"cmd": c.group(1), "arg": c.group(2).strip()} for c in SCRIPT_CMD_RE.finditer(script)]
                scripts.append({"file": f["path"], "technique": t["name"], "commands": cmds})
                for c in cmds:
                    cmd_counter[c["cmd"]] += 1
                    cmd_args[c["cmd"]][c["arg"]] += 1

    payload = {
        "corpus": str(root),
        "file_count": len(files),
        "technique_count": sum(len(f["techniques"]) for f in files),
        "mmdpass": dict(pass_counter.most_common()),
        "technique_annotations": dict(ann_counter.most_common()),
        "script_commands": dict(cmd_counter.most_common()),
        "script_command_args": {k: dict(v.most_common()) for k, v in cmd_args.items()},
        "profiles": dict(prof_counter.most_common()),
        "global_semantics": dict(global_sem.most_common()),
        "global_names": {k: dict(v.most_common()) for k, v in global_names.items()},
        "vertex_input_semantics": dict(input_sem.most_common()),
        "vertex_input_names": {k: dict(v.most_common()) for k, v in input_names.items()},
        "script_techniques": scripts,
        "files": files,
    }
    Path(a.json).write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")

    L = []
    L.append("# MME corpus contract\n")
    L.append(f"Corpus: `{root}`  \nFiles: {len(files)}  \nTechniques: {payload['technique_count']}\n")
    L.append("## MMDPass values\n")
    L.append("| value | techniques |\n|---|---|")
    for k, v in pass_counter.most_common():
        L.append(f"| `{k}` | {v} |")
    L.append("\n## Technique annotations\n")
    L.append("| annotation | techniques |\n|---|---|")
    for k, v in ann_counter.most_common():
        L.append(f"| `{k}` | {v} |")
    L.append("\n## Script commands\n")
    L.append("| command | uses | example arguments |\n|---|---|---|")
    for k, v in cmd_counter.most_common():
        ex = ", ".join(f"`{x}`" for x, _ in cmd_args[k].most_common(4))
        L.append(f"| `{k}` | {v} | {ex} |")
    L.append("\n## Shader profiles\n")
    L.append("| profile | passes |\n|---|---|")
    for k, v in prof_counter.most_common():
        L.append(f"| `{k}` | {v} |")
    L.append("\n## Global uniforms by semantic\n")
    L.append("| semantic | files | names seen |\n|---|---|---|")
    for k, v in global_sem.most_common():
        names = ", ".join(f"`{x}`" for x, _ in global_names[k].most_common(3))
        L.append(f"| `{k}` | {v} | {names} |")
    L.append("\n## Vertex input semantics (struct members)\n")
    L.append("| semantic | files | names seen |\n|---|---|---|")
    for k, v in input_sem.most_common():
        names = ", ".join(f"`{x}`" for x, _ in input_names[k].most_common(3))
        L.append(f"| `{k}` | {v} | {names} |")
    if scripts:
        L.append("\n## Script render graphs\n")
        for s in scripts:
            cmds = " ".join(f'{c["cmd"]}={c["arg"]}' for c in s["commands"])
            L.append(f"- `{s['file']}::{s['technique']}` — {cmds}")
    if tech_without_pass:
        L.append("\n## Techniques without MMDPass\n")
        for t in tech_without_pass:
            L.append(f"- `{t}`")
    Path(a.md).write_text("\n".join(L) + "\n", encoding="utf-8")
    print(f"files={len(files)} techniques={payload['technique_count']} md={a.md}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

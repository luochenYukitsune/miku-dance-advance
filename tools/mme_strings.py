#!/usr/bin/env python3
"""Dump MMEffect.dll strings / exports relevant to the compatibility contract."""
from __future__ import annotations

import re
import sys
from pathlib import Path

dll = Path(sys.argv[1] if len(sys.argv) > 1 else r"../MMD932/MMEffect.dll")
b = dll.read_bytes()
s = b.decode("latin-1")
strs = sorted({m.group(0) for m in re.finditer(r"[\x20-\x7e]{4,}", s)})

sections = {
    "Exp* ABI": lambda t: t.startswith("Exp"),
    "Script": lambda t: re.search(r"Script|script", t) is not None,
    "Annotation-ish": lambda t: re.match(r"^(Use|Subset|Mmd|MMD|Effect|Clear|Render|Pass)", t) is not None,
    "Files/config": lambda t: re.search(r"\.(emm|fx|fxsub|fxm|ini|xml|map|txt)$|EFFECTMAP|EffectMap|\.ini", t, re.I) is not None,
    "Error/warn": lambda t: re.match(r"^(Error|Warning|Failed|Please)", t) is not None,
}

for title, pred in sections.items():
    print(f"\n===== {title}")
    for t in strs:
        if pred(t):
            print(" ", t[:160])

print("\n===== exports")
try:
    import pefile  # type: ignore

    pe = pefile.PE(str(dll), fast_load=True)
    pe.parse_data_directories(
        directories=[pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_EXPORT"]]
    )
    print("machine", hex(pe.FILE_HEADER.Machine))
    if hasattr(pe, "DIRECTORY_ENTRY_EXPORT"):
        for e in pe.DIRECTORY_ENTRY_EXPORT.symbols:
            print(" ", (e.name or f"ord{e.ordinal}".encode()).decode())
    else:
        print(" no export directory")
except ImportError:
    print(" pefile not installed; skipped")

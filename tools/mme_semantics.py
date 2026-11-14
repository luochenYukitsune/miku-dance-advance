#!/usr/bin/env python3
"""Locate MME semantic-name strings inside the shipped MMEffect.dll."""
from __future__ import annotations

import re
import sys
from pathlib import Path

dll = Path(sys.argv[1] if len(sys.argv) > 1 else r"../MMD932/MMEffect.dll")
s = dll.read_bytes().decode("latin-1")
strs = sorted({m.group(0) for m in re.finditer(r"[\x20-\x7e]{3,}", s)})

pat = re.compile(
    r"^(WORLD|VIEW|PROJECTION|MATERIAL|TOON|EDGE|GROUND|ADDING|MULTIPLY|VERTEX|TEXCOORD|LIGHT|CAMERA|TIME|POSITION|NORMAL|COLOR|BONE|MORPH)",
    re.I,
)
for t in strs:
    if pat.match(t) and " " not in t:
        print(" ", t[:120])

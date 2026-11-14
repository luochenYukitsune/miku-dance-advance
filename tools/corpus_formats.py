#!/usr/bin/env python3
"""List the texture format strings effects declare in their annotations."""
import collections
import pathlib
import re
import sys

root = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else r"D:\desktop\mmd渲染")
pattern = re.compile(r'string\s+Format\s*=\s*"([^"]+)"')
values = collections.Counter()
for path in root.rglob("*"):
    if path.suffix.lower() not in {".fx", ".fxsub", ".fxm", ".fxh"}:
        continue
    text = path.read_bytes().decode("cp932", "replace")
    for match in pattern.finditer(text):
        values[match.group(1)] += 1
for name, count in values.most_common():
    print(f"{count:4d}  {name}")

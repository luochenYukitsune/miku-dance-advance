#!/usr/bin/env python3
"""Review or migrate only approved static AXAML UI literals. Dry run is the default.

Usage: python -X utf8 scripts/localize-editor-static.py [--write] [--check]
Bound values, identities, icons, units and all source files outside the manifest allowlist survive.
The migration preserves AXAML formatting and is idempotent; it never traverses runtime controls.
"""
from __future__ import annotations
import argparse
import html
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CATALOG_ROOT = ROOT / "src/Mmd.Modern.App/Assets/Localization"
MANIFEST = CATALOG_ROOT / "manifest.json"
ATTRIBUTES = {"Text", "Header", "Content", "Watermark", "ToolTip.Tip", "Title",
              "AutomationProperties.Name", "AutomationProperties.HelpText", "PlaceholderText"}
ATTRIBUTE = re.compile(r'(?P<name>[\w.]+)(?P<space>\s*=\s*)(?P<quote>[\"\x27])(?P<value>.*?)(?P=quote)', re.DOTALL)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--write", action="store_true", help="Apply mapped static literals in allowlisted AXAML only")
    parser.add_argument("--check", action="store_true", help="Return nonzero if any Chinese UI literal lacks a mapping")
    args = parser.parse_args()
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    mapping = manifest["literalToKey"]
    unresolved = []
    replacements = 0
    for relative in manifest["axamlFiles"]:
        path = (ROOT / relative).resolve()
        if ROOT not in path.parents or path.suffix != ".axaml":
            raise ValueError(f"Unsafe manifest path: {relative}")
        raw = path.read_bytes()
        bom = raw.startswith(b"\xef\xbb\xbf")
        source = raw.decode("utf-8-sig")
        file_count = 0

        def replace(match: re.Match[str]) -> str:
            nonlocal file_count
            if match["name"] not in ATTRIBUTES:
                return match[0]
            literal = html.unescape(match["value"])
            if not literal or literal.startswith("{"):
                return match[0]
            key = mapping.get(literal)
            if key:
                file_count += 1
                return f'{match["name"]}{match["space"]}{match["quote"]}{{DynamicResource {key}}}{match["quote"]}'
            if re.search(r"[\u3400-\u9fff]", literal):
                line = source.count("\n", 0, match.start()) + 1
                unresolved.append(f"{relative}:{line}: {literal}")
            return match[0]

        updated = ATTRIBUTE.sub(replace, source)
        replacements += file_count
        if args.write and updated != source:
            path.write_bytes((b"\xef\xbb\xbf" if bom else b"") + updated.encode("utf-8"))
        print(f"{relative}: {file_count} {'migrated' if args.write else 'mapped'} static literals")
    print(f"Total: {replacements}; unmapped Chinese UI literals: {len(unresolved)}")
    for value in unresolved:
        print(value)
    return 1 if args.check and unresolved else 0


if __name__ == "__main__":
    raise SystemExit(main())

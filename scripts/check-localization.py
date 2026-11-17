"""Checks the three UI language catalogs before a build.

1. zh-CN, en and ja are valid JSON with exactly the same keys and no empty values.
2. Every literal `{DynamicResource Mda.Text.*}` used by an .axaml file exists in the catalogs.
3. `{0}`-style placeholders match across languages for each key.
Exit code 1 lists the first problems; nothing is modified.
"""
import json
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
CATALOGS = ROOT / 'src/Mmd.Modern.App/Assets/Localization'
LANGUAGES = ('zh-CN', 'en', 'ja')
PLACEHOLDER = re.compile(r'\{\d+(?::[^}]*)?\}')
REFERENCE = re.compile(r'\{DynamicResource (Mda\.Text\.[A-Za-z0-9_.]+)\}')


def main() -> int:
    problems: list[str] = []
    catalogs = {}
    for language in LANGUAGES:
        path = CATALOGS / f'{language}.json'
        try:
            catalogs[language] = json.loads(path.read_text(encoding='utf-8-sig'))
        except (OSError, ValueError) as error:
            problems.append(f'{path.name}: cannot read ({error})')
    if len(catalogs) == len(LANGUAGES):
        reference = set(catalogs['zh-CN'])
        for language, catalog in catalogs.items():
            for key in sorted(reference - set(catalog)):
                problems.append(f'{language}: missing {key}')
            for key in sorted(set(catalog) - reference):
                problems.append(f'{language}: extra {key} (not in zh-CN)')
            for key, value in catalog.items():
                if not isinstance(value, str) or not value.strip():
                    problems.append(f'{language}: empty value for {key}')
        for key in sorted(reference):
            shapes = {language: sorted(PLACEHOLDER.findall(catalogs[language].get(key, ''))) for language in LANGUAGES}
            if len({tuple(shape) for shape in shapes.values()}) > 1:
                problems.append(f'placeholders differ for {key}: {shapes}')
        used = 0
        for axaml in sorted((ROOT / 'src').rglob('*.axaml')):
            for key in REFERENCE.findall(axaml.read_text(encoding='utf-8-sig')):
                used += 1
                if key not in reference:
                    problems.append(f'{axaml.relative_to(ROOT).as_posix()}: unknown resource {key}')
        print(f'{len(reference)} keys x {len(LANGUAGES)} languages; {used} axaml references checked')
    for problem in problems[:200]:
        print('::error::' + problem if 'GITHUB_ACTIONS' in __import__('os').environ else problem)
    if len(problems) > 200:
        print(f'... and {len(problems) - 200} more')
    return 1 if problems else 0


if __name__ == '__main__':
    sys.exit(main())

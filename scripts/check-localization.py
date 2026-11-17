"""编译前检查三种界面语言的文案。

1. zh-CN、en、ja 都是合法 JSON，键完全一致，没有空值。
2. .axaml 里写死的 `{DynamicResource Mda.Text.*}` 在文案里都存在。
3. 每个键的 `{0}` 占位符在各语言间一致。
有问题时以退出码 1 结束并列出问题；不修改任何文件。
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
    sys.stdout.reconfigure(encoding='utf-8')
    problems: list[str] = []
    catalogs = {}
    for language in LANGUAGES:
        path = CATALOGS / f'{language}.json'
        try:
            catalogs[language] = json.loads(path.read_text(encoding='utf-8-sig'))
        except (OSError, ValueError) as error:
            problems.append(f'{path.name}：无法读取（{error}）')
    if len(catalogs) == len(LANGUAGES):
        reference = set(catalogs['zh-CN'])
        for language, catalog in catalogs.items():
            for key in sorted(reference - set(catalog)):
                problems.append(f'{language}：缺少 {key}')
            for key in sorted(set(catalog) - reference):
                problems.append(f'{language}：多出 {key}（zh-CN 中没有）')
            for key, value in catalog.items():
                if not isinstance(value, str) or not value.strip():
                    problems.append(f'{language}：{key} 的值为空')
        for key in sorted(reference):
            shapes = {language: sorted(PLACEHOLDER.findall(catalogs[language].get(key, ''))) for language in LANGUAGES}
            if len({tuple(shape) for shape in shapes.values()}) > 1:
                problems.append(f'{key} 的占位符各语言不一致：{shapes}')
        used = 0
        for axaml in sorted((ROOT / 'src').rglob('*.axaml')):
            for key in REFERENCE.findall(axaml.read_text(encoding='utf-8-sig')):
                used += 1
                if key not in reference:
                    problems.append(f'{axaml.relative_to(ROOT).as_posix()}：引用了不存在的文案 {key}')
        print(f'共 {len(reference)} 个键 × {len(LANGUAGES)} 种语言；检查了 {used} 处界面引用')
    for problem in problems[:200]:
        print('::error::' + problem if 'GITHUB_ACTIONS' in __import__('os').environ else problem)
    if len(problems) > 200:
        print(f'……另有 {len(problems) - 200} 处')
    return 1 if problems else 0


if __name__ == '__main__':
    sys.exit(main())

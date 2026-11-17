#!/usr/bin/env python3
"""从两个版本标签之间的 Git 提交日志（及可选 CHANGELOG.md）生成中英双语 Release 说明，默认中文在前。"""
from __future__ import annotations

import argparse
import os
import pathlib
import re
import subprocess

CJK_RE = re.compile(r'[\u4e00-\u9fff]')
TAG_VER_RE = re.compile(r'^v(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?$')


def git(*args: str, cwd: pathlib.Path) -> str:
    return subprocess.check_output(['git', '-C', str(cwd), *args], text=True, encoding='utf-8').strip()


def parse_semver(tag: str) -> tuple[int, ...] | None:
    m = TAG_VER_RE.fullmatch(tag.strip())
    if not m:
        return None
    return tuple(int(g or 0) for g in m.groups())


def find_previous_tag(repo: pathlib.Path, current_tag: str) -> str | None:
    raw = git('tag', '--list', 'v*', cwd=repo)
    cur_ver = parse_semver(current_tag)
    candidates: list[tuple[tuple[int, ...], str]] = []
    for line in raw.splitlines():
        t = line.strip()
        if not t or t == current_tag:
            continue
        ver = parse_semver(t)
        if ver is None:
            continue
        if cur_ver is None or ver < cur_ver:
            candidates.append((ver, t))
    if not candidates:
        return None
    candidates.sort()
    return candidates[-1][1]


def extract_changelog_section(repo: pathlib.Path, version: str) -> list[str]:
    changelog = repo / 'CHANGELOG.md'
    if not changelog.is_file():
        return []
    lines = changelog.read_text(encoding='utf-8').splitlines()
    collecting = False
    bullets: list[str] = []
    for raw in lines:
        s = raw.strip()
        if s.startswith('## '):
            header = s[3:].strip().lstrip('v')
            if header == version or header.startswith(version + ' '):
                collecting = True
                continue
            if collecting:
                break
        elif collecting and s.startswith('- '):
            bullets.append(s[2:].strip())
    return bullets


def parse_commits(repo: pathlib.Path, rev_range: str) -> list[dict[str, object]]:
    sep = '---MDA-COMMIT-SEP---'
    field_sep = '---MDA-FIELD-SEP---'
    raw = git('log', rev_range, '--no-merges', f'--format=%h{field_sep}%s{field_sep}%b{sep}', cwd=repo)
    commits: list[dict[str, object]] = []
    for chunk in raw.split(sep):
        chunk = chunk.strip()
        if not chunk:
            continue
        parts = chunk.split(field_sep, 2)
        if len(parts) < 2:
            continue
        sha = parts[0].strip()
        subject = parts[1].strip()
        body = parts[2].strip() if len(parts) > 2 else ''
        bullets: list[str] = []
        en_explicit: list[str] = []
        zh_explicit: list[str] = []
        cur: str | None = None
        for line in body.splitlines():
            s = line.strip()
            if not s or s.lower().startswith('co-authored-by:') or s.lower().startswith('generated with'):
                continue
            if re.match(r'^en\s*[:：]\s*', s, re.IGNORECASE):
                if cur:
                    bullets.append(cur)
                    cur = None
                en_explicit.append(re.sub(r'^en\s*[:：]\s*', '', s, flags=re.IGNORECASE).strip())
            elif re.match(r'^zh\s*[:：]\s*', s, re.IGNORECASE):
                if cur:
                    bullets.append(cur)
                    cur = None
                zh_explicit.append(re.sub(r'^zh\s*[:：]\s*', '', s, flags=re.IGNORECASE).strip())
            elif s.startswith(('- ', '* ')):
                if cur:
                    bullets.append(cur)
                cur = s[2:].strip()
            elif cur is not None:
                cur += (' ' if not CJK_RE.search(cur[-1:] + s[:1]) else '') + s
        if cur:
            bullets.append(cur)
        commits.append({
            'sha': sha,
            'subject': subject,
            'bullets': bullets,
            'zh_explicit': zh_explicit,
            'en_explicit': en_explicit,
        })
    return commits


EN_PREFIX_MAP = [
    ('界面：', 'UI update: '),
    ('画风：', 'Rendering style update: '),
    ('灯光：', 'Lighting update: '),
    ('修复：', 'Bug fix: '),
    ('重构：', 'Refactoring: '),
    ('本地化：', 'Localization update: '),
    ('文档：', 'Documentation update: '),
    ('测试：', 'Test suite update: '),
    ('性能：', 'Performance improvement: '),
    ('CI：', 'CI / Release pipeline update: '),
    ('Classic Toon：', 'Classic Toon update: '),
    ('Classic：', 'Classic engine update: '),
]


def fallback_en_for_zh_subject(sha: str, subject: str) -> str:
    for zh_pre, en_pre in EN_PREFIX_MAP:
        if subject.startswith(zh_pre):
            return f'{en_pre}`{sha}` ({subject})'
    return f'Update `{sha}`: {subject}'


def build_notes(version: str, prev_tag: str | None, commits: list[dict[str, object]],
                changelog_zh: list[str], extra_zh: str = '', extra_en: str = '') -> str:
    zh_items: list[str] = []
    en_items: list[str] = []

    if extra_zh.strip():
        for line in extra_zh.strip().splitlines():
            s = line.strip().lstrip('-* ').strip()
            if s:
                zh_items.append(s)
    if changelog_zh and not zh_items:
        zh_items.extend(changelog_zh)

    if extra_en.strip():
        for line in extra_en.strip().splitlines():
            s = line.strip().lstrip('-* ').strip()
            if s:
                en_items.append(s)

    need_zh_from_commits = not zh_items
    need_en_from_commits = not en_items

    for c in commits:
        sha = str(c['sha'])
        subj = str(c['subject'])
        bullets = [str(b) for b in c['bullets']]  # type: ignore[index]
        zh_exp = [str(b) for b in c['zh_explicit']]  # type: ignore[index]
        en_exp = [str(b) for b in c['en_explicit']]  # type: ignore[index]
        subj_is_zh = bool(CJK_RE.search(subj))

        if need_zh_from_commits:
            if zh_exp:
                zh_items.extend(zh_exp)
            elif subj_is_zh:
                zh_bullets = [b for b in bullets if CJK_RE.search(b)]
                if zh_bullets:
                    zh_items.append(f'**{subj}**（`{sha}`）')
                    for b in zh_bullets:
                        zh_items.append(f'  - {b}')
                else:
                    zh_items.append(f'{subj}（`{sha}`）')
            else:
                if bullets:
                    zh_items.append(f'提交 `{sha}`：{subj}')
                    for b in bullets:
                        zh_items.append(f'  - {b}')
                else:
                    zh_items.append(f'提交 `{sha}`：{subj}')

        if need_en_from_commits:
            if en_exp:
                en_items.extend(en_exp)
            elif not subj_is_zh:
                en_bullets = [b for b in bullets if not CJK_RE.search(b)]
                if en_bullets:
                    en_items.append(f'**{subj}** (`{sha}`)')
                    for b in en_bullets:
                        en_items.append(f'  - {b}')
                else:
                    en_items.append(f'{subj} (`{sha}`)')
            else:
                en_items.append(fallback_en_for_zh_subject(sha, subj))

    if not zh_items:
        zh_items.append(f'常规定期构建与维护更新（基于标签 `v{version}`）。')
    if not en_items:
        en_items.append(f'Maintenance and build update for `v{version}`.')

    def fmt_bullets(items: list[str]) -> str:
        out = []
        for item in items:
            if item.startswith('  - '):
                out.append(item)
            else:
                out.append(f'- {item}')
        return '\n'.join(out)

    range_note_zh = f'（自 `{prev_tag}` 以来的变更汇总）' if prev_tag else ''
    range_note_en = f' (changes since `{prev_tag}`)' if prev_tag else ''
    setup_name = f'MDA-{version}-win-x64-setup.exe'

    return f"""## 中文{range_note_zh}

{fmt_bullets(zh_items)}

**下载软件**：选择下方 `{setup_name}`。安装包内置现代渲染引擎（含 FSR / XeSS / DLSS 超分运行库）与 FFmpeg／ffprobe，传统（Classic）渲染引擎可在安装向导中按需勾选。

超分功能能否实际启用由运行时的显卡与驱动决定。内置 FFmpeg 的上游支持基线为 Windows 10 22H2。

FFmpeg 对应源码与构建材料随本次发布一并提供，供自行构建或合规查验使用，正常安装无需下载。需要时请同时下载两份 `dependency-sources` ZIP 和 `build-materials` ZIP 解压至同一目录。`SHA256SUMS.txt` 提供全部附件的 SHA-256 校验值。

[使用教程](https://luochenyukitsune.github.io/miku-dance-advance/guide/first-animation.html) · [文档站](https://luochenyukitsune.github.io/miku-dance-advance/)

## English{range_note_en}

{fmt_bullets(en_items)}

**Download** `{setup_name}` below. The installer bundles the Modern Direct3D 12 engine (including FSR / XeSS / DLSS upscaling runtimes) and FFmpeg/ffprobe; select the optional Classic engine during setup if needed.

Actual upscaler availability depends on your GPU and driver at runtime. The bundled FFmpeg build's upstream Windows support baseline is Windows 10 22H2.

FFmpeg corresponding source and build-material archives are attached for rebuilding or license compliance; normal installations do not need them. To rebuild FFmpeg, download both `dependency-sources` ZIPs and the `build-materials` ZIP into one directory and follow the included instructions. Attachment SHA-256 checksums are listed in `SHA256SUMS.txt`.
"""


def main() -> None:
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--repo-dir', type=pathlib.Path, default=pathlib.Path(__file__).resolve().parent.parent)
    p.add_argument('--tag', required=True, help='例如 v0.1.30')
    p.add_argument('--target-ref', default='HEAD', help='目标 Git 引用，默认 HEAD')
    p.add_argument('--previous-tag', default=os.environ.get('PREVIOUS_TAG', ''), help='指定上一版标签；留空则自动查找')
    p.add_argument('--extra-zh', default=os.environ.get('EXTRA_ZH', ''), help='可选：覆盖中文更新要点（多行）')
    p.add_argument('--extra-en', default=os.environ.get('EXTRA_EN', ''), help='可选：覆盖英文更新要点（多行）')
    p.add_argument('--output', type=pathlib.Path, required=True)
    args = p.parse_args()

    m = TAG_VER_RE.fullmatch(args.tag.strip())
    if not m:
        raise SystemExit(f'版本号标签格式不合法：{args.tag}（期望如 v0.1.30）')
    version = args.tag.strip()[1:]
    repo = args.repo_dir.resolve()
    prev_tag = args.previous_tag.strip() or find_previous_tag(repo, args.tag.strip())
    rev_range = f'{prev_tag}..{args.target_ref}' if prev_tag else args.target_ref

    commits = parse_commits(repo, rev_range)
    changelog_zh = extract_changelog_section(repo, version)
    notes = build_notes(version, prev_tag, commits, changelog_zh, args.extra_zh, args.extra_en)

    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(notes, encoding='utf-8')
    print(f'已生成双语更新说明：{args.output}（范围：{rev_range}，共 {len(commits)} 笔提交）')


if __name__ == '__main__':
    main()

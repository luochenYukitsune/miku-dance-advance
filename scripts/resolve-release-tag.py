#!/usr/bin/env python3
"""解析或自动递增发布版本号标签（如 v0.1.29 -> v0.1.30），并确定对比日志用的上一版标签。"""
from __future__ import annotations

import argparse
import json
import os
import pathlib
import re
import subprocess

TAG_RE = re.compile(r'^v?(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?(?:-([0-9A-Za-z][0-9A-Za-z._-]*))?$')
SUFFIX_RE = re.compile(r'^-?([0-9A-Za-z][0-9A-Za-z._-]*)$')


def normalize_tag(raw: str) -> str:
    s = raw.strip()
    m = TAG_RE.fullmatch(s)
    if not m:
        raise SystemExit(f'版本号标签格式不合法：{raw!r}（期望如 v0.1.30 或 v0.1.30-Inside）')
    return s if s.startswith('v') else f'v{s}'


def normalize_suffix(raw: str) -> str:
    s = raw.strip()
    if not s:
        return ''
    m = SUFFIX_RE.fullmatch(s)
    if not m:
        raise SystemExit(f'版本结尾后缀格式不合法：{raw!r}（期望以字母或数字开头，如 -Inside、-dev）')
    return m.group(1)


def parse_version_tuple(tag: str) -> tuple[tuple[int, int, int, int, int, str], int] | None:
    s = tag.strip()
    if not s.startswith('v'):
        return None
    m = TAG_RE.fullmatch(s)
    if not m:
        return None
    parts = [int(g) for g in m.groups()[:4] if g is not None]
    suffix = m.group(5) or ''
    padded = (parts[0], parts[1], parts[2], parts[3] if len(parts) > 3 else 0, 0 if suffix else 1, suffix)
    return padded, len(parts)


def collect_existing_tags(repo_dir: pathlib.Path, gh_repo: str) -> set[str]:
    found: set[str] = set()
    try:
        out = subprocess.check_output(
            ['git', '-C', str(repo_dir), 'tag', '--list', 'v*'],
            text=True, encoding='utf-8', stderr=subprocess.DEVNULL)
        for line in out.splitlines():
            if parse_version_tuple(line.strip()) is not None:
                found.add(line.strip())
    except Exception:
        pass

    if gh_repo and (os.environ.get('GH_TOKEN') or os.environ.get('GITHUB_TOKEN')):
        for endpoint in (f'repos/{gh_repo}/tags?per_page=100', f'repos/{gh_repo}/releases?per_page=100'):
            try:
                raw = subprocess.check_output(['gh', 'api', endpoint], text=True, encoding='utf-8', stderr=subprocess.DEVNULL)
                items = json.loads(raw)
                for item in items:
                    name = (item.get('tag_name') or item.get('name') or '').strip()
                    if parse_version_tuple(name) is not None and not item.get('draft', False):
                        found.add(name)
            except Exception:
                pass
    return found


def bump_last_segment(tag: str) -> str:
    parsed = parse_version_tuple(tag)
    if parsed is None:
        return 'v0.1.0'
    (maj, min_, pat, bld, _, _), count = parsed
    if count == 4:
        return f'v{maj}.{min_}.{pat}.{bld + 1}'
    return f'v{maj}.{min_}.{pat + 1}'


def main() -> None:
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--repo-dir', type=pathlib.Path, default=pathlib.Path(__file__).resolve().parent.parent)
    p.add_argument('--repo', default=os.environ.get('GITHUB_REPOSITORY', ''))
    p.add_argument('--tag', default=os.environ.get('INPUT_TAG', ''))
    p.add_argument('--suffix', default=os.environ.get('INPUT_VERSION_SUFFIX', ''))
    p.add_argument('--previous-tag', default=os.environ.get('INPUT_PREVIOUS_TAG', ''))
    p.add_argument('--write-github-output', action='store_true')
    args = p.parse_args()

    existing = collect_existing_tags(args.repo_dir.resolve(), args.repo.strip())
    ranked = sorted(
        ((parse_version_tuple(t)[0], t) for t in existing if parse_version_tuple(t) is not None),  # type: ignore[index]
        key=lambda x: x[0],
    )

    suffix = normalize_suffix(args.suffix)
    raw_tag = args.tag.strip()
    if raw_tag:
        resolved_tag = normalize_tag(raw_tag)
    elif ranked:
        resolved_tag = bump_last_segment(ranked[-1][1])
    else:
        resolved_tag = 'v0.1.0'

    resolved_parsed = parse_version_tuple(resolved_tag)
    if suffix and resolved_parsed is not None and not resolved_parsed[0][5]:
        resolved_tag = f'{resolved_tag}-{suffix}'

    raw_prev = args.previous_tag.strip()
    if raw_prev:
        previous_tag = normalize_tag(raw_prev)
    else:
        cur_tuple = parse_version_tuple(resolved_tag)[0]  # type: ignore[index]
        lower = [t for ver, t in ranked if ver < cur_tuple and t != resolved_tag]
        previous_tag = lower[-1] if lower else ''

    version = resolved_tag[1:]
    result = {'tag': resolved_tag, 'version': version, 'previous_tag': previous_tag}
    print(json.dumps(result, ensure_ascii=False))

    gh_out = os.environ.get('GITHUB_OUTPUT', '')
    if args.write_github_output and gh_out:
        with open(gh_out, 'a', encoding='utf-8') as f:
            f.write(f'tag={resolved_tag}\n')
            f.write(f'version={version}\n')
            f.write(f'previous_tag={previous_tag}\n')


if __name__ == '__main__':
    main()

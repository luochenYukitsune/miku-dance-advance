#!/usr/bin/env python3
"""收集安装包与固定版本 FFmpeg GPL 源码包，生成 SHA256SUMS.txt，并直接创建/更新公开的 GitHub Pre-release。"""
from __future__ import annotations

import argparse
import hashlib
import json
import pathlib
import re
import shutil
import subprocess

FFMPEG_SOURCE_ASSETS = (
    'FFmpeg-9.0.2-dependency-sources-1.zip',
    'FFmpeg-9.0.2-dependency-sources-2.zip',
    'FFmpeg-9.0.2-build-materials.zip',
    'ffmpeg-n9.0.2-57-gd838f9f9ae-win64-gpl-9.0.zip',
)


def gh(*args: str) -> str:
    return subprocess.check_output(['gh', *args], text=True, encoding='utf-8')


def sha256_file(path: pathlib.Path) -> str:
    h = hashlib.sha256()
    with path.open('rb') as f:
        for chunk in iter(lambda: f.read(4 * 1024 * 1024), b''):
            h.update(chunk)
    return h.hexdigest()


def find_donor_release(repo: str, current_tag: str) -> tuple[str, dict[str, str]] | None:
    releases = json.loads(gh('api', f'repos/{repo}/releases?per_page=50'))
    for rel in releases:
        if rel.get('tag_name') == current_tag or rel.get('draft'):
            continue
        assets = {a['name']: a.get('digest', '') for a in rel.get('assets', [])}
        if all(name in assets for name in FFMPEG_SOURCE_ASSETS[:3]):
            return rel['tag_name'], assets
    return None


def main() -> None:
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--repo', required=True)
    p.add_argument('--tag', required=True)
    p.add_argument('--commit', required=True)
    p.add_argument('--installer', type=pathlib.Path, required=True)
    p.add_argument('--notes', type=pathlib.Path, required=True)
    p.add_argument('--staging-dir', type=pathlib.Path, required=True)
    args = p.parse_args()

    if not re.fullmatch(r'v\d+\.\d+\.\d+(?:\.\d+)?', args.tag):
        raise SystemExit(f'非法的发布标签：{args.tag}')
    version = args.tag[1:]
    expected_installer_name = f'MDA-{version}-win-x64-setup.exe'
    installer = args.installer.resolve()
    if not installer.is_file() or installer.name != expected_installer_name:
        raise SystemExit(f'安装包文件名或路径不匹配：{installer}（期望 {expected_installer_name}）')

    notes_text = args.notes.read_text(encoding='utf-8')
    if '## 中文' not in notes_text or '## English' not in notes_text or notes_text.index('## 中文') > notes_text.index('## English'):
        raise SystemExit('发布说明必须包含 "## 中文" 和 "## English"，且中文在前。')

    stage = args.staging_dir.resolve()
    if stage.exists():
        shutil.rmtree(stage)
    stage.mkdir(parents=True)

    staged_files: list[pathlib.Path] = []
    staged_installer = stage / expected_installer_name
    shutil.copy2(installer, staged_installer)
    staged_files.append(staged_installer)

    donor = find_donor_release(args.repo, args.tag)
    if not donor:
        raise SystemExit('未在历史 Release 中找到可复用的 FFmpeg 9.0.2 对应源码包附件。')
    donor_tag, donor_digests = donor
    print(f'从历史版本 {donor_tag} 复用固定版本 FFmpeg 源码附件…')
    for name in FFMPEG_SOURCE_ASSETS:
        if name not in donor_digests:
            continue
        subprocess.run(['gh', 'release', 'download', donor_tag, '-R', args.repo, '-p', name, '-D', str(stage)], check=True)
        downloaded = stage / name
        actual = sha256_file(downloaded)
        expected_digest = donor_digests[name]
        if expected_digest.startswith('sha256:') and actual != expected_digest[7:].lower():
            raise SystemExit(f'复用附件 {name} 哈希校验不符：{actual} != {expected_digest}')
        staged_files.append(downloaded)

    checksum_lines = [f'{sha256_file(f)}  {f.name}' for f in sorted(staged_files, key=lambda x: x.name)]
    sums_file = stage / 'SHA256SUMS.txt'
    sums_file.write_text('\n'.join(checksum_lines) + '\n', encoding='utf-8')
    upload_list = [str(f) for f in staged_files] + [str(sums_file)]

    existing = subprocess.run(['gh', 'release', 'view', args.tag, '-R', args.repo, '--json', 'id'],
                              capture_output=True, text=True)
    title = f'MDA {version}'
    if existing.returncode == 0:
        subprocess.run(['gh', 'release', 'upload', args.tag, *upload_list, '--clobber', '-R', args.repo], check=True)
        subprocess.run([
            'gh', 'release', 'edit', args.tag, '-R', args.repo,
            '--title', title, '--notes-file', str(args.notes.resolve()),
            '--draft=false', '--prerelease'
        ], check=True)
    else:
        subprocess.run([
            'gh', 'release', 'create', args.tag, *upload_list, '-R', args.repo,
            '--target', args.commit, '--title', title,
            '--notes-file', str(args.notes.resolve()), '--prerelease'
        ], check=True)

    rel_info = json.loads(gh('release', 'view', args.tag, '-R', args.repo, '--json', 'url,isDraft,isPrerelease,assets'))
    if rel_info.get('isDraft') or not rel_info.get('isPrerelease'):
        raise SystemExit('Release 状态不符合预期（应为公开的 Pre-release）。')
    remote_assets = {a['name']: a.get('digest', '') for a in rel_info.get('assets', [])}
    for f in staged_files:
        expected = 'sha256:' + sha256_file(f)
        if remote_assets.get(f.name) != expected:
            raise SystemExit(f'远端附件哈希核验失败：{f.name}')
    print('预发布版已公开：' + rel_info['url'])


if __name__ == '__main__':
    main()

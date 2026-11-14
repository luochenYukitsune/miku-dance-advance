"""Export tracked source without local history or evidence. Never uploads or deletes.

Destination must be new. Findings show paths and lines, never credential values.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess

ROOT_FILES = {'.gitattributes', '.gitignore', 'Directory.Build.props', 'global.json',
              'Mmd.Modern.slnx', 'NuGet.Config', 'LICENSE', 'LICENSING.md', 'THIRD_PARTY_NOTICES.md', 'CHANGELOG.md'}
SOURCE_ROOTS = {'src', 'tests', 'native', 'third_party', 'installer', 'examples', 'fixtures', 'scripts', 'tools'}
PUBLIC_DOC_ROOTS = ('docs/guide/', 'docs/development/', 'docs/site/', 'docs/assets/css/', 'docs/_data/', 'docs/_layouts/')
PUBLIC_DOC_FILES = {'docs/index.md', 'docs/_config.yml', 'docs/LICENSE-Minimal.txt'}
SECRET_PATTERNS = {
    'credential': re.compile(rb'(?i)(?:sk-[A-Za-z0-9_-]{20,}|gh[pousr]_[A-Za-z0-9]{25,}|github_pat_[A-Za-z0-9_]{25,}|-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----)'),
    'local-token-url': re.compile(rb'https?://(?:127\.0\.0\.1|localhost)[^\s<>"\x27]*[?&]token=[^\s<>"\x27]+'),
}


def selected(path, include_readme, include_workflows=False):
    if path in ROOT_FILES or (include_readme and path == 'README.md'):
        return True
    if path in PUBLIC_DOC_FILES or path.startswith(PUBLIC_DOC_ROOTS):
        return True
    if path.startswith('.github/workflows/'):
        return include_workflows
    if path.startswith('compatibility/d3d9-mme-host/'):
        return True
    return path.split('/')[0] in SOURCE_ROOTS


def prepare(source, destination, report, include_readme, include_workflows=False):
    if destination.exists():
        raise ValueError('Destination exists; choose a new snapshot directory.')
    source = source.resolve(strict=True)
    paths = subprocess.check_output(['git', '-C', str(source), 'ls-files', '-z']).decode('utf-8').split('\0')
    missing = (ROOT_FILES | ({'README.md'} if include_readme else set())).difference(paths)
    if missing:
        raise ValueError('Track required files first: ' + ', '.join(sorted(missing)))
    entries, excluded, findings = [], [], []
    for relative in sorted(filter(None, paths)):
        if not selected(relative, include_readme, include_workflows):
            excluded.append(relative)
            continue
        path = source / relative
        if path.is_symlink() or source not in path.resolve(strict=True).parents:
            raise ValueError('Nonlocal or symbolic source: ' + relative)
        data = path.read_bytes()
        for kind, pattern in SECRET_PATTERNS.items():
            for match in pattern.finditer(data):
                findings.append(dict(path=relative, kind=kind, line=data[:match.start()].count(b'\n') + 1))
        if path.suffix.lower() in {'.exe', '.dll', '.pma', '.pmc', '.mp4', '.wav', '.zip', '.7z'}:
            findings.append(dict(path=relative, kind='unexpected-binary-payload'))
        entries.append(dict(path=relative, bytes=len(data), sha256=hashlib.sha256(data).hexdigest()))
    result = dict(schema='mda.public-source.v1', sourceCommit=subprocess.check_output(
        ['git', '-C', str(source), 'rev-parse', 'HEAD'], text=True).strip(),
        sourceWorktreeChanges=subprocess.check_output(['git', '-C', str(source), 'status', '--porcelain'], text=True).splitlines(),
        destination=str(destination.resolve()), includeReadme=include_readme, includeWorkflows=include_workflows, files=entries,
        excluded=excluded, findings=findings, passed=not findings)
    report.parent.mkdir(parents=True, exist_ok=True)
    if findings:
        report.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
        raise ValueError('Source scan rejected snapshot; inspect finding paths in report.')
    destination.mkdir(parents=True)
    for entry in entries:
        data = (source / entry['path']).read_bytes()
        if hashlib.sha256(data).hexdigest() != entry['sha256']:
            raise ValueError('Source changed during export: ' + entry['path'])
        target = destination / entry['path']
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
    report.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    return dict(files=len(entries), excluded=len(excluded), bytes=sum(e['bytes'] for e in entries), report=str(report))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, default=Path(__file__).resolve().parent.parent)
    parser.add_argument('--destination', type=Path, required=True)
    parser.add_argument('--report', type=Path, required=True)
    parser.add_argument('--include-readme', action='store_true', help='Include after DSH text has been integrated.')
    parser.add_argument('--include-workflows', action='store_true', help='Requires GitHub authorization with workflow scope when pushing.')
    args = parser.parse_args()
    print(json.dumps(prepare(args.source, args.destination, args.report, args.include_readme, args.include_workflows)))

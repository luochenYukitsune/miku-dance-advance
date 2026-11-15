"""Publish a draft after its complete, hash-bound assets are present."""
import argparse, json, re, subprocess

def gh(*args):
    return subprocess.check_output(['gh', *args])

p = argparse.ArgumentParser()
p.add_argument('--repo', required=True)
p.add_argument('--tag', required=True)
p.add_argument('--commit', required=True)
a = p.parse_args()
if not re.fullmatch(r'v\d+\.\d+\.\d+(?:\.\d+)?', a.tag):
    raise SystemExit('Invalid release tag')
releases = json.loads(gh('api', f'repos/{a.repo}/releases?per_page=100'))
r = next((r for r in releases if r['tag_name'] == a.tag), None)
if not r:
    raise SystemExit('Prepare the draft installer and source attachments first.')
if not r['draft']:
    print('Release is already public; no changes made.')
    raise SystemExit(0)
if r['target_commitish'] != a.commit:
    raise SystemExit('Draft targets a different source commit; build that commit before publishing.')
assets = {x['name']: x for x in r['assets']}
required = {f'MDA-{a.tag[1:]}-win-x64-setup.exe', 'SHA256SUMS.txt',
            'FFmpeg-9.0.2-dependency-sources-1.zip', 'FFmpeg-9.0.2-dependency-sources-2.zip',
            'FFmpeg-9.0.2-build-materials.zip'}
if not required <= assets.keys():
    raise SystemExit('Missing release attachments: ' + ', '.join(sorted(required - assets.keys())))
body = r.get('body') or ''
if '## 中文' not in body or '## English' not in body or body.index('## 中文') > body.index('## English'):
    raise SystemExit('Release notes must contain Chinese first, followed by English.')
sums = gh('api', assets['SHA256SUMS.txt']['url'], '-H', 'Accept: application/octet-stream').decode('utf-8')
digests = {}
for line in sums.splitlines():
    match = re.fullmatch(r'([a-fA-F0-9]{64})  ([^/\\]+)', line)
    if not match or match[2] in digests:
        raise SystemExit('Invalid or duplicate checksum entry')
    digests[match[2]] = match[1].lower()
for name in required - {'SHA256SUMS.txt'}:
    asset = assets[name]
    if name not in digests or asset.get('digest') != 'sha256:' + digests[name] or not asset['size']:
        raise SystemExit('Asset digest mismatch: ' + name)
gh('api', f'repos/{a.repo}/releases/{r["id"]}', '--method', 'PATCH', '-F', 'draft=false', '-F', 'prerelease=true')
print(r['html_url'])

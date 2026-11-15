"""Reuse the ordinary CI result for this exact commit instead of rebuilding it."""
import argparse,json,subprocess,time
p=argparse.ArgumentParser()
p.add_argument('--repo',required=True)
p.add_argument('--commit',required=True)
a=p.parse_args()
for attempt in range(120):
    data=json.loads(subprocess.check_output(['gh','api',f'repos/{a.repo}/actions/workflows/build.yml/runs?head_sha={a.commit}&per_page=20']))
    runs=[r for r in data['workflow_runs'] if r['head_sha']==a.commit and r['event'] in ('push','workflow_dispatch')]
    if runs:
        latest=max(runs,key=lambda r:r['id'])
        if latest['status']=='completed':
            if latest['conclusion']!='success':
                raise SystemExit('Source build failed: '+latest['html_url'])
            print('Using successful build: '+latest['html_url'])
            break
    if attempt%4==0: print('Waiting for source build of '+a.commit,flush=True)
    time.sleep(15)
else: raise SystemExit('Timed out waiting for the source build.')

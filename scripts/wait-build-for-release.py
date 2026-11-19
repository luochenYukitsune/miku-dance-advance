"""Reuse the ordinary CI result for this exact commit instead of rebuilding it."""
import argparse
import json
import subprocess
import time


def is_build_ignored_path(path: str) -> bool:
    """Match paths ignored by build.yml (`paths-ignore: ['docs/**', '*.md']`)."""
    normalized = path.strip().replace('\\', '/')
    if not normalized:
        return False
    return normalized.startswith('docs/') or ('/' not in normalized and normalized.endswith('.md'))


def changed_files_for_commit(commit: str) -> list[str] | None:
    """Return paths touched by `commit` relative to its first parent, or None on git failure."""
    try:
        raw = subprocess.check_output(
            ['git', '-c', 'core.quotepath=false', 'diff-tree', '--no-commit-id', '--name-only', '-r', '--root', '-m', '--first-parent', commit],
            text=True,
            encoding='utf-8',
            stderr=subprocess.DEVNULL,
        )
    except (subprocess.CalledProcessError, OSError):
        return None
    return [line for line in raw.splitlines() if line]


def resolve_effective_code_commit(commit: str) -> tuple[str, list[str]]:
    """Walk first-parent history when a commit only edits build-ignored docs/markdown paths."""
    try:
        raw = subprocess.check_output(
            ['git', 'rev-list', '--first-parent', '--max-count=50', commit],
            text=True,
            encoding='utf-8',
            stderr=subprocess.DEVNULL,
        )
    except (subprocess.CalledProcessError, OSError):
        return commit, []

    ignored: list[str] = []
    for sha in (line.strip() for line in raw.splitlines() if line.strip()):
        files = changed_files_for_commit(sha)
        if files is None:
            return commit, []
        if files and all(is_build_ignored_path(f) for f in files):
            for f in files:
                if f not in ignored:
                    ignored.append(f)
            continue
        return (sha, ignored) if ignored else (commit, [])

    return commit, []


def query_build_runs(repo: str, commit_sha: str) -> list[dict]:
    """Fetch push / workflow_dispatch runs of build.yml for `commit_sha`."""
    raw = subprocess.check_output(
        ['gh', 'api', f'repos/{repo}/actions/workflows/build.yml/runs?head_sha={commit_sha}&per_page=20'],
        text=True,
        encoding='utf-8',
    )
    data = json.loads(raw)
    return [
        r for r in data.get('workflow_runs', [])
        if r.get('head_sha') == commit_sha and r.get('event') in ('push', 'workflow_dispatch')
    ]


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument('--repo', required=True)
    parser.add_argument('--commit', required=True)
    parser.add_argument('--timeout-minutes', type=float, default=35.0)
    parser.add_argument('--no-run-grace-seconds', type=float, default=90.0)
    args = parser.parse_args()

    poll_seconds = 15.0
    deadline = time.monotonic() + max(60.0, args.timeout_minutes * 60.0)
    grace_deadline = time.monotonic() + max(15.0, args.no_run_grace_seconds)
    attempt = 0

    effective_commit, ignored_files = resolve_effective_code_commit(args.commit)
    if effective_commit != args.commit:
        preview = ', '.join(ignored_files[:5]) + ('...' if len(ignored_files) > 5 else '')
        print(
            f'Commit {args.commit} only touches build.yml paths-ignore files ({preview}); '
            f'will accept build.yml result for {args.commit} or latest non-ignored ancestor {effective_commit}.',
            flush=True,
        )

    while time.monotonic() < deadline:
        try:
            checked_commit = args.commit
            runs = query_build_runs(args.repo, args.commit)
            if not runs and effective_commit != args.commit:
                checked_commit = effective_commit
                runs = query_build_runs(args.repo, effective_commit)
        except (subprocess.CalledProcessError, json.JSONDecodeError) as error:
            print(f'Transient error querying CI runs for {args.commit}: {error}', flush=True)
            time.sleep(poll_seconds)
            attempt += 1
            continue

        if runs:
            latest = max(runs, key=lambda r: r.get('id', 0))
            status = latest.get('status')
            conclusion = latest.get('conclusion')
            if status == 'completed':
                if conclusion != 'success':
                    raise SystemExit(
                        f"同提交“构建与测试”未通过（{conclusion}）/ Source build failed ({conclusion}) for {checked_commit}: {latest.get('html_url')}"
                    )
                print(f"Using successful build ({checked_commit}): {latest.get('html_url')}", flush=True)
                break
            if attempt % 4 == 0:
                print(f"Waiting for source build {latest.get('id')} ({status}) of {checked_commit}", flush=True)
        else:
            if time.monotonic() >= grace_deadline:
                candidates = args.commit if effective_commit == args.commit else f'{args.commit} or {effective_commit}'
                raise SystemExit(
                    f'未找到提交 {candidates} 对应的“构建与测试”运行记录；若该提交仅改文档触发路径忽略或确需紧急跳过，请在手动触发时勾选 skip_build_check。'
                    f' / No build.yml run found for {candidates} within {int(args.no_run_grace_seconds)}s.'
                )
            if attempt % 2 == 0:
                print(f'Waiting for build.yml run to appear for {args.commit}', flush=True)

        attempt += 1
        time.sleep(poll_seconds)
    else:
        raise SystemExit(f'等待提交 {args.commit}“构建与测试”超时 / Timed out waiting for the source build.')


if __name__ == '__main__':
    main()

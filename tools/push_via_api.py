#!/usr/bin/env python3
"""通过 api.github.com 推送本地提交 —— 绕开被间歇性封掉的 github.com。

为什么需要它
------------
这台机器上 `github.com:443` 是**间歇性不通**的（实测同一 IP 几秒内能在 0/12 与 12/12 之间跳变），
而 `api.github.com` 稳定在 0.4 秒。git 的 smart-HTTP 协议只能走 github.com，所以 `git push`
会随机失败、重试也不保证。这个脚本改用 GitHub 的 **REST / Git Data API** 把本地提交原样搬上去 ——
走的全是 api.github.com。

保存历史的方式
--------------
按 `git rev-list --reverse HEAD` 逐个提交重放：每个提交都带着**原始的 author / committer / 日期 /
提交信息**去建 commit。GitHub 的 blob / tree / commit 用的是同一套 SHA-1 算法，所以只要内容一致，
远端每个提交的 sha 会和本地**逐个相等** —— 脚本最后会把这个核对报出来。

用法
----
    # 先干跑，只读本地、不碰网络
    python tools/push_via_api.py --dry-run

    # 真推（token 只从环境变量读，不会出现在命令行/历史里）
    GITHUB_TOKEN=ghp_xxx python tools/push_via_api.py

    # 只想推一个快照提交、不要 66 条历史（快很多：约 400 次请求 vs 1100 次）
    GITHUB_TOKEN=ghp_xxx python tools/push_via_api.py --squash

token 需要 `repo` 权限（经典 PAT）或 Contents: Read and write（细粒度 PAT）。
"""
from __future__ import annotations

import argparse
import base64
import json
import os
import subprocess
import sys
import time
import urllib.error
import urllib.request
from concurrent.futures import ThreadPoolExecutor

API = "https://api.github.com"
DEFAULT_REPO = "yangdong19961116-cyber/whalelog"
DEFAULT_BRANCH = "main"
BLOB_WORKERS = 6


# --------------------------------------------------------------------------- #
# 本地 git
# --------------------------------------------------------------------------- #

def git(*args: str, cwd: str, binary: bool = False):
    p = subprocess.run(["git", *args], cwd=cwd, capture_output=True)
    if p.returncode != 0:
        raise SystemExit(f"git {' '.join(args)} 失败：{p.stderr.decode('utf-8', 'replace').strip()}")
    return p.stdout if binary else p.stdout.decode("utf-8")


def read_commits(cwd: str) -> list[dict]:
    """把本地历史读成 [{sha, parents, message, author, committer, entries}]。"""
    shas = git("rev-list", "--reverse", "HEAD", cwd=cwd).split()
    out = []
    for sha in shas:
        meta = git("show", "-s", "--format=%an%x00%ae%x00%aI%x00%cn%x00%ce%x00%cI%x00%B", sha, cwd=cwd)
        an, ae, ad, cn, ce, cd, message = meta.split("\x00", 6)

        parents = git("show", "-s", "--format=%P", sha, cwd=cwd).split()

        # -z 让路径原样输出（中文文件名不会被转义成 \346\234\210）
        raw = git("-c", "core.quotepath=false", "ls-tree", "-r", "-z", sha, cwd=cwd, binary=True)
        entries = []
        for rec in raw.split(b"\x00"):
            if not rec:
                continue
            meta_part, _, path = rec.partition(b"\t")
            mode, otype, osha = meta_part.decode().split()
            entries.append({
                "path": path.decode("utf-8"),
                "mode": mode,
                "type": "commit" if otype == "commit" else "blob",
                "sha": osha,
            })

        out.append({
            "sha": sha,
            "parents": parents,
            "message": message.rstrip("\n"),
            "author": {"name": an, "email": ae, "date": ad},
            "committer": {"name": cn, "email": ce, "date": cd},
            "entries": entries,
        })
    return out


# --------------------------------------------------------------------------- #
# api.github.com
# --------------------------------------------------------------------------- #

class ApiError(Exception):
    def __init__(self, code: int, body: str):
        super().__init__(f"HTTP {code}: {body[:400]}")
        self.code = code
        self.body = body


def api(method: str, path: str, token: str, payload=None, ok=(200, 201)):
    """一次 API 调用，对网断/限流/5xx 自动退避重试。"""
    url = API + path
    data = json.dumps(payload).encode("utf-8") if payload is not None else None

    last = None
    for attempt in range(5):
        req = urllib.request.Request(url, data=data, method=method)
        req.add_header("Authorization", f"Bearer {token}")
        req.add_header("Accept", "application/vnd.github+json")
        req.add_header("X-GitHub-Api-Version", "2022-11-28")
        req.add_header("User-Agent", "whalelog-push-via-api")
        if data:
            req.add_header("Content-Type", "application/json")
        try:
            with urllib.request.urlopen(req, timeout=120) as resp:
                if resp.status not in ok:
                    raise ApiError(resp.status, resp.read().decode("utf-8", "replace"))
                body = resp.read()
                return json.loads(body) if body else {}
        except urllib.error.HTTPError as ex:
            body = ex.read().decode("utf-8", "replace")
            last = ApiError(ex.code, body)
            # 401/403 是配置问题，重试没意义；422 交给调用方判断
            if ex.code in (401, 403, 422):
                raise last
            time.sleep(min(2 ** attempt, 15))
        except (urllib.error.URLError, TimeoutError, ConnectionError) as ex:
            last = ApiError(0, str(ex))
            time.sleep(min(2 ** attempt, 15))
    raise last


# --------------------------------------------------------------------------- #

def main() -> int:
    ap = argparse.ArgumentParser(description="通过 api.github.com 推送本地提交")
    ap.add_argument("--repo", default=DEFAULT_REPO, help=f"owner/name（默认 {DEFAULT_REPO}）")
    ap.add_argument("--branch", default=DEFAULT_BRANCH, help=f"目标分支（默认 {DEFAULT_BRANCH}）")
    ap.add_argument("--cwd", default=None, help="仓库目录（默认当前目录）")
    ap.add_argument("--token", default=None, help="PAT（默认读 $GITHUB_TOKEN / $GH_TOKEN）")
    ap.add_argument("--squash", action="store_true", help="只推一个快照提交，不要完整历史")
    ap.add_argument("--dry-run", action="store_true", help="只读本地，不碰网络，打印将要发生的事")
    args = ap.parse_args()

    cwd = args.cwd or git("rev-parse", "--show-toplevel", cwd=os.getcwd()).strip()

    token = args.token or os.environ.get("GITHUB_TOKEN") or os.environ.get("GH_TOKEN")
    if not token and not args.dry_run:
        print("缺少 token：请用 GITHUB_TOKEN=ghp_xxx 传进来。", file=sys.stderr)
        return 2

    commits = read_commits(cwd)
    total_blobs = len({e["sha"] for c in commits for e in c["entries"] if e["type"] == "blob"})

    print(f"仓库     : {cwd}")
    print(f"远端     : {args.repo}  分支 {args.branch}")
    print(f"本地历史 : {len(commits)} 个提交，去重后 {total_blobs} 个 blob")
    print(f"HEAD     : {commits[-1]['sha']}")

    if args.squash:
        head = commits[-1]
        message = "\n\n".join(
            [f"chore: 首次推送（{len(commits)} 个本地提交压成一个快照）", "---"]
            + [f"* {c['sha'][:8]} {c['message'].splitlines()[0]}" for c in commits]
        )
        commits = [{
            "sha": head["sha"],
            "parents": [],
            "message": message,
            "author": head["author"],
            "committer": head["committer"],
            "entries": head["entries"],
        }]
        print(f"模式     : --squash，压成 1 个提交（{len(head['entries'])} 个文件）")

    if args.dry_run:
        print("\n[dry-run] 不做任何网络请求。计划：")
        for c in commits:
            print(f"  {c['sha'][:8]}  {len(c['entries']):4d} 文件  {c['message'].splitlines()[0][:60]}")
        print("\n去掉 --dry-run 并带上 GITHUB_TOKEN 即可真推。")
        return 0

    # ---- 1. blob ---------------------------------------------------------- #
    blob_map: dict[str, str] = {}
    todo = sorted({e["sha"] for c in commits for e in c["entries"] if e["type"] == "blob"})

    print(f"\n[1/4] 上传 {len(todo)} 个 blob …")
    done = 0
    lock_report = time.time()

    def upload(sha: str) -> tuple[str, str]:
        content = git("cat-file", "blob", sha, cwd=cwd, binary=True)
        b64 = base64.b64encode(content).decode("ascii")
        res = api("POST", f"/repos/{args.repo}/git/blobs", token,
                  {"content": b64, "encoding": "base64"})
        return sha, res["sha"]

    with ThreadPoolExecutor(max_workers=BLOB_WORKERS) as pool:
        for sha, new_sha in pool.map(upload, todo):
            blob_map[sha] = new_sha
            done += 1
            if time.time() - lock_report > 3 or done == len(todo):
                print(f"      {done}/{len(todo)}", flush=True)
                lock_report = time.time()

    # ---- 2/3. tree + commit（按历史顺序） --------------------------------- #
    print(f"\n[2/4] 建 tree / commit（{len(commits)} 个）…")
    sha_map: dict[str, str] = {}
    for i, c in enumerate(commits, 1):
        tree_entries = []
        for e in c["entries"]:
            item = {"path": e["path"], "mode": e["mode"], "type": e["type"]}
            item["sha"] = e["sha"] if e["type"] == "commit" else blob_map[e["sha"]]
            tree_entries.append(item)

        tree = api("POST", f"/repos/{args.repo}/git/trees", token, {"tree": tree_entries})

        payload = {
            "message": c["message"],
            "tree": tree["sha"],
            "parents": [sha_map[p] for p in c["parents"]],
            "author": c["author"],
            "committer": c["committer"],
        }
        commit = api("POST", f"/repos/{args.repo}/git/commits", token, payload)
        sha_map[c["sha"]] = commit["sha"]

        same = "✓ 与本地一致" if commit["sha"] == c["sha"] else "≠ 与本地不同"
        print(f"      {i}/{len(commits)}  {commit['sha'][:8]}  {same}  {c['message'].splitlines()[0][:44]}")

    head_remote = sha_map[commits[-1]["sha"]]

    # ---- 4. ref ----------------------------------------------------------- #
    print(f"\n[3/4] 更新 refs/heads/{args.branch} → {head_remote[:8]} …")
    try:
        api("POST", f"/repos/{args.repo}/git/refs", token,
            {"ref": f"refs/heads/{args.branch}", "sha": head_remote})
        print("      已创建分支")
    except ApiError as ex:
        if ex.code != 422:
            raise
        api("PATCH", f"/repos/{args.repo}/git/refs/heads/{args.branch}", token,
            {"sha": head_remote, "force": True})
        print("      分支已存在，已更新")

    # ---- 核对 ------------------------------------------------------------- #
    print("\n[4/4] 核对远端 …")
    ref = api("GET", f"/repos/{args.repo}/git/ref/heads/{args.branch}", token)
    remote_sha = ref["object"]["sha"]
    local_sha = commits[-1]["sha"]

    print(f"      远端 {args.branch} = {remote_sha}")
    print(f"      本地 HEAD      = {local_sha}")
    if remote_sha == local_sha:
        print("\n完成 —— 远端与本地**完全一致**。之后 `git fetch origin` 就能对上号。")
    else:
        print("\n完成 —— 但 sha 不同（--squash 模式属正常，历史被压成一个提交了）。")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        print("\n已中断", file=sys.stderr)
        sys.exit(130)

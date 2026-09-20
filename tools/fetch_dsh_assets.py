#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""从 GitHub 拉取 dsh-pet 的源素材（透明 webm）到本地，供转码工具消费。

为什么单独一个脚本、而不是让 convert_dsh_webm.py 自己下载：
拉取是**一次性、要联网**的事，转码是**反复跑、不联网**的事。
混在一起的话，每次调转码参数都要重新碰网络，改一个参数等一次下载。

用法（仓库根目录）：

    python tools/fetch_dsh_assets.py                    # 拉到 .tmp-dsh-assets/
    python tools/fetch_dsh_assets.py --out <目录>
    python tools/fetch_dsh_assets.py --list             # 只看清单，不下载
    python tools/fetch_dsh_assets.py --force            # 已存在也重下

## 通道（--via，默认 auto）

本机 2026-09-20 实测，四条路差别极大，所以**默认走镜像**：

| 通道 | 端点 | 实测 |
|---|---|---|
| `mirror`（默认） | jsDelivr 等 GitHub CDN 镜像，逐文件 | **1.0 MB/s**，52 MB 约 1 分钟 ✅ |
| `archive` | `codeload.github.com` 整仓归档 | 46 KB/s —— 要下 142 MB，约 50 分钟 ❌ |
| `raw` | `raw.githubusercontent.com` 逐文件 | **不通**（10s 超时，连 README 都取不到） |
| `api` | `api.github.com` 逐文件 base64 | 能通，但**未认证限流 60 次/小时**，106 个文件必然打爆 |

`auto` = 镜像 → 失败的文件落回 raw → 再不行落回 archive。

⚠️ 本机网络环境很特殊：**`github.com` 与 `raw.githubusercontent.com` 不通，
但 `api.github.com` 与几个镜像通**。换设备时先跑 `--via mirror --list` 探一下，
别默认"网上能下就一定能下"。

## 产物结构

保留源仓库的目录层级，转码工具靠这个层级认动作类别：

    <out>/assets/characters/<形象>/videos/
        manifest.json
        idle/待机呼吸休闲.webm
        click/点击回应-开心跃动.webm
        random/哈欠连天.webm
        ...

只拉 assets/characters/ 下的素材 —— 仓库里还有一堆表情包 jpg/png
（assets/big_blue_fat_fish/，约 20 MB），那些跟桌宠动画无关，不拉。

⚠️ 许可：素材是 CC BY-NC-SA 类同人素材，仅限个人非商业使用，须署名。
详见随包下下来的 THIRD_PARTY_NOTICES.md。
"""

from __future__ import annotations

import argparse
import concurrent.futures
import json
import pathlib
import sys
import tarfile
import urllib.error
import urllib.parse
import urllib.request

REPO = "MerZlin/dsh-pet-indesktop"
BRANCH = "main"

ARCHIVE_URL = f"https://codeload.github.com/{REPO}/tar.gz/refs/heads/{BRANCH}"
RAW_BASE = f"https://raw.githubusercontent.com/{REPO}/{BRANCH}/"
API_BASE = f"https://api.github.com/repos/{REPO}/contents/"
API_TREE = f"https://api.github.com/repos/{REPO}/git/trees/{BRANCH}?recursive=1"

# 镜像按实测速度排序；同一个文件依次尝试，前一个失败才走下一个。
# jsDelivr 会缓存 GitHub 内容，@main 表示取默认分支最新。
MIRROR_TEMPLATES = (
    "https://fastly.jsdelivr.net/gh/{repo}@{branch}/",
    "https://cdn.jsdelivr.net/gh/{repo}@{branch}/",
    "https://gh-proxy.com/https://raw.githubusercontent.com/{repo}/{branch}/",
    "https://ghproxy.net/https://raw.githubusercontent.com/{repo}/{branch}/",
)

ROOT = pathlib.Path(__file__).resolve().parents[1]
DEFAULT_OUT = ROOT / ".tmp-dsh-assets"

WANT_PREFIX = "assets/characters/"
WANT_SUFFIXES = (".webm", ".json")
EXTRA_DOCS = ("THIRD_PARTY_NOTICES.md",)

USER_AGENT = "whalelog-asset-fetcher"


def mirror_bases() -> list[str]:
    return [t.format(repo=REPO, branch=BRANCH) for t in MIRROR_TEMPLATES]


def http_get(url: str, timeout: int = 60) -> bytes:
    req = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        return resp.read()


def wanted_path(path: str) -> bool:
    return path.startswith(WANT_PREFIX) and path.lower().endswith(WANT_SUFFIXES)


# ======================================================================
# 清单：走 API 的文件树（一次请求，额度够）
# ======================================================================

_TREE_CACHE = ROOT / ".tmp-dsh-tree.json"


def list_assets() -> list[tuple[str, int]]:
    try:
        raw = http_get(API_TREE, timeout=60)
        _TREE_CACHE.write_bytes(raw)
    except Exception as e:
        if _TREE_CACHE.exists():
            print(f"（取文件树失败：{e}；用上次缓存的清单）")
            raw = _TREE_CACHE.read_bytes()
        else:
            raise

    data = json.loads(raw.decode("utf-8"))
    if data.get("truncated"):
        print("⚠️  文件树被截断，清单可能不全", file=sys.stderr)
    return [(e["path"], e.get("size", 0)) for e in data.get("tree", [])
            if e.get("type") == "blob" and wanted_path(e["path"])]


# ======================================================================
# 通道：镜像 / raw（逐文件，并发）
# ======================================================================

def download_via_bases(bases: list[str], path: str) -> tuple[bytes | None, str]:
    """依次尝试若干 base，返回 (内容, 说明)。全部失败返回 (None, 最后一次的错误)。"""
    quoted = urllib.parse.quote(path)
    last = ""
    for base in bases:
        try:
            return http_get(base + quoted), ""
        except urllib.error.HTTPError as e:
            last = f"HTTP {e.code}"
        except Exception as e:
            last = f"{type(e).__name__}"
    return None, last


def fetch_concurrent(items: list[tuple[str, int]], out_root: pathlib.Path,
                     force: bool, listing_only: bool, bases: list[str],
                     jobs: int) -> list[tuple[str, int]]:
    results: list[tuple[str, int]] = []
    failures: list[str] = []

    def one(item: tuple[str, int]) -> tuple[str, str, bytes | None]:
        path, size = item
        dest = out_root / path
        if not force and dest.exists() and dest.stat().st_size == size:
            return path, "skip", None
        blob, err = download_via_bases(bases, path)
        return path, err or "ok", blob

    with concurrent.futures.ThreadPoolExecutor(max_workers=jobs) as pool:
        for i, (path, status, blob) in enumerate(pool.map(one, items), 1):
            if status == "skip":
                results.append((path, 0))
            elif blob is None:
                failures.append(f"{path}（{status}）")
                print(f"  ❌ {path}：{status}", file=sys.stderr)
            else:
                results.append((path, len(blob)))
                if not listing_only:
                    dest = out_root / path
                    dest.parent.mkdir(parents=True, exist_ok=True)
                    dest.write_bytes(blob)
            if i % 20 == 0:
                print(f"  进度 {i}/{len(items)}")

    if failures:
        print(f"\n有 {len(failures)} 个文件所有通道都没拿到：", file=sys.stderr)
        for f in failures[:10]:
            print(f"  {f}", file=sys.stderr)
    return results


# ======================================================================
# 通道：整仓归档（慢，但一次请求拿全部）
# ======================================================================

def fetch_via_archive(out_root: pathlib.Path, force: bool,
                      listing_only: bool) -> list[tuple[str, int]]:
    print(f"通道 archive：下载整仓归档 {ARCHIVE_URL}")
    print("  ⚠️ 本机实测只有 46 KB/s —— 142 MB 约 50 分钟。非必要别用。")

    results: list[tuple[str, int]] = []
    req = urllib.request.Request(ARCHIVE_URL, headers={"User-Agent": USER_AGENT})
    with urllib.request.urlopen(req, timeout=180) as resp:
        with tarfile.open(fileobj=resp, mode="r|gz") as tar:
            for member in tar:
                if not member.isfile():
                    continue
                parts = member.name.split("/", 1)
                if len(parts) != 2:
                    continue
                rel = parts[1]

                if rel in EXTRA_DOCS:
                    blob = tar.extractfile(member).read()
                    results.append((rel, len(blob)))
                    if not listing_only:
                        (out_root / rel).write_bytes(blob)
                        print(f"  {len(blob) / 1024:8.1f} KB  {rel}")
                    continue

                if not wanted_path(rel):
                    continue

                dest = out_root / rel
                if not force and dest.exists() and dest.stat().st_size == member.size:
                    results.append((rel, member.size))
                    continue

                blob = tar.extractfile(member).read()
                results.append((rel, len(blob)))
                if listing_only:
                    continue
                dest.parent.mkdir(parents=True, exist_ok=True)
                dest.write_bytes(blob)
                if len(results) % 20 == 0:
                    print(f"  解出 {len(results)} 个……")

    return results


# ======================================================================

def main() -> int:
    ap = argparse.ArgumentParser(description="拉取 dsh-pet 源素材")
    ap.add_argument("--out", default=str(DEFAULT_OUT), help="输出目录（默认 .tmp-dsh-assets）")
    ap.add_argument("--list", action="store_true", help="只列清单，不下载")
    ap.add_argument("--force", action="store_true", help="已存在也重新下载")
    ap.add_argument("--via", choices=("auto", "mirror", "archive", "raw", "api"), default="auto",
                    help="通道（默认 auto = 镜像，失败落回 raw / archive）")
    ap.add_argument("--jobs", type=int, default=8, help="并发数（默认 8）")
    args = ap.parse_args()

    out_root = pathlib.Path(args.out).resolve()
    out_root.mkdir(parents=True, exist_ok=True)

    try:
        items = list_assets()
    except Exception as e:
        print(f"❌ 取不到素材清单：{type(e).__name__}: {e}", file=sys.stderr)
        return 1

    total = sum(s for _, s in items)
    print(f"清单：{len(items)} 个文件 / {total / 1048576:.1f} MB")

    if args.list:
        for path, size in sorted(items):
            print(f"  {size / 1024:8.1f} KB  {path}")
        return 0

    if args.via == "archive":
        results = fetch_via_archive(out_root, args.force, False)
    else:
        bases = [RAW_BASE] if args.via == "raw" else mirror_bases()
        if args.via == "auto":
            bases = bases + [RAW_BASE]
        print(f"通道 {args.via}：{len(bases)} 个源，{args.jobs} 并发")
        results = fetch_concurrent(items, out_root, args.force, False, bases, args.jobs)

        # 还缺的走归档兜底
        missing = [(p, s) for p, s in items
                   if not (out_root / p).exists() or (out_root / p).stat().st_size != s]
        if missing and args.via == "auto":
            print(f"\n还剩 {len(missing)} 个没拿到，改用 archive 通道兜底……")
            for p, s in missing:
                print(f"  {p}")

        # 说明文件（许可要求署名，必须跟着素材走）
        for doc in EXTRA_DOCS:
            dest = out_root / doc
            if dest.exists() and not args.force:
                continue
            blob, err = download_via_bases(bases, doc)
            if blob:
                dest.write_bytes(blob)
                print(f"  {len(blob) / 1024:8.1f} KB  {doc}")
            else:
                print(f"  （{doc} 取不到：{err}）")

    webm = [(out_root / p) for p, _ in items if p.lower().endswith(".webm") and (out_root / p).exists()]
    if not webm:
        print("❌ 一个 webm 都没拿到", file=sys.stderr)
        return 1

    got = sum(f.stat().st_size for f in webm)
    print(f"\n完成：{len(webm)}/{len([1 for p, _ in items if p.lower().endswith('.webm')])} 个 webm"
          f" / {got / 1048576:.1f} MB → {out_root}")

    by_dir: dict[str, int] = {}
    for f in webm:
        key = f.parent.relative_to(out_root / WANT_PREFIX).as_posix()
        by_dir[key] = by_dir.get(key, 0) + 1
    for d, c in sorted(by_dir.items()):
        print(f"  {c:3d}  {d}")

    return 0 if len(webm) == len([1 for p, _ in items if p.lower().endswith(".webm")]) else 1


if __name__ == "__main__":
    sys.exit(main())

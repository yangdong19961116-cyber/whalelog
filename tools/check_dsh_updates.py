#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""看看 GitHub 上的 dsh-pet 动画素材有没有更新。

## 为什么要单独一个脚本

`fetch_dsh_assets.py` 只负责"拉"，不负责"值不值得拉" —— 它没法回答
"52 MB 的素材，这次有没有必要重下一遍"。这个脚本就是那个判断题：
**只比清单，不下载一个字节**（除了几 KB 的 tree JSON）。

## 怎么判"有没有更新"

素材是**原样搬运、不重编码**的（见 convert_dsh_webm.py 的说明），
所以判据可以很硬 —— 两条都要对得上才算没变：

1. **文件名**：本地每个 `anim/<动作>/anim.json` 里记着 `"source": "待机呼吸休闲.webm"`，
   那是源仓库里的原始文件名。
2. **字节数**：源 tree 里每个 blob 有 size，本地 `video.webm` 有实际大小。
   原样搬运意味着两者必须**逐字节相等**。

只要有一条对不上，就是"变了"。数量多了是新增，少了是改名/删除，
同名不同字节是**同一个动作被重画了**（这种最容易漏 —— 数量对得上，看着一切正常）。

## 用法（仓库根目录）

    python tools/check_dsh_updates.py              # 人看的报告
    python tools/check_dsh_updates.py --quiet      # 只在"有更新"时输出（给脚本用）
    python tools/check_dsh_updates.py --json       # 机器可读
    python tools/check_dsh_updates.py --local <目录>   # 指定本地形象包目录

退出码：0 = 素材没变，1 = 有更新（或取不到清单）。

## ⚠️ 本机网络

`github.com` 与 `raw.githubusercontent.com` **不通**，只有 **`api.github.com` 通**。
所以这个脚本走 tree API（一次请求拿全量清单，额度够 —— 限流 60 次/小时，
认证用户更多；别把它塞进循环里跑）。详见 fetch_dsh_assets.py 顶部的通道表。

清单会缓存到 `.tmp-dsh-tree.json`，取不到时用上次的（会在输出里说明）。
"""

from __future__ import annotations

import argparse
import json
import os
import pathlib
import sys
import urllib.error
import urllib.request

REPO = "MerZlin/dsh-pet-indesktop"
BRANCH = "main"
API_TREE = f"https://api.github.com/repos/{REPO}/git/trees/{BRANCH}?recursive=1"

ROOT = pathlib.Path(__file__).resolve().parents[1]
DEFAULT_LOCAL = ROOT / "assets" / "characters" / "dsh"
TREE_CACHE = ROOT / ".tmp-dsh-tree.json"

WANT_PREFIX = "assets/characters/"
USER_AGENT = "whalelog-asset-update-check"


def fetch_tree(quiet: bool) -> tuple[list[dict], str]:
    """返回 (tree, 说明)。取不到就退回落盘的缓存，说明里会写明。"""
    try:
        req = urllib.request.Request(API_TREE, headers={"User-Agent": USER_AGENT})
        with urllib.request.urlopen(req, timeout=60) as resp:
            raw = resp.read()
        TREE_CACHE.write_bytes(raw)
        data = json.loads(raw.decode("utf-8"))
        note = "远端最新" if not data.get("truncated") else "远端最新（⚠️ 文件树被截断，清单可能不全）"
        return data.get("tree", []), note
    except Exception as e:
        if TREE_CACHE.exists():
            data = json.loads(TREE_CACHE.read_bytes().decode("utf-8"))
            return data.get("tree", []), f"⚠️ 取不到远端（{type(e).__name__}），用的是上次缓存的清单"
        raise


def local_assets(local_root: pathlib.Path) -> dict[str, dict]:
    """本地已转码的动作：source 文件名 → {动作、字节数、角色}。"""
    anim_root = local_root / "anim"
    out: dict[str, dict] = {}
    if not anim_root.is_dir():
        return out

    for d in sorted(anim_root.iterdir()):
        if not d.is_dir():
            continue
        # 跳过 `_` / `.` 开头的目录：手工备份（`idle-xxx.bak`、`_old`）不该被当成一个动作。
        # 不跳的话，一个改坏前的备份会被算成"源仓库里没有的动作"，报出一堆假的"消失"。
        if d.name.startswith(("_", ".")):
            continue
        meta = d / "anim.json"
        if not meta.is_file():
            continue
        try:
            j = json.loads(meta.read_text(encoding="utf-8"))
        except Exception:
            continue
        source = j.get("source")
        if not source:
            continue
        video = d / "video.webm"
        out[source] = {
            "action": d.name,
            "size": video.stat().st_size if video.is_file() else -1,
            "role": j.get("role", ""),
        }
    return out


def remote_assets(tree: list[dict]) -> dict[str, dict]:
    """源仓库里的素材：文件名 → {字节数、路径}。"""
    out: dict[str, dict] = {}
    for e in tree:
        path = e.get("path", "")
        if e.get("type") != "blob" or not path.lower().endswith(".webm"):
            continue
        if not path.startswith(WANT_PREFIX):
            continue
        out[os.path.basename(path)] = {"size": e.get("size", 0), "path": path}
    return out


def compare(local: dict[str, dict], remote: dict[str, dict]) -> dict:
    added = sorted(k for k in remote if k not in local)
    removed = sorted(k for k in local if k not in remote)
    changed = sorted(
        ((k, local[k]["size"], remote[k]["size"]) for k in remote
         if k in local and local[k]["size"] != remote[k]["size"]),
        key=lambda x: x[0],
    )
    same = [k for k in remote if k in local and local[k]["size"] == remote[k]["size"]]
    return {
        "localCount": len(local),
        "remoteCount": len(remote),
        "sameCount": len(same),
        "added": [{"name": k, "size": remote[k]["size"], "path": remote[k]["path"]} for k in added],
        "removed": removed,
        "changed": [{"name": n, "localSize": a, "remoteSize": b} for n, a, b in changed],
        "hasUpdate": bool(added or removed or changed),
    }


def report(result: dict, note: str) -> None:
    print(f"源仓库：{REPO}（{BRANCH}） · 清单来源：{note}")
    print(f"本地已转码 {result['localCount']} 个动作 / 源仓库 {result['remoteCount']} 个素材")
    print()

    if not result["hasUpdate"]:
        print(f"✅ 素材没有任何更新 —— {result['sameCount']}/{result['remoteCount']} 个动作同名同字节。")
        print("   不用重新拉取，也不用重跑转码。")
        return

    print("⚠️ 素材有更新：")
    if result["added"]:
        print(f"\n  新增 {len(result['added'])} 个（源仓库有了、本地还没转）：")
        for a in result["added"]:
            print(f"    + {a['size']:>9,} 字节  {a['name']}")
    if result["removed"]:
        print(f"\n  消失 {len(result['removed'])} 个（源仓库删了或改名了）：")
        for n in result["removed"]:
            print(f"    - {n}")
    if result["changed"]:
        print(f"\n  重画 {len(result['changed'])} 个（同名但字节数变了 —— 数量对得上，最容易漏）：")
        for c in result["changed"]:
            print(f"    ~ {c['name']}  本地 {c['localSize']:,} → 远端 {c['remoteSize']:,}"
                  f"  (差 {c['remoteSize'] - c['localSize']:+,})")

    print("\n下一步：python tools/fetch_dsh_assets.py  →  python tools/convert_dsh_webm.py --out assets/characters/dsh")
    print("（转完记得重跑自测：形象包换了一批素材，先前的断言可能跟着变。）")


def main() -> int:
    ap = argparse.ArgumentParser(description="检查 dsh-pet 动画素材有没有更新（只比清单，不下载）")
    ap.add_argument("--local", default=str(DEFAULT_LOCAL), help="本地形象包目录（默认 assets/characters/dsh）")
    ap.add_argument("--json", action="store_true", help="输出 JSON")
    ap.add_argument("--quiet", action="store_true", help="没更新就一个字都不说（给脚本用）")
    args = ap.parse_args()

    try:
        tree, note = fetch_tree(args.quiet)
    except Exception as e:
        print(f"❌ 取不到素材清单：{type(e).__name__}: {e}", file=sys.stderr)
        return 1

    local = local_assets(pathlib.Path(args.local).resolve())
    remote = remote_assets(tree)
    if not remote:
        print("❌ 清单里一个 webm 都没有，检查 WANT_PREFIX 或分支名", file=sys.stderr)
        return 1

    result = compare(local, remote)
    result["source"] = REPO
    result["note"] = note

    if args.json:
        print(json.dumps(result, ensure_ascii=False, indent=2))
    elif result["hasUpdate"] or not args.quiet:
        report(result, note)

    return 1 if result["hasUpdate"] else 0


if __name__ == "__main__":
    sys.exit(main())

#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
修复跨天再归档时「新版本粘在首次归档那天」的历史记录。

背景
----
2026-09-17 之前的版本里，同一个文件第二天改了内容再归档时，只把版本号顶上去、
没动归属日期（addedDate）。而全程序判断「某天有哪些归档文件」只看归属日期 ——
当日页面、只看当天、全部视图分栏、月历圆点、日报周报、notes\\*.md 全是它。
于是新版本出现在旧日期那一页，产生它的那天反而是空的。

现象：9 月 16 日归档的文件，9 月 17 日再归档 → 只在 9 月 16 日那页看到 v2，
      9 月 17 日那页什么都没有。

修法
----
按每条版本历史的记录时刻（recordedAt）把记录拆开，一天一条：
  最新那天保留原记录（它的版本号/指纹本来就是最新版的），更早的各新建一条，
  每条带上自己那天的版本号和指纹。
拆完 9 月 16 日那页显示 v1、9 月 17 日那页显示 v2。

用法
----
    python tools/repair_archive_dates.py                  # 默认只预览，绝不写文件
    python tools/repair_archive_dates.py --apply          # 实际写入（先自动备份）
    python tools/repair_archive_dates.py --data <数据目录>

数据目录默认取环境变量 WHALELOG_DATA_DIR，没设则用 %APPDATA%\\WhaleLog。

安全
----
* **默认只读**：不加 --apply 不会写任何文件。
* 程序正在运行时直接拒绝 —— 它内存里还压着旧数据，一存盘就把修复覆盖了。
* 写之前先把 index.json 复制成 index.json.bak-<时间戳>，并且只写这一份文件。
* 结果会重新读回来校验一遍，对不上就报错退出。
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import uuid
from datetime import datetime
from pathlib import Path


# ---------------------------------------------------------------------------


def find_running_app() -> str | None:
    """正在运行的鲸记进程。返回可读描述，没在跑就返回 None。"""
    try:
        out = subprocess.run(
            ["tasklist", "/FI", "IMAGENAME eq WhaleLog.exe", "/NH"],
            capture_output=True, text=True, timeout=15,
        ).stdout
    except Exception:
        return None                      # 查不了就当作没在跑，别把功能卡死
    for line in out.splitlines():
        if "whalelog.exe" in line.lower():
            return " ".join(line.split())
    return None


def resolve_data_dir(explicit: str | None) -> Path:
    if explicit:
        return Path(explicit)
    env = os.environ.get("WHALELOG_DATA_DIR")
    if env:
        return Path(env)
    appdata = os.environ.get("APPDATA")
    if not appdata:
        sys.exit("找不到 %APPDATA%，请用 --data 指定数据目录")
    return Path(appdata) / "WhaleLog"


def load_index(path: Path) -> dict:
    text = path.read_text(encoding="utf-8-sig")
    return json.loads(text)


def version_date(ver: dict, fallback: str | None) -> str | None:
    """这条版本历史属于哪天：优先用它自己的记录时刻。"""
    stamp = ver.get("recordedAt") or ver.get("RecordedAt")
    if isinstance(stamp, str) and len(stamp) >= 10:
        head = stamp[:10]
        if head[:4].isdigit() and head[4] == "-" and head[7] == "-":
            return head
    return fallback


def split_record(rec: dict) -> tuple[dict, list[dict]] | None:
    """
    把一条跨天的记录拆成 (保留的那条, [新增的更早的那几条])。
    不需要拆（只有一版、或全部版本都在同一天）返回 None。
    数据看着不对劲（版本号与日期不同向）也返回 None —— 这种情况宁可不动。
    """
    hist = rec.get("history") or []
    if len(hist) <= 1:
        return None

    dated = [(version_date(v, rec.get("addedDate")), v) for v in hist]
    if any(d is None for d, _ in dated):
        return None

    dates = sorted({d for d, _ in dated})
    if len(dates) == 1:
        return None

    # 越晚的分组，版本号应该越大。不是这样说明数据已经乱了，不猜。
    maxima = [max((v.get("version") or 0) for d, v in dated if d == day) for day in dates]
    if maxima != sorted(maxima) or len(set(maxima)) != len(maxima):
        return None

    groups = {
        day: sorted([v for d, v in dated if d == day], key=lambda v: v.get("version") or 0)
        for day in dates
    }

    keep_day = dates[-1]
    latest_of_keep = groups[keep_day][-1]
    if rec.get("addedDate") != keep_day:
        # 原记录的版本号/指纹代表的是最新那版，所以保留的必须是最后一天；
        # 而这正是要修的地方 —— 它以前停在首次归档那天
        print(f"    修正归属日期：{rec.get('addedDate')} → {keep_day}")

    new_records: list[dict] = []
    for day in dates[:-1]:
        latest = groups[day][-1]
        clone = dict(rec)
        clone["id"] = uuid.uuid4().hex
        clone["version"] = latest.get("version") or 1
        clone["sha256"] = latest.get("sha256") or ""
        clone["sizeBytes"] = latest.get("sizeBytes") or 0
        clone["lastWriteTime"] = latest.get("lastWriteTime")
        clone["snapshotPath"] = latest.get("snapshotPath")
        clone["addedDate"] = day
        clone["addedAt"] = latest.get("recordedAt")
        clone["history"] = groups[day]
        new_records.append(clone)

    kept = dict(rec)
    kept["addedDate"] = keep_day
    kept["addedAt"] = latest_of_keep.get("recordedAt")
    kept["history"] = groups[keep_day]
    return kept, new_records


def link_entries(book: dict, new_records: list[dict]) -> None:
    """
    新建的记录如果继承了 entryId，就把它也挂进那条笔记的 fileIds，
    免得出现"记录指着条目、条目却不认它"的单向箭头。
    """
    entries = {
        e.get("id"): e
        for day in book.get("days") or []
        for e in day.get("entries") or []
    }
    for rec in new_records:
        entry = entries.get(rec.get("entryId"))
        if entry is None:
            continue
        ids = entry.setdefault("fileIds", [])
        if rec["id"] not in ids:
            ids.append(rec["id"])


# ---------------------------------------------------------------------------


def describe(files: list[dict], limit: int = 12) -> None:
    for rec in files[:limit]:
        hist = rec.get("history") or []
        marks = "、".join(f"v{v.get('version')}@{version_date(v, None)}" for v in hist)
        print(f"    {rec.get('fileName')}  v{rec.get('version')}  归属 {rec.get('addedDate')}  历史 [{marks}]")
    if len(files) > limit:
        print(f"    …… 还有 {len(files) - limit} 条")


def main() -> int:
    ap = argparse.ArgumentParser(description="修复跨天再归档导致的归属日期错位")
    ap.add_argument("--data", help="数据目录（默认 WHALELOG_DATA_DIR 或 %%APPDATA%%\\WhaleLog）")
    ap.add_argument("--apply", action="store_true", help="真的写入；不加就只预览")
    ap.add_argument("--force", action="store_true", help="即使程序正在运行也继续（危险）")
    args = ap.parse_args()

    data_dir = resolve_data_dir(args.data)
    index = data_dir / "index.json"

    print(f"数据目录：{data_dir}")
    if not index.exists():
        print(f"找不到 {index}，没什么可修的。")
        return 0

    running = find_running_app()
    if running:
        print(f"鲸记正在运行（{running}）—— 预览没问题，但真写入前必须先退掉它。")
        print("  它内存里还压着旧数据，任一次存盘都会把这里的修复覆盖掉。")

    book = load_index(index)
    files = book.get("files") or []
    print(f"读入 {len(files)} 条归档记录")

    kept_all: list[dict] = []
    added_all: list[dict] = []
    skipped: list[dict] = []

    for rec in files:
        split = split_record(rec)
        if split is None:
            kept_all.append(rec)
            continue
        kept, added = split
        kept_all.append(kept)
        added_all.extend(added)

    if not added_all:
        print()
        print("没有需要修的记录 —— 所有版本历史都在同一天里。")
        return 0

    print()
    print(f"需要拆开的记录：{len(added_all)} 条新记录将从 {len(added_all) + 0} 条旧记录里分出来")
    print()
    print("  拆分前：")
    describe([r for r in files if any(
        (r.get("versionGroupKey"), r.get("fullPath")) == (a.get("versionGroupKey"), a.get("fullPath"))
        for a in added_all)])
    print()
    print("  拆分后（同一条版本链上的记录）：")
    targets = {(a.get("fullPath")) for a in added_all}
    describe([r for r in kept_all + added_all if r.get("fullPath") in targets])

    if not args.apply:
        print()
        print("以上只是预览，没有写任何文件。确认无误后加 --apply 执行。")
        return 0

    if running and not args.force:
        print()
        print("鲸记正在运行，停手了 —— 它会用内存里的旧数据把修复覆盖掉。")
        print("请先退出鲸记再跑；确认过风险的话加 --force。")
        return 2
    if running:
        print()
        print("⚠ --force 已放行，但鲸记还在跑，修复很可能被它覆盖。")

    # --- 写入 ---
    stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
    backup = index.with_name(f"index.json.bak-{stamp}")
    shutil.copy2(index, backup)
    print()
    print(f"已备份原文件 → {backup.name}")

    link_entries(book, added_all)
    book["files"] = kept_all + added_all

    tmp = index.with_name(index.name + ".tmp")
    tmp.write_text(
        json.dumps(book, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    os.replace(tmp, index)

    # --- 读回来校验 ---
    check = load_index(index)
    check_files = check.get("files") or []
    if len(check_files) != len(files) + len(added_all):
        print(f"校验失败：写回后应有 {len(files) + len(added_all)} 条，实际 {len(check_files)} 条")
        return 1
    for rec in check_files:
        if not rec.get("fullPath") or not rec.get("addedDate"):
            print(f"校验失败：记录缺少路径或归属日期 → {rec.get('fileName')}")
            return 1
    if len(check.get("days") or []) != len(book.get("days") or []):
        print("校验失败：每日记录数量对不上")
        return 1

    print(f"已写入 → {index}")
    print(f"  归档记录 {len(files)} → {len(check_files)} 条（拆出 {len(added_all)} 条）")
    print()
    print("下一步：启动鲸记，翻到那两天确认一下。")
    print("  notes\\*.md 是程序每次存盘时重新生成的，改动任意一条记录后它们会跟着更新。")
    return 0


if __name__ == "__main__":
    sys.exit(main())

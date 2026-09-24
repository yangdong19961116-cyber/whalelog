#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""形象素材的「透明区剖面」——全量解码，把内容并集包围盒和 alpha 占比量出来。

用法（仓库根目录）：

    # 全量跑一遍（106 个动作约 1~2 分钟），出报告 + 总览图
    python tools/check_alpha_profile.py

    # 只要报告不要图，或者换个形象
    python tools/check_alpha_profile.py --no-sheet
    python tools/check_alpha_profile.py --characters assets/characters/别的形象

    # 先拿几个动作试试
    python tools/check_alpha_profile.py --only 三球 写代码 --limit 5

## 它和 convert_dsh_webm.py --check 的分工

`--check` 只解**首帧**（`-frames:v 1`）就下结论。道具在第 150 帧才出现的那种动作，
首帧干干净净，被裁了也看不出来。它还会校一条「滤镜必须是源画布全幅」——
那条规则本身就说明**画布已经被占满**，但没有任何东西在验这件事。

这个脚本**逐帧解完整段**，所以能回答两个 `--check` 答不了的问题：

1. **内容并集到底多大？**（各个方向的剩余余量是多少 px）
   规格文档里写的 `x 4..630 y 10..359` 是个实测值，但没有工具在日常里复现它 ——
   换了源素材、改了编码参数之后这句话可能早就失效了，而没人知道。
2. **每條动作自己需要多大画布？** 谁把画布用满了、谁只占中间一小块。
   要收小画布时这份清单就是依据（`migrate_dsh_geometry.py --viewport`）。

## 关于「alpha=255 占比 50~65% 算正常」这条经验值

那是**网页桌宠**的判据 —— 它的画布是紧贴角色裁出来的方形，所以不透明区自然占一半以上。
dsh 这批素材的画布是 16:9 的整幅源画布，角色只占中间一条，占比本来就只有十几个百分点。

所以这里**不做绝对阈值判定**，改成两个相对判据：

- 各动作之间横向比：某个动作的占比显著低于全集中位数 → 可能漏画或整段被裁；
- 动作内部纵向比：某帧的内容面积远小于该动作自己的最大值 → 那一帧可能是空的。

绝对数字照样全打出来，供人对照 —— 判据靠比，数字靠看。

## 判据

硬问题（退出码 1）：
  · 一个动作一帧都没解出来，或整段全透明
  · 帧表 frameWidth/Height 与滤镜 crop 矩形的尺寸对不上
  · 滤镜是纯裁切时，crop 矩形**没有盖住**该动作的内容并集 —— 这就是被裁
  · 滤镜是恒等（源画布全幅）时，源尺寸与 index.json 声明的画布不一致

提醒：
  · 内容并集贴到裁切边界 1px 以内（余量为 0，下次重编码就会切到）
  · 某动作内容面积不到全集中位数的 1/4
  · 某动作里有近乎全透明的帧

依赖：Python 3.9+ / pillow / numpy / imageio-ffmpeg。
解码参数复用 `convert_dsh_webm.py` 的那一套 —— 本文件**不重复实现 ffmpeg 调用**，
免得「透明 webm 必须点名 libvpx-vp9」这种坑在两个地方各踩一遍。
"""

from __future__ import annotations

import argparse
import concurrent.futures
import io
import json
import math
import os
import pathlib
import re
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFont

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))

# 复用转码器的解码路径：ffmpeg 定位、探测、逐帧 RGBA 管道。
# 它带 `if __name__ == "__main__"` 保护，import 不会跑主流程。
import convert_dsh_webm as cdw  # noqa: E402

DEFAULT_CHARACTERS = ROOT / "assets" / "characters" / "dsh"
DEFAULT_OUT_DIR = ROOT / "outputs"

#: alpha 低于这个值算作透明（和 convert_dsh_webm.py 保持一致：
#: 8 而不是 0，避开 JPEG 残留噪点和缩放产生的极淡边缘）
ALPHA_BBOX_THRESHOLD = 8

#: alpha 直方图分档。为什么要单独把 ==1 拎出来：
#: VP9 的 alpha 是**独立有损**编码的一个平面，近零的 alpha 会被量化成 1。
#: 实测 dsh 的「透明背景」有 81.9% 的像素解出来是 alpha=1、只有 0.6% 是 0 ——
#: 所以「alpha>0 占比」这个指标在这批素材上恒等于 100%，毫无信息量。
#: 网页桌宠那条「过渡带约 6%」的判据也因此不能照搬：它假设掩码是干净的 0/255。
#: 真正该看的是 9..254 那一档（≈ 边缘羽化），以及 >8 那一档（= 内容）。
HIST_BUCKETS = (
    (0, 0, "全透明"),
    (1, 1, "底噪(=1)"),
    (2, 8, "近零(2-8)"),
    (9, 63, "淡过渡(9-63)"),
    (64, 254, "强过渡(64-254)"),
    (255, 255, "实心(255)"),
)

#: 总览图里取哪几个位置的帧（按估算总帧数的比例）
SAMPLE_POSITIONS = (0.10, 0.50, 0.90)

#: 内容面积低于全集中位数的这个比例 → 提醒
OUTLIER_RATIO = 0.25

#: 单帧内容面积低于该动作最大值的这个比例 → 提醒（可能是空帧）
EMPTY_FRAME_RATIO = 0.05

#: 总览图参数
TILE_W, TILE_H = 160, 90
SHEET_COLS = 4
SHEET_BG = (237, 237, 234)
SHEET_LABEL = (40, 40, 38)
SHEET_BOX = (216, 90, 48)          # 并集包围盒框的颜色
SHEET_GRID = (200, 200, 196)

_CROP_RE = re.compile(r"^\s*crop=(\d+):(\d+):(\d+):(\d+)\s*$")


# ======================================================================
# 滤镜解析
# ======================================================================

def parse_pure_crop(flt: str) -> tuple[int, int, int, int] | None:
    """滤镜是「单个 crop」时返回 (w, h, x, y)，否则 None。

    只认纯裁切。带 scale / pad 的滤镜是几何变换，内容坐标和画布坐标不再一一对应，
    包含性判断就不适用了 —— 那时返回 None，由调用方降级成「只核尺寸」。
    """
    segs = [s for s in flt.split(",") if s.strip()]
    if len(segs) != 1:
        return None
    m = _CROP_RE.match(segs[0])
    if not m:
        return None
    return tuple(int(v) for v in m.groups())  # type: ignore[return-value]


# ======================================================================
# 单个动作的剖面
# ======================================================================

def profile_one(item: dict) -> dict:
    """逐帧解完整段，返回这个动作的透明区剖面。

    返回值要能跨进程传（ProcessPoolExecutor），所以只放基本类型 + 缩略图字节。
    """
    video = pathlib.Path(item["video"])
    info = cdw.probe_source(video)
    sw, sh = info["width"], info["height"]

    est = info.get("frames") or 0
    want: dict[int, int] = {}
    for slot, pos in enumerate(SAMPLE_POSITIONS):
        if est > 0:
            want[max(0, min(est - 1, int(round(pos * est))))] = slot

    thumbs: dict[int, bytes] = {}
    minx = miny = 1 << 30
    maxx = maxy = -1

    count = 0
    areas: list[int] = []
    hist = np.zeros(256, dtype=np.int64)
    first_rgba: np.ndarray | None = None
    last_rgba: np.ndarray | None = None

    for fr in cdw.iter_frames(video, sw, sh, 0):
        if count == 0:
            first_rgba = fr
        a = fr[:, :, 3]
        mask = a > ALPHA_BBOX_THRESHOLD
        area = int(mask.sum())
        areas.append(area)

        hist += np.bincount(a.ravel(), minlength=256)

        if area:
            ys, xs = np.nonzero(mask)
            minx, maxx = min(minx, int(xs.min())), max(maxx, int(xs.max()))
            miny, maxy = min(miny, int(ys.min())), max(maxy, int(ys.max()))

        slot = want.get(count)
        if slot is not None:
            thumbs[slot] = _thumb_bytes(fr)
        last_rgba = fr
        count += 1

    if count == 0:
        return {"action": item["action"], "role": item["role"], "video": str(video),
                "sourceWidth": sw, "sourceHeight": sh, "fps": info["fps"],
                "count": 0, "empty": True, "thumbs": {}, "bbox": None}

    # 估算帧数不准时补上没采到的槽位：首/末两端各留一份原始帧兜底
    if 0 not in thumbs and first_rgba is not None:
        thumbs[0] = _thumb_bytes(first_rgba)
    if 2 not in thumbs and last_rgba is not None:
        thumbs[2] = _thumb_bytes(last_rgba)
    if 1 not in thumbs:
        filler = first_rgba if first_rgba is not None else last_rgba
        if filler is not None:
            thumbs[1] = _thumb_bytes(filler)

    bbox = None if maxx < 0 else (minx, miny, maxx, maxy)
    nz = [v for v in areas if v > 0]
    tot = sw * sh * count

    def share(lo: int, hi: int) -> float:
        return float(hist[lo:hi + 1].sum()) / tot if tot else 0.0

    return {
        "action": item["action"],
        "role": item["role"],
        "video": str(video),
        "sourceWidth": sw,
        "sourceHeight": sh,
        "fps": info["fps"],
        "count": count,
        "empty": bbox is None,
        "bbox": bbox,
        # 逐帧极值：bbox 是"整段并集"，这里再看单帧的波动
        "areaMin": int(min(nz)) if nz else 0,
        "areaMax": int(max(nz)) if nz else 0,
        "areaMean": int(sum(areas) / count),
        "emptyFrames": int(sum(1 for v in areas if v == 0)),
        # alpha 直方图分档（占比，按全体像素 × 全部帧）
        "buckets": [[lo, hi, label, int(hist[lo:hi + 1].sum()), share(lo, hi)]
                    for lo, hi, label in HIST_BUCKETS],
        "floorRatio": share(1, 1),                 # VP9 alpha 量化底噪
        "contentRatio": share(ALPHA_BBOX_THRESHOLD + 1, 255),   # = alpha>8，真内容
        "solidRatio": share(255, 255),             # 实心不透明
        "bandRatio": share(9, 254),                # 边缘过渡带（有意义的那一档）
        "transparentRatio": share(0, 0),
        "thumbs": thumbs,
    }


def _thumb_bytes(frame: np.ndarray) -> bytes:
    im = Image.fromarray(frame, "RGBA").resize((TILE_W, TILE_H), Image.LANCZOS)
    buf = io.BytesIO()
    im.save(buf, format="PNG", optimize=True)
    return buf.getvalue()


# ======================================================================
# 采集与判定
# ======================================================================

def collect(anim_root: pathlib.Path, only: list[str] | None) -> list[dict]:
    out = []
    for d in sorted(p for p in anim_root.iterdir() if p.is_dir()):
        mf = next((d / n for n in ("anim.json", "sheet.json") if (d / n).is_file()), None)
        if mf is None:
            continue
        man = json.loads(mf.read_text(encoding="utf-8"))
        if man.get("type") != "video":
            continue
        if only and not any(o.lower() in man.get("name", d.name).lower() for o in only):
            continue
        out.append({
            "action": man.get("name", d.name),
            "role": man.get("role", man.get("name", d.name).split("-", 1)[0]),
            "video": str(d / man.get("video", "video.webm")),
            "manifest": man,
            "dir": str(d),
        })
    return out


class Report:
    def __init__(self) -> None:
        self.failures: list[str] = []
        self.warnings: list[str] = []
        self.infos: list[str] = []

    def fail(self, m: str) -> None:
        self.failures.append(m)

    def warn(self, m: str) -> None:
        self.warnings.append(m)

    def info(self, m: str) -> None:
        self.infos.append(m)


def judge(items: list[dict], rep: Report, index_path: pathlib.Path) -> dict:
    """把每个动作的剖面和它的帧表/声明画布对照，汇出全局并集。"""
    union = None
    for it in items:
        b = it["bbox"]
        if b is None:
            continue
        union = b if union is None else (min(union[0], b[0]), min(union[1], b[1]),
                                         max(union[2], b[2]), max(union[3], b[3]))

    # ---------------- 逐动作 ----------------
    edge_touch: dict[str, list[str]] = {}
    for it in items:
        name = it["action"]
        man = it["manifest"]

        if it.get("count", 0) == 0:
            rep.fail(f"{name}：一帧都没解出来")
            continue
        if it.get("empty"):
            rep.fail(f"{name}：整段全透明（{it['count']} 帧都没有内容）")
            continue

        fw, fh = int(man.get("frameWidth", 0)), int(man.get("frameHeight", 0))
        flt = man.get("filter", "")
        crop = parse_pure_crop(flt)

        if crop is None:
            # 非纯裁切（带 scale/pad）：内容坐标和画布坐标不再一一对应，
            # 只核一次解码尺寸，包含性交给人工
            rep.warn(f"{name}：滤镜不是纯裁切（{flt}）——"
                     f"内容并集的包含性本工具判不了，请人工核对")
            continue

        cw, ch, cx, cy = crop
        if (cw, ch) != (fw, fh):
            rep.fail(f"{name}：帧表画布 {fw}×{fh} 与滤镜裁切矩形 {cw}×{ch} 不一致")

        if (cx, cy) != (0, 0) or (cw, ch) != (it["sourceWidth"], it["sourceHeight"]):
            # 滤镜在裁：内容并集必须落在裁剪矩形内，留 1px 余量
            x0, y0, x1, y1 = it["bbox"]
            if x0 < cx or y0 < cy or x1 > cx + cw - 1 or y1 > cy + ch - 1:
                rep.fail(
                    f"{name}：内容并集 x{x0}..{x1} y{y0}..{y1} 超出裁切矩形 "
                    f"x{cx}..{cx + cw - 1} y{cy}..{cy + ch - 1} —— 有内容被裁")
            else:
                dx = min(x0 - cx, cx + cw - 1 - x1)
                dy = min(y0 - cy, cy + ch - 1 - y1)
                if min(dx, dy) <= 1:
                    rep.warn(f"{name}：内容距离裁切边界只剩 {min(dx, dy)}px，"
                             f"重编码/改参数就会切到")
        else:
            # 恒等：画布 = 源画布，那就核对源尺寸和声明画布
            sw, sh = it["sourceWidth"], it["sourceHeight"]
            if (sw, sh) != (fw, fh):
                rep.fail(f"{name}：源 {sw}×{sh} 与帧表画布 {fw}×{fh} 不一致")
            x0, y0, x1, y1 = it["bbox"]
            touch = []
            if x0 <= 0:
                touch.append("左")
            if y0 <= 0:
                touch.append("上")
            if x1 >= sw - 1:
                touch.append("右")
            if y1 >= sh - 1:
                touch.append("下")
            if touch:
                for side in touch:
                    edge_touch.setdefault(side, []).append(name)

    # ---------------- 全局并集 vs 声明画布 ----------------
    geom = None
    if index_path.is_file():
        geom = json.loads(index_path.read_text(encoding="utf-8")).get("geometry")

    if union and geom:
        declared = (int(geom.get("canvasWidth", 0)), int(geom.get("canvasHeight", 0)))
        x0, y0, x1, y1 = union
        rep.info(f"全 {len(items)} 个动作的内容并集：x {x0}..{x1}  y {y0}..{y1}"
                 f"（宽 {x1 - x0 + 1} × 高 {y1 - y0 + 1}）")
        rep.info(f"声明画布：{declared[0]}×{declared[1]}")
        rep.info(f"四边剩余余量：左 {x0}  右 {declared[0] - 1 - x1}  "
                 f"上 {y0}  下 {declared[1] - 1 - y1}")
        if x1 > declared[0] - 1 or y1 > declared[1] - 1:
            rep.fail(f"内容并集超出声明画布 {declared[0]}×{declared[1]}")
        elif min(x0, y0, declared[0] - 1 - x1, declared[1] - 1 - y1) <= 0:
            rep.warn("内容并集已经贴满声明画布的一条边 —— "
                     "这就是「滤镜必须全幅」那条硬检查存在的原因")

        # 逐帧全扫的并集 vs index.json 里的滤镜，两者必须互相印证
        flt = geom.get("floatFilter", "")
        crop = parse_pure_crop(flt)
        if crop:
            cw, ch, cx, cy = crop
            if cw != declared[0] or ch != declared[1]:
                rep.fail(f"index.json 的 floatFilter 裁切 {cw}×{ch} 与 "
                         f"geometry 声明的 {declared[0]}×{declared[1]} 不一致")
            if x0 < cx or y0 < cy or x1 > cx + cw - 1 or y1 > cy + ch - 1:
                rep.fail("内容并集超出 index.json 的 floatFilter 裁切矩形 —— "
                         "悬浮窗按它解码，会看到被裁的画面")

        # 并集占声明画布的比例：太小说明画布白留了一大片
        fill = ((x1 - x0 + 1) * (y1 - y0 + 1)) / (declared[0] * declared[1])
        rep.info(f"并集内接矩形占声明画布 {fill * 100:.1f}%")

    # ---------------- 顶边：合并成一条，别刷屏 ----------------
    # 单条动作顶边是**正常**的（海报级构图本来就会顶到画布边），
    # 全量跑下来会有十几条，分散打印反而看不出重点。合并成一条按方向的清单，
    # 要收小画布时直接照着这份名单核对。
    if edge_touch:
        parts = [f"{side} {len(names)} 个（{'、'.join(names[:4])}"
                 + ("…" if len(names) > 4 else "") + "）"
                 for side, names in sorted(edge_touch.items())]
        rep.warn("有动作的内容顶到了源画布边缘 —— " + "；".join(parts)
                 + "　画布在这几个方向已无余量，收小必然切到东西")

    # ---------------- 全局 alpha 分布 ----------------
    live_all = [it for it in items if it.get("count")]
    if live_all:
        grand = sum(sum(b[3] for b in it["buckets"]) for it in live_all)
        if grand:
            floor = sum(it["buckets"][1][3] for it in live_all) / grand
            content = sum(it["contentRatio"] * (it["sourceWidth"] * it["sourceHeight"]
                                                * it["count"]) for it in live_all) / grand
            if floor > 0.5:
                rep.info(
                    f"alpha=1 的像素占 {floor * 100:.1f}% —— 这是 VP9 alpha 平面的量化底噪"
                    f"（透明区被压成 1 而不是 0），不是内容。所以「alpha>0 占比」"
                    f"在这批素材上恒等于 100%，判内容要用 alpha>8 那一档（实测 {content * 100:.1f}%）")
                rep.info("底噪最高只有 1/255 = 0.4% 不透明度，合成到桌面上看不出来，"
                         "不必处理 —— 但任何按 alpha>0 算的判据都要改掉")
            elif floor:
                rep.info(f"alpha=1 的像素占 {floor * 100:.2f}%（VP9 量化底噪）")

    # ---------------- 动作之间的相对比较 ----------------
    live = [it for it in items if not it.get("empty") and it.get("count")]
    if live:
        med = float(np.median([it["areaMean"] for it in live]))
        rep.info(f"各动作平均内容面积的中位数：{med:,.0f} px")
        if med > 0:
            for it in sorted(live, key=lambda i: i["areaMean"])[:5]:
                if it["areaMean"] < med * OUTLIER_RATIO:
                    rep.warn(f"{it['action']}：平均内容面积 {it['areaMean']:,} px，"
                             f"只有中位数的 {it['areaMean'] / med * 100:.0f}% —— "
                             f"可能漏画或整段被裁")
        for it in live:
            if it["areaMin"] and it["areaMax"] and \
                    it["areaMin"] < it["areaMax"] * EMPTY_FRAME_RATIO:
                rep.warn(f"{it['action']}：最小帧内容面积 {it['areaMin']:,} px，"
                         f"只有本动作最大值的 {it['areaMin'] / it['areaMax'] * 100:.0f}% "
                         f"—— 疑似空帧")
            if it["emptyFrames"]:
                rep.warn(f"{it['action']}：有 {it['emptyFrames']}/{it['count']} 帧完全透明")

    return {"union": union, "geometry": geom}


# ======================================================================
# 报告
# ======================================================================

def print_table(items: list[dict]) -> None:
    header = (f"{'动作':28s} {'帧':>4s} {'秒':>5s} "
              f"{'实心255':>8s} {'内容>8':>8s} {'过渡9-254':>10s} {'底噪=1':>8s} "
              f"{'内容并集 x0..x1 y0..y1':>24s} {'面积中位':>9s}")
    print(header)
    print("-" * 124)

    rows = sorted(items, key=lambda it: -(it["areaMean"] if it.get("count") else 0))
    for it in rows:
        if it.get("empty"):
            print(f"{it['action'][:28]:28s} {it.get('count', 0):4d}   （整段全透明）")
            continue
        b = it["bbox"]
        dur = it["count"] / it["fps"] if it["fps"] else 0
        print(f"{it['action'][:28]:28s} {it['count']:4d} {dur:5.1f} "
              f"{it['solidRatio'] * 100:7.2f}% {it['contentRatio'] * 100:7.2f}% "
              f"{it['bandRatio'] * 100:9.2f}% {it['floorRatio'] * 100:7.1f}% "
              f"{b[0]:5d}..{b[2]:5d} {b[1]:5d}..{b[3]:5d} "
              f"{it['areaMean']:9,d}")


def print_histogram(items: list[dict]) -> None:
    """全体像素合起来的 alpha 分档 —— 底噪那一档必须一眼看得见。"""
    live = [it for it in items if it.get("count")]
    if not live:
        return
    total = sum(sum(b[3] for b in it["buckets"]) for it in live)
    if not total:
        return

    print()
    print("全体像素的 alpha 分档（%d 个动作 / %d 帧合计）："
          % (len(live), sum(it["count"] for it in live)))
    for i, (lo, hi, label) in enumerate(HIST_BUCKETS):
        n = sum(it["buckets"][i][3] for it in live)
        pct = 100.0 * n / total
        bar = "█" * int(round(pct / 2))
        print(f"  alpha {lo:3d}..{hi:3d}  {label:9s}{pct:7.2f}%  {bar}")


# ======================================================================
# 总览图
# ======================================================================

def load_font(size: int) -> ImageFont.FreeTypeFont | ImageFont.ImageFont:
    for name in ("msyh.ttc", "msyhl.ttc", "simhei.ttf", "simsun.ttc"):
        p = pathlib.Path("C:/Windows/Fonts") / name
        if p.is_file():
            try:
                return ImageFont.truetype(str(p), size)
            except Exception:
                continue
    return ImageFont.load_default()


def make_sheet(items: list[dict], union, declared, out_path: pathlib.Path) -> tuple[int, int]:
    """首/中/末三帧拼一张总览，每格画出该动作的内容并集包围盒。"""
    live = [it for it in items if it.get("thumbs")]
    if not live:
        raise RuntimeError("没有任何缩略图可以拼")

    font = load_font(12)
    group_w = TILE_W * 3 + 4
    label_h = 16
    group_h = label_h + TILE_H
    gap_x, gap_y = 20, 18
    margin = 20
    header_h = 34

    cols = SHEET_COLS
    rows = math.ceil(len(live) / cols)
    W = margin * 2 + cols * group_w + (cols - 1) * gap_x
    H = header_h + margin * 2 + rows * group_h + (rows - 1) * gap_y

    im = Image.new("RGB", (W, H), SHEET_BG)
    dr = ImageDraw.Draw(im)

    title = (f"形象素材透明区剖面 · {len(live)} 个动作 · 每格左→右为 10% / 50% / 90% 处的帧"
             f"　红框 = 该动作全段内容并集")
    dr.text((margin, 12), title, font=font, fill=SHEET_LABEL)

    if union and declared:
        x0, y0, x1, y1 = union
        sub = (f"全动作并集 x {x0}..{x1}  y {y0}..{y1}　"
               f"声明画布 {declared[0]}×{declared[1]}　"
               f"余量 左{x0} 右{declared[0] - 1 - x1} 上{y0} 下{declared[1] - 1 - y1}")
        dr.text((margin, header_h - 6), sub, font=font, fill=(110, 110, 106))

    # 每个动作：按顺序取到的那几个采样槽
    for i, it in enumerate(live):
        r, c = divmod(i, cols)
        gx = margin + c * (group_w + gap_x)
        gy = header_h + margin + r * (group_h + gap_y)

        b = it["bbox"]
        sw = it["sourceWidth"]
        sh = it["sourceHeight"]
        ratio = TILE_W / sw

        label = f"{it['action']}　内容{it['contentRatio'] * 100:.0f}%"
        dr.text((gx, gy), _clip(label, 34), font=font, fill=SHEET_LABEL)

        for slot in range(3):
            data = it["thumbs"].get(slot)
            tx = gx + slot * (TILE_W + 2)
            ty = gy + label_h
            if data is None:
                dr.rectangle([tx, ty, tx + TILE_W - 1, ty + TILE_H - 1], fill=(224, 224, 220))
                continue
            tile = Image.open(io.BytesIO(data)).convert("RGBA")
            im.paste(tile, (tx, ty), tile)
            dr.rectangle([tx, ty, tx + TILE_W - 1, ty + TILE_H - 1],
                         outline=SHEET_GRID, width=1)
            if slot == 0 and b is not None:
                bx0 = tx + int(round(b[0] * ratio))
                by0 = ty + int(round(b[1] * ratio))
                bx1 = tx + int(round((b[2] + 1) * ratio)) - 1
                by1 = ty + int(round((b[3] + 1) * ratio)) - 1
                dr.rectangle([bx0, by0, bx1, by1], outline=SHEET_BOX, width=1)

    out_path.parent.mkdir(parents=True, exist_ok=True)
    im.save(out_path, optimize=True)
    return im.size


def _clip(text: str, max_chars: int) -> str:
    return text if len(text) <= max_chars else text[:max_chars - 1] + "…"


# ======================================================================

def main() -> int:
    ap = argparse.ArgumentParser(
        description="形象素材的透明区剖面（全量解码）",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__)
    ap.add_argument("--characters", default=str(DEFAULT_CHARACTERS),
                    help="形象包目录（含 anim/ 与 index.json）")
    ap.add_argument("--only", nargs="*", help="只测名字含这些关键词的动作")
    ap.add_argument("--limit", type=int, help="最多测几个动作（调试用）")
    ap.add_argument("--jobs", type=int, default=max(1, min(8, (os.cpu_count() or 4)) - 1),
                    help="并发数")
    ap.add_argument("--no-sheet", action="store_true", help="不出总览图")
    ap.add_argument("--sheet", default=str(DEFAULT_OUT_DIR / "素材总览_首中末.png"),
                    help="总览图落点")
    ap.add_argument("--json", default=str(DEFAULT_OUT_DIR / "素材alpha剖面.json"),
                    help="剖面数据落点（给机器看的）")
    args = ap.parse_args()

    root = pathlib.Path(args.characters).resolve()
    anim_root = root / "anim"
    if not anim_root.is_dir():
        print(f"❌ 没有 anim 目录：{anim_root}", file=sys.stderr)
        return 2

    items = collect(anim_root, args.only)
    if args.limit:
        items = items[:args.limit]
    if not items:
        print("❌ 没有匹配到任何视频型动作", file=sys.stderr)
        return 2

    print(f"形象包：{root}")
    print(f"共 {len(items)} 个动作（逐帧全解，约 {len(items) * 0.7:.0f}s）\n")

    profiles: list[dict] = []
    if args.jobs <= 1:
        for it in items:
            p = profile_one(it)
            p["manifest"] = it["manifest"]
            profiles.append(p)
    else:
        with concurrent.futures.ProcessPoolExecutor(max_workers=args.jobs) as pool:
            futs = {pool.submit(profile_one, it): it for it in items}
            done_n = 0
            for fut in concurrent.futures.as_completed(futs):
                it = futs[fut]
                try:
                    p = fut.result()
                except Exception as e:
                    print(f"  ❌ {it['action']}：{e}", file=sys.stderr)
                    continue
                p["manifest"] = it["manifest"]
                profiles.append(p)
                done_n += 1
                if done_n % 20 == 0:
                    print(f"  {done_n}/{len(items)} ……")

    profiles.sort(key=lambda p: p["action"])

    rep = Report()
    summary = judge(profiles, rep, root / "index.json")

    print()
    print_table(profiles)
    print_histogram(profiles)

    print()
    print("=" * 118)
    for m in rep.infos:
        print(f"  · {m}")
    for m in rep.warnings:
        print(f"  ! {m}")
    for m in rep.failures:
        print(f"  ✗ {m}")
    print("=" * 118)

    declared = None
    if summary.get("geometry"):
        g = summary["geometry"]
        declared = (int(g.get("canvasWidth", 0)), int(g.get("canvasHeight", 0)))

    payload = {
        "characters": str(root),
        "actionCount": len(profiles),
        "sourceUnion": summary["union"],
        "declaredCanvas": declared,
        "actions": [
            {k: v for k, v in p.items() if k not in ("thumbs", "manifest")}
            for p in profiles
        ],
    }
    jp = pathlib.Path(args.json)
    jp.parent.mkdir(parents=True, exist_ok=True)
    jp.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"\n剖面数据：{jp}")

    if not args.no_sheet:
        try:
            size = make_sheet(profiles, summary["union"], declared, pathlib.Path(args.sheet))
            print(f"总览图：{args.sheet}  {size[0]}×{size[1]}")
        except Exception as e:
            print(f"  ❌ 总览图没出成：{e}", file=sys.stderr)

    if rep.failures:
        print(f"\n结论：{len(rep.failures)} 项必须修，{len(rep.warnings)} 项提醒")
        return 1
    print(f"\n结论：通过（{len(rep.warnings)} 项提醒）" if rep.warnings
          else "\n结论：通过，无提醒")
    return 0


if __name__ == "__main__":
    sys.exit(main())

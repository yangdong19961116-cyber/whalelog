#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""把 dsh-pet 的透明 webm 转成鲸记能吃的精灵图 PNG + 帧表。

源素材：640×360 / 24fps / VP9-alpha 透明 webm（角色只占中间 216×270）
目标：720×720 那种"墙"式画布在鲸记这里没必要 —— 见下面「为什么不是 720×720」。

用法（仓库根目录）：

    # 1) 先看源素材到底长什么样（不写任何文件）
    python tools/convert_dsh_webm.py --probe

    # 2) 体积对照实验（挑几个代表性动作，出多套参数对比表）
    python tools/convert_dsh_webm.py --bench

    # 3) 批量转码
    python tools/convert_dsh_webm.py --out build/pet-assets/shenshen
    python tools/convert_dsh_webm.py --out <目标>/Assets/characters/shenshen

    # 4) 抽查产物是否符合规格（锚点是否漂、有没有裁边）
    python tools/convert_dsh_webm.py --check build/pet-assets/shenshen

## 为什么不是 720×720

《动态素材规格.md》规定画布 720×720、角色高 600。那是给**手画的**素材定的，
目的是"角色在屏幕上的大小不因为换素材而变"。但对这批源素材，照做有两个问题：

1. **纯浪费**。源素材里角色只有 270px 高，放大到 600 是 2.22 倍插值放大；
   而悬浮窗实际只把角色显示成 **165px 高** —— 放大之后再缩回去，
   凭空多出 4.6 倍的像素和体积，画质还更糊。
2. **画布太大反而显示不了**。悬浮窗宽 236（设计像素），720 的画布塞不进去。

所以这里用**原生分辨率**：角色高就是源素材的 270px，画布 384×384。

帧表里的 `characterHeight` 字段就是为这件事存在的 ——
`AnimSheet.DisplayBoxHeight()` 按 `165 × 帧高 ÷ 角色高` 反算显示框，
只要这个比值不变，**角色在屏幕上的大小和 720 方案完全一样**，
但像素少 4.6 倍、且全程零插值。

    384 ÷ 270 = 1.4222 = 720 ÷ 506…（同一比值，屏幕尺寸一致）

## 几何：整段用一个固定变换，不逐帧对齐

账号锚点取自源素材的 `manifest.json`：`body_box = [212, 60, 428, 330]`
→ 横向中心 x=320（正好画布中心）、脚底 y=330、角色高 270。

**整段动画共用这一个变换**（缩放 + 平移），逐帧只换像素：

    屏幕点 = (锚点x + (源x − 320) × s,  锚点y + (源y − 330) × s)

不能逐帧按 alpha 包围盒对齐 —— 那样跳跃、伸展会被压平，动感全没了。
这是《动态素材规格.md》里特别强调过的一条。
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
import shutil
import subprocess
import sys
import time

import numpy as np
from PIL import Image

ROOT = pathlib.Path(__file__).resolve().parents[1]
DEFAULT_SRC = ROOT / ".tmp-dsh-assets" / "assets" / "characters" / "shenshen" / "videos"
# 默认输出直接落在仓库里的「出厂素材」位置。
# 理由：这个目录是**要提交、要进包**的东西（打便携包时整个 characters\ 会被拷进
# Assets\），不是一次性的中间产物 —— 放在 build\ 里会让人以为可以随手删掉。
DEFAULT_OUT = ROOT / "assets" / "characters" / "dsh"

# ----------------------------------------------------------------------
# 几何默认值
# ----------------------------------------------------------------------

#: 源素材的锚点（来自 manifest.json，写死一份作为缺省）
DEFAULT_BODY_BOX = (212, 60, 428, 330)

#: 画布边长（正方形）。384 = 角色高 270 × 1.4222，与 720/600 同比例。
DEFAULT_CELL = 384

#: 角色在画布里的高度。0 表示 = 源素材里 body_box 的高度（即 1:1 不缩放）。
DEFAULT_CHAR_H = 0

#: 输出帧率。0 表示跟随源素材（24fps）。
DEFAULT_FPS = 0

#: 源画布顶部在格子里留多少透明像素（实测有动作会顶到源画布最上沿）。
DEFAULT_TOP_PAD = 6

#: 源文件名里的前缀，转成目录名时要去掉（目录名要能一眼看出是什么动作）
STRIP_PREFIXES = ("点击回应 - ", "点击回应-", "点击回应 ", "碎碎念-")

#: 角色名 → 角色目录名前缀。前缀同时决定了它在程序里扮演的角色：
#: idle=持续循环的待机、clickBounce=点击回应（多个随机挑）、drag=拖拽中循环、
#: alert=提醒、ambient=随机小动作、move=贴边移动、turn=转向
ROLE_OF_FOLDER = {
    "idle": "idle",
    "turn": "turn",
    "move": "move",
    "click": "clickBounce",
    "drag": "drag",
    "random": "ambient",
    "events": "ambient",       # 余额事件那 6 个：鲸记没有余额概念，当普通小动作
    "events/balance": "ambient",
}

#: 需要跳过的（源仓库里跟桌宠动画无关的）
SKIP_NAMES = ()


# ======================================================================
# ffmpeg
# ======================================================================

_FFMPEG: str | None = None


def ffmpeg_exe() -> str:
    global _FFMPEG
    if _FFMPEG:
        return _FFMPEG
    try:
        import imageio_ffmpeg
        _FFMPEG = imageio_ffmpeg.get_ffmpeg_exe()
    except Exception as e:
        raise SystemExit(
            f"找不到 ffmpeg：{e}\n"
            "  本机没有系统级 ffmpeg，用 venv 里的 imageio-ffmpeg：\n"
            "    <venv>/Scripts/pip install imageio-ffmpeg\n"
            "  ⚠️ 不要改用 PyAV —— 它内置的解码器会静默丢掉 VP9 的 alpha（解出来 alpha 全是 255）。")
    return _FFMPEG


# ======================================================================
# 源素材探测与解码
# ======================================================================

_INFO_RE = re.compile(r"Video: (\S+).*?, (\d+)x(\d+)")
_FPS_RE = re.compile(r"([\d.]+) fps")
_DUR_RE = re.compile(r"Duration: (\d+):(\d+):([\d.]+)")


def probe_source(path: pathlib.Path) -> dict:
    """读源素材的分辨率与帧率（只解头部，很快）。"""
    p = subprocess.run(
        [ffmpeg_exe(), "-hide_banner", "-c:v", "libvpx-vp9", "-i", str(path)],
        capture_output=True, text=True, errors="replace", timeout=60)
    text = p.stderr
    m, f = _INFO_RE.search(text), _FPS_RE.search(text)
    if not m:
        raise RuntimeError(f"读不出视频信息：{path}")
    d = _DUR_RE.search(text)
    duration = 0.0
    if d:
        duration = int(d.group(1)) * 3600 + int(d.group(2)) * 60 + float(d.group(3))
    fps = float(f.group(1)) if f else 24.0
    return {
        "codec": m.group(1),
        "width": int(m.group(2)),
        "height": int(m.group(3)),
        "fps": fps,
        "duration": duration,
        # 容器里的时长 × 帧率 ≈ 帧数。**不逐帧解码去数** ——
        # 106 个素材全解一遍要一分多钟，而运行时那边本来就会按实际解出的帧数兜底。
        "frames": max(1, int(round(duration * fps))) if duration else 0,
    }


def iter_frames(path: pathlib.Path, width: int, height: int, out_fps: float = 0):
    """逐帧解出 RGBA 像素。走 rawvideo 管道 —— 不落任何中间文件。"""
    cmd = [ffmpeg_exe(), "-v", "error", "-c:v", "libvpx-vp9", "-i", str(path)]
    if out_fps and out_fps > 0:
        cmd += ["-vf", f"fps={out_fps}"]
    cmd += ["-f", "rawvideo", "-pix_fmt", "rgba", "-"]

    proc = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    frame_bytes = width * height * 4
    try:
        while True:
            buf = proc.stdout.read(frame_bytes)
            if not buf or len(buf) < frame_bytes:
                break
            yield np.frombuffer(buf, dtype=np.uint8).reshape(height, width, 4)
    finally:
        proc.stdout.close()
        err = proc.stderr.read().decode("utf-8", "replace")
        proc.stderr.close()
        code = proc.wait()
        if code != 0:
            raise RuntimeError(f"ffmpeg 退出码 {code}：{err.strip()[:400]}")


# ======================================================================
# 几何
# ======================================================================

class Geometry:
    """把源画布映射到目标格子的一套固定变换。

    整段动画共用一个实例 —— 这是"跳跃不会被压平"的保证。
    """

    def __init__(self, cell: int, char_h: int, body_box: tuple[int, int, int, int],
                 src_w: int = 640, src_h: int = 360, top_pad: int = 6):
        self.cell = cell
        self.src_w = src_w
        self.src_h = src_h
        self.top_pad = top_pad

        x1, y1, x2, y2 = body_box
        self.body_w = x2 - x1
        self.body_h = y2 - y1
        if self.body_h <= 0 or self.body_w <= 0:
            raise ValueError(f"body_box 不合法：{body_box}")

        # 角色高 0 = 原生分辨率（1:1，零插值）
        self.char_h = char_h if char_h > 0 else self.body_h
        self.scale = self.char_h / self.body_h

        # 锚点：横向居中；纵向让**源画布顶部**落在 top_pad 处 ——
        # 不是让角色在格子里居中。原因是实测源素材里有动作会顶到画布最上沿
        # （纵向最大 330px），按"居中"算会正好差 3px 把头发裁掉。
        # 按源画布对齐则一眼可验证：源 y=0 → 格子 y=top_pad。
        self.anchor_x = cell / 2.0
        self.src_anchor_x = (x1 + x2) / 2.0
        self.src_anchor_y = float(y2)
        self.anchor_y = top_pad + self.src_anchor_y * self.scale

        # 缩放后的源画布尺寸，以及它左上角该落在格子的哪里
        self.scaled_w = int(round(src_w * self.scale))
        self.scaled_h = int(round(src_h * self.scale))
        self.offset_x = int(round(self.anchor_x - self.src_anchor_x * self.scale))
        self.offset_y = int(round(self.anchor_y - self.src_anchor_y * self.scale))

        if self.scaled_h + top_pad > cell:
            raise ValueError(
                f"格子高 {cell} 装不下源画布高 {self.scaled_h} + 顶部留白 {top_pad}；"
                f"把 --cell 调大，或把 --top-pad 调小")

    def describe(self) -> str:
        return (f"格子 {self.cell}×{self.cell}  角色高 {self.char_h}px  "
                f"缩放 {self.scale:.4f}  锚点 ({self.anchor_x:g}, {self.anchor_y:g})  "
                f"源画布偏移 ({self.offset_x}, {self.offset_y})")

    def cell_size(self) -> int:
        return self.cell

    def ffmpeg_filter(self) -> str:
        """算出把源素材裁成 cell×cell 的 ffmpeg 滤镜串。

        **这是转码链里唯一把"源坐标 ↔ 格子坐标"写死的地方**，而且它同时被
        离线转码（PNG 路线）和运行时解码（视频路线）共用 —— 两边必须一模一样，
        否则同一套素材换条路线走就会错位。所以它在产物里被原样存进 anim.json，
        运行时直接拿来用，不做第二份实现。

        步骤：裁出与源画布相交的那一块 →（需要时）缩放 → 补透明边到满格。
        交出来的矩形一定落在源画布内，所以 crop 不会报越界。
        """
        cell, s = self.cell, self.scale

        left = self.src_anchor_x - self.anchor_x / s
        top = self.src_anchor_y - self.anchor_y / s

        cx0 = max(0, int(math.ceil(left)))
        cy0 = max(0, int(math.ceil(top)))
        cx1 = min(self.src_w, int(math.floor(left + cell / s)))
        cy1 = min(self.src_h, int(math.floor(top + cell / s)))

        cw, ch = cx1 - cx0, cy1 - cy0
        if cw <= 0 or ch <= 0:
            raise ValueError(f"格子 {cell} 与源画布没有交集（anchor_y={self.anchor_y}）")

        # 裁出来的这块在源里的位置，换算成它在格子里的落点
        pad_x = int(round((cx0 - left) * s))
        pad_y = int(round((cy0 - top) * s))

        parts = [f"crop={cw}:{ch}:{cx0}:{cy0}"]

        if abs(s - 1.0) > 1e-9:
            sw, sh = int(round(cw * s)), int(round(ch * s))
            parts.append(f"scale={sw}:{sh}:flags=lanczos")
        else:
            sw, sh = cw, ch

        # pad 至少要留一格，否则 ffmpeg 会因为"没有变化"而报错
        pw, ph = max(1, cell - sw), max(1, cell - sh)
        parts.append(f"pad={cell}:{cell}:{pad_x}:{pad_y}:color=black@0")
        parts.append(f"crop={cell}:{cell}:0:0")

        return ",".join(parts)


def place_frame(frame: np.ndarray, geom: Geometry, resize_cache: dict) -> np.ndarray:
    """把一帧源像素摆进一个 cell×cell 的透明格子。"""
    cell = geom.cell

    if geom.scale != 1.0:
        key = frame.shape
        if key not in resize_cache:
            resize_cache[key] = (geom.scaled_w, geom.scaled_h)
        im = Image.fromarray(frame, "RGBA").resize(
            (geom.scaled_w, geom.scaled_h), Image.LANCZOS)
        src = np.asarray(im)
    else:
        src = frame

    out = np.zeros((cell, cell, 4), dtype=np.uint8)

    ox, oy = geom.offset_x, geom.offset_y
    sx0, sy0 = max(0, -ox), max(0, -oy)
    dx0, dy0 = max(0, ox), max(0, oy)
    w = min(src.shape[1] - sx0, cell - dx0)
    h = min(src.shape[0] - sy0, cell - dy0)
    if w > 0 and h > 0:
        out[dy0:dy0 + h, dx0:dx0 + w] = src[sy0:sy0 + h, sx0:sx0 + w]

    return out


# ======================================================================
# 单个动作的转码
# ======================================================================

def pick_cols(count: int, cell: int, max_side: int = 8192) -> int:
    """排帧列数：尽量排成方的，同时别让精灵图宽到离谱。"""
    soft_max = max(1, max_side // cell)
    return max(1, min(soft_max, math.ceil(math.sqrt(count))))


def convert_one(src: pathlib.Path, out_root: pathlib.Path, geom: Geometry,
                out_fps: float, action_name: str, role: str,
                loop: bool, force: bool = False, quiet: bool = False) -> dict:
    t0 = time.time()
    dest_dir = out_root / "anim" / action_name
    sheet_path = dest_dir / "sheet.png"
    json_path = dest_dir / "sheet.json"

    info = probe_source(src)

    if sheet_path.exists() and json_path.exists() and not force:
        return {"action": action_name, "skipped": True, "bytes": sheet_path.stat().st_size,
                "count": json.loads(json_path.read_text(encoding="utf-8")).get("count", 0),
                "seconds": 0.0}

    frames: list[np.ndarray] = []
    cache: dict = {}
    for f in iter_frames(src, info["width"], info["height"], out_fps):
        frames.append(place_frame(f, geom, cache))

    if not frames:
        raise RuntimeError(f"一帧都没解出来：{src}")

    count = len(frames)
    cell = geom.cell
    cols = pick_cols(count, cell)
    rows = math.ceil(count / cols)

    sheet = np.zeros((rows * cell, cols * cell, 4), dtype=np.uint8)
    for i, fr in enumerate(frames):
        r, c = divmod(i, cols)
        sheet[r * cell:(r + 1) * cell, c * cell:(c + 1) * cell] = fr

    dest_dir.mkdir(parents=True, exist_ok=True)
    Image.fromarray(sheet, "RGBA").save(sheet_path, optimize=True, compress_level=9)

    fps = out_fps if out_fps > 0 else info["fps"]
    manifest = {
        "name": action_name,
        "type": "sheet",
        "sheet": "sheet.png",
        "frameWidth": cell,
        "frameHeight": cell,
        "cols": cols,
        "rows": rows,
        "count": count,
        "fps": round(fps, 3),
        "loop": loop,
        "characterHeight": geom.char_h,
        "anchor": {"x": geom.anchor_x, "y": geom.anchor_y},
        "returnTo": "base",
        "sound": "",
        "_comment": "由 tools/convert_dsh_webm.py 从 dsh-pet 的透明 webm 生成；"
                    "不要手改，改了会被下次转码覆盖。",
    }
    json_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n",
                         encoding="utf-8")

    size = sheet_path.stat().st_size
    if not quiet:
        print(f"  {action_name:34s} {count:4d}帧 {cols}×{rows}  "
              f"{size / 1048576:6.2f} MB  {time.time() - t0:5.1f}s")
    return {"action": action_name, "role": role, "count": count, "cols": cols, "rows": rows,
            "bytes": size, "seconds": time.time() - t0, "sheet": str(sheet_path)}


# ======================================================================
# 清单
# ======================================================================

def action_name_for(folder: str, stem: str) -> tuple[str, str]:
    """由 源目录名 + 文件名 推出 (动作目录名, 角色)。"""
    role = ROLE_OF_FOLDER.get(folder)
    if role is None:
        # 未知目录（源仓库以后加分类了）：当普通小动作，不丢素材
        role = "ambient"

    name = stem
    for pre in STRIP_PREFIXES:
        if name.startswith(pre):
            name = name[len(pre):]
            break

    return f"{role}-{name}", role


def collect(src_root: pathlib.Path, only: list[str] | None = None) -> list[dict]:
    out = []
    for webm in sorted(src_root.rglob("*.webm")):
        rel_folder = webm.parent.relative_to(src_root).as_posix()
        action, role = action_name_for(rel_folder, webm.stem)

        if only and not any(o.lower() in webm.stem.lower() or o.lower() in action.lower()
                            for o in only):
            continue

        out.append({
            "src": webm,
            "folder": rel_folder,
            "action": action,
            "role": role,
        })
    return out


def load_body_box(src_root: pathlib.Path) -> tuple[int, int, int, int]:
    for cand in (src_root / "manifest.json", src_root.parent / "manifest.json"):
        if cand.is_file():
            try:
                data = json.loads(cand.read_text(encoding="utf-8"))
                box = data.get("body_box")
                if isinstance(box, (list, tuple)) and len(box) == 4:
                    return tuple(int(round(float(v))) for v in box)  # type: ignore
            except Exception:
                pass
    return DEFAULT_BODY_BOX


# ======================================================================
# 子命令
# ======================================================================

def cmd_probe(src_root: pathlib.Path, args) -> int:
    items = collect(src_root, args.only)
    print(f"源目录：{src_root}")
    print(f"共 {len(items)} 个 webm\n")

    box = load_body_box(src_root)
    print(f"body_box = {box}  角色 {box[2] - box[0]}×{box[3] - box[1]}\n")

    header = f"{'动作':32s} {'帧数':>5s} {'时长':>7s} {'内容包围盒（相对锚点）':>28s}  重复帧"
    print(header)
    print("-" * len(header) * 2)

    totals = {"count": 0, "dup": 0}
    widest = []
    for it in items:
        info = probe_source(it["src"])
        frames = list(iter_frames(it["src"], info["width"], info["height"], 0))
        n = len(frames)
        cx, fy = (box[0] + box[2]) / 2, box[3]

        minx = miny = 10 ** 9
        maxx = maxy = -10 ** 9
        seen = set()
        dup = 0
        for fr in frames:
            ys, xs = np.where(fr[:, :, 3] > 8)
            if len(xs) == 0:
                continue
            minx, maxx = min(minx, xs.min()), max(maxx, xs.max())
            miny, maxy = min(miny, ys.min()), max(maxy, ys.max())
            h = hash(fr.tobytes())
            if h in seen:
                dup += 1
            else:
                seen.add(h)

        if minx > maxx:
            print(f"{it['action'][:32]:32s} {n:5d}    （全透明）")
            continue

        totals["count"] += n
        totals["dup"] += dup
        dur = n / info["fps"]
        lx, rx = cx - minx, maxx - cx
        ty, by = fy - miny, maxy - fy
        print(f"{it['action'][:32]:32s} {n:5d} {dur:6.1f}s   "
              f"左{lx:5.0f} 右{rx:5.0f} 上{ty:5.0f} 下{by:5.0f}   "
              f"{dup:3d} ({100 * dup / max(1, n):4.1f}%)")

        widest.append((max(lx, rx), it["action"], lx, rx, ty, by))

    print()
    print(f"合计 {totals['count']} 帧，其中完全重复 {totals['dup']} 帧"
          f"（{100 * totals['dup'] / max(1, totals['count']):.1f}%）")

    widest.sort(reverse=True)
    print("\n横向最宽的 10 个动作（决定格子该多宽）：")
    for w, name, lx, rx, ty, by in widest[:10]:
        print(f"  {name[:36]:36s} 左{lx:5.0f} 右{rx:5.0f} → 需要半宽 {w:4.0f}px")

    max_up = max((t for _, _, _, _, t, _ in widest), default=0)
    max_down = max((b for _, _, _, _, _, b in widest), default=0)
    print(f"\n纵向最大：锚点上方 {max_up:.0f}px、下方 {max_down:.0f}px")
    return 0


def cmd_bench(src_root: pathlib.Path, args) -> int:
    """体积对照实验：同一批动作，几套参数各转一遍，比体积。"""
    box = load_body_box(src_root)
    body_h = box[3] - box[1]

    # 样本挑得有代表性：最典型的待机、外加点击回应、最宽的特效、中等宽度
    picks = args.only or ["待机呼吸休闲", "点击回应 - 开心跃动",
                          "蝴蝶蜜蜂环绕头顶开花", "蓝鲸现世"]
    items = collect(src_root, picks)
    if not items:
        items = collect(src_root)[:4]
    print(f"实验样本 {len(items)} 个：{[i['action'] for i in items]}")

    # 只变帧率与格子；char_h = 0 表示原生分辨率（不缩放）
    variants = [
        (384, 0, 24),
        (384, 0, 16),
        (384, 0, 12),
        (320, 0, 24),
    ]

    out_root = ROOT / ".tmp-dsh-bench"
    rows = []
    for cell, char_h, fps in variants:
        geom = Geometry(cell, char_h, box)
        total = frames_total = 0
        t0 = time.time()
        for it in items:
            r = convert_one(it["src"], out_root / f"{cell}-{fps}", geom, fps,
                            it["action"], it["role"], loop=False, force=True, quiet=True)
            total += r["bytes"]
            frames_total += r["count"]
        rows.append((cell, geom.char_h, fps, total, frames_total, time.time() - t0))
        print(f"  格子 {cell:4d} 角色 {geom.char_h:4d} {fps:2d}fps → "
              f"{total / 1048576:7.2f} MB / {frames_total} 帧  ({time.time() - t0:5.1f}s)")

    # 全量外推。总帧数由 --probe 实测得到（106 个动作 × 241 帧）
    TOTAL_FRAMES = 25423
    print()
    print(f"{'格子':>6s} {'输出帧率':>8s} {'样本体积':>10s} {'每帧':>8s} "
          f"{'全量 25423 帧外推':>18s}")
    for cell, char_h, fps, total, frames, secs in rows:
        per = total / max(1, frames)
        est = per * TOTAL_FRAMES
        print(f"{cell:6d} {fps:8d} {total / 1048576:9.2f}M "
              f"{per / 1024:7.1f}K {est / 1048576:16.0f} MB")

    return 0


def cmd_convert(src_root: pathlib.Path, args) -> int:
    box = load_body_box(src_root)
    geom = Geometry(args.cell, args.char_h, box)
    out_root = pathlib.Path(args.out).resolve()

    items = collect(src_root, args.only)
    if not items:
        print("❌ 没有匹配到任何源素材", file=sys.stderr)
        return 1

    print(f"源：{src_root}")
    print(f"出：{out_root}")
    print(f"{geom.describe()}")
    print(f"输出帧率：{args.fps if args.fps else '跟随源素材'}")
    print(f"共 {len(items)} 个动作，{args.jobs} 并发\n")

    out_root.mkdir(parents=True, exist_ok=True)

    # 动作名 → 目录名。角色决定 loop：待机与拖拽是持续循环，其余播一次
    def loop_of(role: str) -> bool:
        return role in ("idle", "drag")

    done, failed, skipped = [], [], []
    t0 = time.time()

    if args.jobs <= 1:
        for it in items:
            try:
                r = convert_one(it["src"], out_root, geom, args.fps, it["action"], it["role"],
                                loop_of(it["role"]), force=args.force)
                (skipped if r.get("skipped") else done).append(r)
            except Exception as e:
                failed.append((it["action"], str(e)))
                print(f"  ❌ {it['action']}：{e}", file=sys.stderr)
    else:
        with concurrent.futures.ProcessPoolExecutor(max_workers=args.jobs) as pool:
            futs = {pool.submit(convert_one, it["src"], out_root, geom, args.fps,
                                it["action"], it["role"], loop_of(it["role"]),
                                args.force, False): it for it in items}
            for fut in concurrent.futures.as_completed(futs):
                it = futs[fut]
                try:
                    r = fut.result()
                    (skipped if r.get("skipped") else done).append(r)
                    if not r.get("skipped"):
                        print(f"  {it['action']:34s} {r['count']:4d}帧 "
                              f"{r['cols']}×{r['rows']}  {r['bytes'] / 1048576:6.2f} MB")
                except Exception as e:
                    failed.append((it["action"], str(e)))
                    print(f"  ❌ {it['action']}：{e}", file=sys.stderr)

    total = sum(r["bytes"] for r in done) + sum(r["bytes"] for r in skipped)
    frames = sum(r["count"] for r in done) + sum(r["count"] for r in skipped)

    _write_attribution(out_root)
    _write_index(out_root, geom, done + skipped)

    print()
    print(f"完成：{len(done)} 个新转 + {len(skipped)} 个跳过，失败 {len(failed)}")
    print(f"帧数合计 {frames}，体积 {total / 1048576:.1f} MB，用时 {time.time() - t0:.0f}s")
    if failed:
        for name, err in failed[:10]:
            print(f"  ❌ {name}：{err}", file=sys.stderr)
        return 1
    return 0


# ======================================================================
# 视频包：不转码，只把源素材原样搬过去 + 写一份元数据
# ======================================================================

def emit_one_video(it: dict, out_root: pathlib.Path, geom: Geometry,
                   src_w: int, src_h: int, force: bool = False) -> dict:
    """一个动作 = 一个目录：video.webm（原样）+ anim.json（元数据）。"""
    role = it["role"]
    dest = out_root / "anim" / it["action"]
    dest.mkdir(parents=True, exist_ok=True)

    target = dest / "video.webm"
    if force or not target.exists() or target.stat().st_size != it["src"].stat().st_size:
        shutil.copyfile(it["src"], target)

    info = probe_source(it["src"])

    manifest = {
        "name": it["action"],
        "type": "video",
        "video": "video.webm",
        "filter": geom.ffmpeg_filter(),
        "frameWidth": geom.cell,
        "frameHeight": geom.cell,
        "cols": 1,
        "rows": max(1, info.get("frames") or 1),
        "count": info.get("frames") or 0,
        "fps": round(info["fps"], 3),
        "loop": role in ("idle", "drag"),
        "characterHeight": geom.char_h,
        "anchor": {"x": geom.anchor_x, "y": geom.anchor_y},
        "returnTo": "base",
        "sound": "",
        "role": role,
        "source": it["src"].name,
        "_comment": "由 tools/convert_dsh_webm.py 生成。frameWidth/Height 是解码后的格子尺寸；"
                    "filter 是裁进格子的 ffmpeg 滤镜串，运行时按它解码。不要手改。",
    }
    (dest / "anim.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    return {"action": it["action"], "role": role, "count": manifest["count"],
            "bytes": target.stat().st_size, "video": str(target)}


def extract_still(src: pathlib.Path, geom: Geometry, out_png: pathlib.Path) -> tuple[int, int]:
    """用**同一个滤镜**取首帧存成 PNG，当静态本体用。

    必须走同一个滤镜：静态图是"没在播动画、鼠标也不在上面"时显示的那一张，
    它和动画第一帧的构图差一个像素，切换时就会看到角色跳一下。

    实现上刻意**不落临时文件**：先让 ffmpeg 写 temp.png、再用 PIL 读、再删掉 —— 
    多一个中间文件就多一次删除，而受限环境里的删除拦截会让整条流水线无声中断
    （脚本连收尾的 index.json 都写不出来，还留下一个垃圾文件）。
    现在 ffmpeg 直接写内存（pipe:1），PIL 读内存，PNG 字节一次写入目标文件。
    """
    p = subprocess.run(
        [ffmpeg_exe(), "-v", "error", "-c:v", "libvpx-vp9", "-i", str(src),
         "-vf", geom.ffmpeg_filter(), "-frames:v", "1", "-pix_fmt", "rgba",
         "-f", "image2pipe", "-vcodec", "png", "pipe:1"],
        capture_output=True, timeout=120)

    if p.returncode != 0 or not p.stdout:
        err = p.stderr.decode("utf-8", "replace").strip()[:300]
        raise RuntimeError(f"取静态帧失败：{err}")

    im = Image.open(io.BytesIO(p.stdout)).convert("RGBA")
    if im.size != (geom.cell, geom.cell):
        raise RuntimeError(f"静态帧尺寸 {im.size} 不等于格子 {geom.cell}×{geom.cell}")

    # 压到最小 —— 它只是"不动时的那一张"，没有动画信息
    buf = io.BytesIO()
    im.save(buf, format="PNG", optimize=True, compress_level=9)
    out_png.write_bytes(buf.getvalue())
    return im.size


def cmd_emit_video(src_root: pathlib.Path, args) -> int:
    box = load_body_box(src_root)
    geom = Geometry(args.cell, args.char_h, box, top_pad=args.top_pad)
    out_root = pathlib.Path(args.out).resolve()

    items = collect(src_root, args.only)
    if not items:
        print("❌ 没有匹配到任何源素材", file=sys.stderr)
        return 1

    # 源画布尺寸从第一个素材读，用来校验格子装不装得下
    first = probe_source(items[0]["src"])
    geom = Geometry(args.cell, args.char_h, box, first["width"], first["height"],
                    top_pad=args.top_pad)

    print(f"源：{src_root}  （{first['width']}×{first['height']}）")
    print(f"出：{out_root}")
    print(f"{geom.describe()}")
    print(f"滤镜：{geom.ffmpeg_filter()}")
    print(f"共 {len(items)} 个动作（原样搬运，不重新编码）\n")

    out_root.mkdir(parents=True, exist_ok=True)
    done = []
    for i, it in enumerate(items, 1):
        try:
            r = emit_one_video(it, out_root, geom, first["width"], first["height"], args.force)
            done.append(r)
            if i % 20 == 0 or i == len(items):
                print(f"  {i}/{len(items)} ……")
        except Exception as e:
            print(f"  ❌ {it['action']}：{e}", file=sys.stderr)

    # 静态本体：拿待机动作的首帧，走同一个滤镜
    idle = next((r for r in done if r["role"] == "idle"), None)
    if idle:
        still_src = pathlib.Path(idle["video"])
        size = extract_still(still_src, geom, out_root / "whale.png")
        print(f"\n静态本体 whale.png {size[0]}×{size[1]}，取自 {still_src.parent.name}")
    else:
        print("\n⚠️  没找到待机动作，没生成静态本体", file=sys.stderr)

    _write_attribution(out_root)
    _write_index(out_root, geom, done, emit="video")

    total = sum(r["bytes"] for r in done)
    frames = sum(r["count"] for r in done)
    print(f"\n完成：{len(done)} 个动作，{frames} 帧，原样搬运 {total / 1048576:.1f} MB")
    return 0 if len(done) == len(items) else 1


def _write_attribution(out_root: pathlib.Path) -> None:
    """许可要求署名 —— 写在产物目录里，别指望以后有人记得补。"""
    text = """鲸记 WhaleLog · 形象素材「溟月」

来源与许可
==========
本目录下的精灵图动画，由 dsh-pet 项目的透明 webm 素材转码生成。

  角色        OC「溟月」
  画师        上善无形
  素材来源    https://github.com/MerZlin/dsh-pet-indesktop
              （上游：https://github.com/PC2005-cloud/dsh-pet）
  许可        CC BY-NC-SA 类同人素材 —— **仅限个人非商业使用**，须保留署名与来源
  转码工具    tools/convert_dsh_webm.py

⚠️ 重要
--------
这批素材**不得用于任何商业或盈利场景**。鲸记目前是个人自用工具，属于允许范围。
如果鲸记将来要发布、收费或以任何形式商用，**必须先把整个 characters\\dsh\\ 目录撤下**，
换回自有的或者已获授权的形象素材。

署名不是可选的礼貌，而是使用许可的生效条件 —— 请勿删除本文件。

技术说明
--------
每个动作一个子目录，目录名就是动作名，里面是：

  video.webm   原始动画（**未经任何重新编码**，画质与源素材逐像素相同）
  anim.json    帧表：帧数、帧率、角色高、锚点，以及把画面裁进格子的 ffmpeg 滤镜

鲸记在运行时按 anim.json 里的 filter 逐帧解码 webm。之所以不用「预先转成精灵图 PNG」：
PNG 对这类带透明通道的连续画面只能压到约 3 倍，而源素材的 VP9 压了约 100 倍 ——
106 个动作全转成 PNG 要 1.8 GB，直接播放 webm 只要 52 MB。详见
docs/动态素材规格.md 的「视频素材」一节。

目录名的前缀决定程序怎么用它：

  idle-*         持续循环的待机动作（同一时刻只会播一个）
  clickBounce-*  点击小鲸鱼时的回应（多个时随机挑一个）
  drag-*         被拖拽时循环播
  alert-*        到点提醒时播
  ambient-*      闲置时随机冒出来的小动作
  move-*         贴边滑到屏幕边缘时播
  turn-*         转向（当前未接入）

以上由 dsh-pet 的素材分类（idle/turn/move/click/drag/random/events）自动映射而来。

whale.png 是静态本体（不动时显示的那一张），取自待机动作首帧、走同一个滤镜，
所以「静止 → 播第一帧」不会跳。
"""
    (out_root / "来源与许可.txt").write_text(text, encoding="utf-8")


def _write_index(out_root: pathlib.Path, geom: Geometry, results: list[dict],
                 emit: str = "sheet") -> None:
    """产物索引：给人和工具看的一览。"""
    by_role: dict[str, list[str]] = {}
    for r in results:
        role = r.get("role", "")
        if not role:
            role = r["action"].split("-", 1)[0]
        by_role.setdefault(role, []).append(r["action"])

    data = {
        "emit": emit,
        "geometry": {
            "cell": geom.cell,
            "characterHeight": geom.char_h,
            "anchorX": geom.anchor_x,
            "anchorY": geom.anchor_y,
            "scaleFromSource": round(geom.scale, 5),
            "floatFilter": geom.ffmpeg_filter(),
        },
        "actions": sorted(r["action"] for r in results),
        "byRole": {k: sorted(v) for k, v in sorted(by_role.items())},
        "actionCount": len(results),
        "totalFrames": sum(r["count"] for r in results),
        "totalBytes": sum(r["bytes"] for r in results),
    }
    (out_root / "index.json").write_text(
        json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def cmd_check(target: pathlib.Path, args) -> int:
    """校验产物：锚点是否漂、有没有被裁、帧表跟图对不对得上。"""
    anim_root = target / "anim"
    if not anim_root.is_dir():
        print(f"❌ 没有 anim 目录：{anim_root}", file=sys.stderr)
        return 1

    dirs = sorted(d for d in anim_root.iterdir() if d.is_dir())
    print(f"检查 {len(dirs)} 个动作：{anim_root}\n")

    problems, warned = [], []
    drift_worst = 0.0
    drift_worst_name = ""

    for d in dirs:
        # 帧表文件名：精灵图动作是 sheet.json，视频动作是 anim.json
        jsons = sorted(d.glob("*.json"))
        mf = next((j for j in jsons if j.name in ("sheet.json", "anim.json")), None) \
            or (jsons[0] if jsons else None)
        if mf is None:
            problems.append(f"{d.name}：缺帧表（sheet.json / anim.json）")
            continue
        man = json.loads(mf.read_text(encoding="utf-8"))
        sheet_path = d / man.get("sheet", "sheet.png")

        cell = man["frameWidth"]
        cols, rows, count = man["cols"], man["rows"], man["count"]
        ax, ay = man["anchor"]["x"], man["anchor"]["y"]
        char_h = man["characterHeight"]

        if man["frameHeight"] != cell:
            warned.append(f"{d.name}：格子不是正方形（{cell}×{man['frameHeight']}）")

        if man.get("type") == "video":
            vid = d / man.get("video", "video.webm")
            if not vid.is_file():
                problems.append(f"{d.name}：找不到 {man.get('video')}")
                continue
            flt = man.get("filter", "")
            if not flt or "crop=" not in flt:
                problems.append(f"{d.name}：anim.json 里没有可用的 filter")
                continue
            # 真解一帧出来，确认滤镜能跑通、尺寸对得上
            tmp = d / ".check.png"
            try:
                p = subprocess.run(
                    [ffmpeg_exe(), "-v", "error", "-c:v", "libvpx-vp9", "-i", str(vid),
                     "-vf", flt, "-frames:v", "1", "-pix_fmt", "rgba", "-y", str(tmp)],
                    capture_output=True, text=True, errors="replace", timeout=120)
                if p.returncode != 0 or not tmp.exists():
                    problems.append(f"{d.name}：滤镜跑不通（{p.stderr.strip()[:120]}）")
                    continue
                im = Image.open(tmp)
                if im.size != (cell, cell):
                    problems.append(f"{d.name}：解码出来是 {im.size}，帧表写的是 {cell}×{cell}")
                else:
                    al = np.asarray(im.convert("RGBA"))[:, :, 3]
                    if (al > 8).sum() == 0:
                        problems.append(f"{d.name}：首帧整幅全透明（滤镜裁错位置了？）")
            finally:
                tmp.unlink(missing_ok=True)
            continue

        img = Image.open(sheet_path)
        if img.size != (cell * cols, cell * rows):
            problems.append(f"{d.name}：精灵图 {img.size} 与帧表 {cell * cols}×{cell * rows} 不符")
            continue

        arr = np.asarray(img.convert("RGBA"))
        centers, clipped = [], 0
        for i in range(count):
            r, c = divmod(i, cols)
            tile = arr[r * cell:(r + 1) * cell, c * cell:(c + 1) * cell, 3]
            ys, xs = np.where(tile > 8)
            if len(xs) == 0:
                continue
            centers.append((xs.min() + xs.max()) / 2.0)
            # 贴边（0 或 cell-1）说明被裁了
            if xs.min() <= 0 or xs.max() >= cell - 1 or ys.min() <= 0 or ys.max() >= cell - 1:
                clipped += 1

        if centers:
            drift = max(abs(c - ax) for c in centers)
            if drift > drift_worst:
                drift_worst, drift_worst_name = drift, d.name
            if drift > 2.0:
                problems.append(f"{d.name}：横向中心漂了 {drift:.1f}px（容差 2px）")
        if clipped:
            warned.append(f"{d.name}：{clipped}/{count} 帧贴到了格子边缘（可能被裁）")

        # 脚底是否落在锚点附近（取第一帧的可见底边）
        r, c = 0, 0
        tile = arr[0:cell, 0:cell, 3]
        ys, _ = np.where(tile > 8)
        if len(ys):
            feet = ys.max()
            if abs(feet - ay) > char_h * 0.25:
                warned.append(f"{d.name}：首帧脚底 y={feet}，离锚点 {ay:g} 有点远"
                              f"（角色高 {char_h}）")

    print(f"横向漂移最大：{drift_worst:.2f}px（{drift_worst_name}）")
    print(f"\n❌ 硬问题 {len(problems)} 条")
    for p in problems[:20]:
        print(f"  {p}")
    print(f"\n⚠️  提醒 {len(warned)} 条")
    for w in warned[:20]:
        print(f"  {w}")
    return 1 if problems else 0


# ======================================================================

def main() -> int:
    ap = argparse.ArgumentParser(
        description="dsh-pet webm → 鲸记精灵图",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__.split("## 为什么不是")[0])
    ap.add_argument("--src", default=str(DEFAULT_SRC), help="源素材 videos 目录")
    ap.add_argument("--out", default=str(DEFAULT_OUT), help="输出目录")
    ap.add_argument("--cell", type=int, default=DEFAULT_CELL, help=f"格子边长（默认 {DEFAULT_CELL}）")
    ap.add_argument("--char-h", type=int, default=DEFAULT_CHAR_H,
                    help="角色在画布里的高度（默认 0 = 源素材原生高度，即不缩放）")
    ap.add_argument("--top-pad", type=int, default=DEFAULT_TOP_PAD,
                    help=f"源画布顶部在格子里留多少透明像素（默认 {DEFAULT_TOP_PAD}）")
    ap.add_argument("--fps", type=float, default=DEFAULT_FPS, help="输出帧率（默认 0 = 跟随源）")
    ap.add_argument("--only", nargs="*", help="只处理名字含这些关键词的动作")
    ap.add_argument("--jobs", type=int, default=max(1, (os.cpu_count() or 4) - 1), help="并发数")
    ap.add_argument("--force", action="store_true", help="已存在也重新转")
    ap.add_argument("--emit", choices=("video", "sheet"), default="video",
                    help="video=原样搬运 webm + 元数据（默认，52 MB）；"
                         "sheet=转成精灵图 PNG（1.8 GB，只在对视频路线有疑虑时才用）")
    ap.add_argument("--probe", action="store_true", help="只测源素材，不写文件")
    ap.add_argument("--bench", action="store_true", help="体积对照实验")
    ap.add_argument("--check", metavar="目录", help="校验已有产物")

    args = ap.parse_args()

    if args.check:
        return cmd_check(pathlib.Path(args.check).resolve(), args)

    src_root = pathlib.Path(args.src).resolve()
    if not src_root.is_dir():
        print(f"❌ 源素材目录不存在：{src_root}", file=sys.stderr)
        print("   先跑：python tools/fetch_dsh_assets.py", file=sys.stderr)
        return 1

    if args.probe:
        return cmd_probe(src_root, args)
    if args.bench:
        return cmd_bench(src_root, args)
    if args.emit == "video":
        return cmd_emit_video(src_root, args)
    return cmd_convert(src_root, args)


if __name__ == "__main__":
    sys.exit(main())

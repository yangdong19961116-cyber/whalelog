# -*- coding: utf-8 -*-
"""把新画的「表情差分」处理成能直接放进 Assets\\ 的形象图。

为什么需要它
------------
形象差分（睁眼 / 闭眼 / 惊讶……）只有在**同一画布、逐像素对齐**时才能整张替换，
否则鼠标一动、表情一换，角色就会跳一下 —— 这个偏移肉眼非常敏感，
但靠手工在图像编辑器里对齐既费时又对不准。

这个脚本做三件事，然后把对齐误差量出来给你看：

  1. **抠背景**：把与画面四边相连的纯色背景（通常是白底）变成透明。
     只有连到边框的背景才会被去掉 —— 角色内部的白色（头饰、衣领）会被保住。
  2. **归到基准画布**：缩放成和基准图一样的画布尺寸。
  3. **对齐**：以基准图的透明通道为靶子，搜索最佳缩放与平移，让两张图的前景重合。
     对齐质量用 IoU 表示（交集 ÷ 并集）。**0.98 以上算合格**，
     低于 0.95 基本就是画得不一样或框错了，别硬用。

用法
----
    python tools/prepare_expression.py <输入图> <输出.png> [--base 基准图]

    # 例：把白底的 closeeye.jpg 处理成和 DSniang1.png 对齐的差分图
    python tools/prepare_expression.py closeeye.jpg out/closeeye.png

    # 换了基准图（默认取 DSniang1.png）
    python tools/prepare_expression.py 惊讶.png out/惊讶.png --base 别的基准.png

需要 Pillow 与 numpy（`pip install pillow numpy`）。
脚本只读输入图、只写输出图，不碰基准图。
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

try:
    import numpy as np
    from PIL import Image
except ImportError as e:  # pragma: no cover - 环境问题，给出能照做的提示
    raise SystemExit(
        f"缺少依赖：{e.name}。装一下再跑：pip install pillow numpy"
    )

ROOT = Path(__file__).resolve().parents[1]

DEFAULT_BASE = ROOT / "src" / "WhaleLog" / "Resources" / "Assets" / "DSniang1.png"

# 判定"这就是背景色"的容差（0~255，逐通道最大差值）。
# JPG 在轮廓附近有压缩噪点，太小会把噪点当角色留下白边，太大会吃掉浅色轮廓。
BG_TOLERANCE = 14

# 边缘软化带宽：把紧贴背景的那一圈按"有多接近背景色"给部分透明度，
# 避免深色轮廓外留一圈浅色描边（放在深色桌面上会像发光的毛边）。
SOFTEN_SPAN = 60

# 对齐搜索范围：缩放 ±3%、平移 ±12px。差分图正常不会超出这个量，
# 真超了说明画布根本不一样，应该回去改图而不是让脚本硬凑。
SCALE_RANGE = 0.03
SCALE_STEPS = 7
SHIFT_LIMIT = 12


def load_rgba(path: Path) -> Image.Image:
    if not path.exists():
        raise SystemExit(f"找不到图片：{path}")
    return Image.open(path).convert("RGBA")


def has_real_alpha(im: Image.Image) -> bool:
    """图里已经有真正的透明区（而不是整张不透明）。"""
    alpha = np.asarray(im)[:, :, 3]
    return float(alpha.min()) < 250


def remove_connected_background(im: Image.Image) -> Image.Image:
    """把与四边相连的近似纯色区域变透明。

    做法：先按"接近背景色"筛出候选像素，再从四条边做泛洪（用逐次膨胀实现，
    每轮把已确定的外沿向外长 1 像素、并始终限制在候选集合内）。
    只有连到边框的那一块会被去掉，角色内部同色的区域因为被轮廓包着，碰不到边框。
    """
    rgb = np.asarray(im)[:, :, :3]
    a = rgb.astype(np.int16)
    h, w, _ = a.shape

    border = np.concatenate([a[0], a[-1], a[:, 0], a[:, -1]]).reshape(-1, 3)
    bg = np.median(border, axis=0)

    dist = np.abs(a - bg).max(axis=2)
    candidate = dist <= BG_TOLERANCE

    seed = np.zeros_like(candidate)
    seed[0, :] = candidate[0, :]
    seed[-1, :] = candidate[-1, :]
    seed[:, 0] = candidate[:, 0]
    seed[:, -1] = candidate[:, -1]

    cur = seed
    for _ in range(max(h, w) * 2):
        nxt = cur.copy()
        nxt[1:, :] |= cur[:-1, :]
        nxt[:-1, :] |= cur[1:, :]
        nxt[:, 1:] |= cur[:, :-1]
        nxt[:, :-1] |= cur[:, 1:]
        nxt &= candidate
        if np.array_equal(nxt, cur):
            break
        cur = nxt

    outside = cur
    alpha = np.full((h, w), 255, np.uint8)
    alpha[outside] = 0

    # 紧贴背景的一圈按"有多白"给部分透明度，消掉轮廓外的浅色描边
    band = np.zeros_like(outside)
    band[1:, :] |= outside[:-1, :]
    band[:-1, :] |= outside[1:, :]
    band[:, 1:] |= outside[:, :-1]
    band[:, :-1] |= outside[:, 1:]
    band &= ~outside

    whiteness = np.clip(1.0 - dist / SOFTEN_SPAN, 0.0, 1.0)
    soft = np.round(255 * (1.0 - whiteness)).astype(np.uint8)
    alpha[band] = np.minimum(alpha[band], soft[band])

    out = np.dstack([rgb, alpha]).astype(np.uint8)
    print(f"  背景色 ≈ {bg.astype(int).tolist()}，"
          f"去掉 {outside.mean() * 100:.1f}% 的像素，软化边缘 {int(band.sum())} 像素")
    return Image.fromarray(out, "RGBA")


def mask_of(im: Image.Image) -> np.ndarray:
    return np.asarray(im)[:, :, 3] > 128


def iou(m1: np.ndarray, m2: np.ndarray) -> float:
    union = np.logical_or(m1, m2).sum()
    return 0.0 if union == 0 else float(np.logical_and(m1, m2).sum()) / float(union)


def place_on_canvas(scaled: Image.Image, canvas_size: tuple[int, int],
                    offset: tuple[int, int] = (0, 0)) -> Image.Image:
    """把缩放后的图居中放到画布上，再加一个平移量。"""
    cw, ch = canvas_size
    canvas = Image.new("RGBA", canvas_size, (0, 0, 0, 0))
    canvas.alpha_composite(scaled, ((cw - scaled.size[0]) // 2 + offset[0],
                                    (ch - scaled.size[1]) // 2 + offset[1]))
    return canvas


def align_to_base(new: Image.Image, base: Image.Image) -> tuple[Image.Image, float, float, float]:
    """把 new 缩放到 base 的画布并平移对齐。返回 (图, 缩放, dx, dy)。

    搜索分两级：先按步长 4 扫平移的粗解，再从粗解附近按步长 1 收敛。
    缩放也扫一小段 —— 差分图正常和基准同画布，但作者偶尔会导出成别的边长。
    """
    cw, ch = base.size
    base_mask = mask_of(base)

    def score(mask: np.ndarray, dx: int, dy: int) -> float:
        return iou(base_mask, np.roll(np.roll(mask, dy, axis=0), dx, axis=1))

    best = (-1.0, 1.0, 0, 0)

    for scale in np.linspace(1 - SCALE_RANGE, 1 + SCALE_RANGE, SCALE_STEPS):
        size = (max(1, round(cw * scale)), max(1, round(ch * scale)))
        mask = mask_of(place_on_canvas(new.resize(size, Image.LANCZOS), (cw, ch)))

        # 该缩放下的最佳平移：粗扫 → 收敛
        local = (score(mask, 0, 0), 0, 0)
        for step, limit in ((4, SHIFT_LIMIT), (1, 3)):
            for _ in range(30):                 # 收敛保护，正常几轮就停
                improved = False
                _, cx, cy = local
                for dy in range(cy - limit, cy + limit + 1, step):
                    for dx in range(cx - limit, cx + limit + 1, step):
                        v = score(mask, dx, dy)
                        if v > local[0]:
                            local = (v, dx, dy)
                            improved = True
                if not improved:
                    break

        if local[0] > best[0]:
            best = (local[0], float(scale), local[1], local[2])

    _, scale, dx, dy = best
    size = (max(1, round(cw * scale)), max(1, round(ch * scale)))
    return place_on_canvas(new.resize(size, Image.LANCZOS), (cw, ch), (dx, dy)), scale, dx, dy


def main() -> int:
    ap = argparse.ArgumentParser(
        description="抠背景 + 缩放到基准画布 + 与基准逐像素对齐，产出可做表情差分的 PNG")
    ap.add_argument("source", help="输入图（可以是白底 JPG）")
    ap.add_argument("output", help="输出的 PNG 路径")
    ap.add_argument("--base", default=str(DEFAULT_BASE), help=f"基准形象图，默认 {DEFAULT_BASE.name}")
    ap.add_argument("--no-bg", action="store_true", help="跳过抠背景（输入已经带透明通道时）")
    ap.add_argument("--no-diff", action="store_true",
                    help="不产出「对齐差异图」。输出目录就是 Assets\\ 时用它，免得往素材目录里塞诊断图")
    args = ap.parse_args()

    src = Path(args.source).resolve()
    dst = Path(args.output).resolve()
    base_path = Path(args.base).resolve()

    print(f"输入：{src}")
    print(f"基准：{base_path}")

    new = load_rgba(src)
    base = load_rgba(base_path)
    print(f"  输入 {new.size[0]}×{new.size[1]}，基准 {base.size[0]}×{base.size[1]}")

    if base.size[0] != base.size[1]:
        print("  提示：基准不是正方形，按宽高各自缩放。", file=sys.stderr)

    if args.no_bg:
        print("  跳过抠背景（--no-bg）")
        if not has_real_alpha(new):
            print("  ⚠ 这张图没有透明通道，跳过抠背景会留下一整块底色。", file=sys.stderr)
    elif has_real_alpha(new):
        print("  输入已带透明通道，跳过抠背景")
    else:
        new = remove_connected_background(new)

    ys, xs = np.where(mask_of(new))
    if len(ys) == 0:
        print("  抠完一个前景像素都不剩 —— 背景色判定失败，检查一下输入图。", file=sys.stderr)
        return 1
    print(f"  前景 bbox = ({xs.min()}, {ys.min()}, {xs.max() + 1}, {ys.max() + 1})")

    aligned, scale, dx, dy = align_to_base(new, base)
    base_mask = mask_of(base)
    value = iou(base_mask, mask_of(aligned))

    print(f"  对齐：缩放 {scale:.4f}，平移 dx={dx} dy={dy}")
    print(f"  与基准的 IoU = {value:.4f}（并集 {int(np.logical_or(base_mask, mask_of(aligned)).sum())} 像素）")

    if value >= 0.98:
        print("  → 对齐良好，可以直接用")
    elif value >= 0.95:
        print("  → 勉强能用；建议确认差分图是不是在基准图上改出来的（而不是另画的）", file=sys.stderr)
    else:
        print("  → 对齐太差，别用。两张图很可能不是同一套画布/同一版形象。", file=sys.stderr)

    dst.parent.mkdir(parents=True, exist_ok=True)
    aligned.save(dst)
    print(f"已写出 {dst}")

    # 顺带出一张差异图，让人一眼看出哪里没对上（红=基准多出，青=新图多出，白=重合）
    if not args.no_diff:
        h, w = base.size[1], base.size[0]
        vis = np.zeros((h, w, 3), np.uint8)
        na = mask_of(aligned)
        vis[base_mask] = [255, 60, 60]
        vis[na] = [60, 200, 255]
        vis[np.logical_and(base_mask, na)] = [255, 255, 255]
        diff_path = dst.with_name(dst.stem + "-对齐差异.png")
        Image.fromarray(vis).save(diff_path)
        print(f"对齐差异图 {diff_path}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())

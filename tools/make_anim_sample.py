"""生成动态素材样例：精灵图 + 帧表 JSON（同时是规格的参考实现）。

规格常量集中在下面「规范画布」一节，改这里就是改规格。

为什么画布要从 610 变成 720
---------------------------
现有本体素材 DSniang1.png 的角色**贴着画布的右边和下边**（左留 45、上留 10、
右 0、下 0）。意味着任何向右的位移、横向拉伸、向下压扁都会立刻被裁掉。
实测那版 8 帧弹跳：6 帧被裁（2 帧裁右侧、4 帧裁顶部）。

所以规定一个有余量的规范画布：

    画布 720×720   角色高 600（占 83%）   横向居中（x 中心 = 360）
    脚底基线 y = 660   →  上留 61 / 下留 59 / 左右各留 77

角色**大小一点没变**（还是 600 高），只是四周有了余量：
上下可动 ±60px，横向可放到 1.27× 才触边。

唯一一条硬规则
--------------
每一帧都必须让角色的「横向中心」和「脚底」落在同一个点上：

    x 中心 = 360        脚底 = 660（跳跃帧整体上移，即脚底 < 660）

帧与帧之间只允许「整体垂直位移」和「绕该基准点的缩放」，
**横向中心绝对不能左右漂** —— 判据是每帧角色的 alpha 边界框横向中心必须一致。

垂直方向能走多远，**不能按"上边留了 60px"来算**
-----------------------------------------------
脚底在 660、角色高 600，所以静止时头顶在 y=60 —— 上边只剩 60px。
但这 60px 里还要装下**纵向拉伸**：拉伸到 1.07 时角色高 642，头顶会再往上多出 42px。
第一版 PLAN 就是按"能上移 34px"写的，结果最高帧头顶落到 y=-15，被画布裁掉了
（`tools/check_anim_assets.py` 报「帧3（上）」）。

准确的余量算法（也是本脚本末尾 assert_headroom 用的判据）：

    头顶 y = 661 − round(角色高 × 纵向缩放) + 垂直位移    必须 ≥ 2
    脚底 y = 头顶 y + round(角色高 × 纵向缩放) − 1        必须 ≤ 717

即：**拉伸越猛，可用于"整体上移"的额度越小**。改动 PLAN 之后跑一遍脚本，
它会逐帧打印头顶/脚底的余量，不够就当场报错退出。

产物：
    <输出目录>/sheet.png      精灵图（4 列 × 2 行 = 8 帧）
    <输出目录>/sheet.json     帧表
    <输出目录>/whale.png      同一规范画布下的静态本体（一起换掉才不会跳）
    outputs/动画素材规格/*.png 预览与对齐校验图

用法：
    python tools/make_anim_sample.py

完整规格（帧表字段、动作名、放置目录、排查顺序）见 docs\\动态素材规格.md；
交付前再跑一次 tools\\check_anim_assets.py。
"""

from __future__ import annotations

import json
import pathlib
import sys

from PIL import Image, ImageDraw

ROOT = pathlib.Path(__file__).resolve().parent.parent
SRC = ROOT / "src" / "WhaleLog" / "Resources" / "Assets" / "DSniang1.png"

# ---------------- 规范画布（规格的唯一来源） ----------------
CELL = 720          # 每格画布边长
CHAR_H = 600        # 角色高度（= 现有本体的角色高度，屏幕上大小不变）
ANCHOR_X = CELL // 2   # 角色横向中心 = 360
FOOT_Y = 660        # 脚底基线

# ---------------- 动作参数 ----------------
COLS, ROWS = 4, 2   # 8 帧排成 4×2
FPS = 24            # 实测：渲染时钟 ~64Hz，24 fps 能打满

# (垂直位移, 横向缩放, 纵向缩放) —— 经典 squash & stretch
# 正数 = 整体下移（落地压扁），负数 = 上移（跳起）
#
# 数值按「头顶位置」倒推，别凭感觉给：
#     头顶 y = 661 − round(600 × 纵向缩放) + 垂直位移
# 拉伸到 1.07 时角色高 642，光拉伸就把头顶顶到 y=19，于是垂直位移只敢用 −15。
PLAN = [
    (0,   1.00, 1.00),   # 0 站立（与静态本体逐像素一致）      头顶 61
    (5,   1.07, 0.93),   # 1 下压蓄力                        头顶 108
    (-9,  0.96, 1.06),   # 2 起跳拔高                        头顶 16
    (-15, 0.95, 1.07),   # 3 最高点                          头顶 4
    (-21, 0.97, 1.03),   # 4 滞空                            头顶 22
    (-13, 1.00, 1.00),   # 5 下落                            头顶 48
    (3,   1.06, 0.94),   # 6 落地压扁                        头顶 100
    (0,   1.00, 1.00),   # 7 回正                            头顶 61
]

# 角色离画布边缘至少要留这么多像素。贴着边（0）几乎一定是被裁了，
# 和 tools/check_anim_assets.py 里的 MIN_EDGE_MARGIN_PX 是同一个判据。
MIN_EDGE_MARGIN = 2


def place(char: Image.Image, anchor_x: float, foot_y: int, dy: int,
          sx: float, sy: float) -> Image.Image:
    """把角色按「锚点固定 + 垂直位移 + 绕锚点缩放」摆进一格规范画布。

    关键：先裁掉透明边拿到角色本体，缩放后按**本体**的宽高来摆，
    而不是按画布缩放 —— 本体并不在画布正中，按画布算会横向漂。
    """
    w = max(1, round(char.width * sx))
    h = max(1, round(char.height * sy))
    scaled = char.resize((w, h), Image.LANCZOS)

    frame = Image.new("RGBA", (CELL, CELL), (0, 0, 0, 0))
    px = round(anchor_x - w / 2.0)
    py = round(foot_y - (h - 1) + dy)
    frame.alpha_composite(scaled, (px, py))
    return frame


def assert_headroom(frames: list, title: str = "帧") -> None:
    """逐帧检查角色有没有贴到（甚至越过）画布边缘。

    生成端自己也过一遍这道关，是因为"被裁"这件事**肉眼极难发现**：
    少掉的往往只是头顶十几像素，缩略图里看着一切正常，
    等程序里播起来才发现角色每次跳到最高点就少一截。宁可在这里当场报错。

    判据和 tools/check_anim_assets.py 的 MIN_EDGE_MARGIN_PX 保持一致。
    """
    worst = None
    for i, f in enumerate(frames):
        bb = f.getchannel("A").point(lambda v: 255 if v > 8 else 0).getbbox()
        if bb is None:
            raise SystemExit(f"{title} {i} 整帧透明 —— 漏画了？")

        left, top, right, bottom = bb[0], bb[1], bb[2] - 1, bb[3] - 1
        margin = min(left, top, CELL - 1 - right, CELL - 1 - bottom)
        worst = margin if worst is None else min(worst, margin)

        print(f"  {title} {i}：角色 {right - left + 1}×{bottom - top + 1}，"
              f"离边最近 {margin}px"
              f"（左 {left} / 上 {top} / 右 {CELL - 1 - right} / 下 {CELL - 1 - bottom}）")

    if worst is not None and worst < MIN_EDGE_MARGIN:
        raise SystemExit(
            f"\n角色离画布边缘只剩 {worst}px（至少要 {MIN_EDGE_MARGIN}px）—— 会被裁。\n"
            f"多半是 PLAN 里的上移量太大：纵向拉伸本身就会把头顶往上顶，\n"
            f"   头顶 y = 661 − round({CHAR_H} × 纵向缩放) + 垂直位移\n"
            f"这一串算出来必须 ≥ {MIN_EDGE_MARGIN}。改完 PLAN 再跑一次。")


def main() -> int:
    out = pathlib.Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / "outputs" / "样例-点击弹跳"
    spec_dir = ROOT / "outputs" / "动画素材规格"
    out.mkdir(parents=True, exist_ok=True)
    spec_dir.mkdir(parents=True, exist_ok=True)

    base = Image.open(SRC).convert("RGBA")
    bb = base.getbbox()                       # 角色边界（left, top, right, bottom 开区间）
    char = base.crop(bb)

    # 角色等高缩放：本体是 565×600，目标高 600 → 尺寸不变
    scale = CHAR_H / char.height
    char = char.resize((round(char.width * scale), CHAR_H), Image.LANCZOS)

    margins = (ANCHOR_X - char.width / 2, FOOT_Y - char.height + 1,
               CELL - (ANCHOR_X + char.width / 2), CELL - 1 - FOOT_Y)
    print(f"源图 {base.size}，角色边界 {bb} → 本体 {char.width}×{char.height}")
    print(f"规范画布 {CELL}×{CELL}  锚点 x={ANCHOR_X} 脚底 y={FOOT_Y}")
    print(f"角色四周余量：左 {margins[0]:.0f} / 上 {margins[1]:.0f} / "
          f"右 {margins[2]:.0f} / 下 {margins[3]:.0f}")

    # ---- 静态本体：同一规范画布下的 whale.png ----
    base_frame = place(char, ANCHOR_X, FOOT_Y, 0, 1.0, 1.0)
    base_frame.save(out / "whale.png", optimize=True)

    # ---- 逐帧 ----
    frames = [place(char, ANCHOR_X, FOOT_Y, dy, sx, sy) for dy, sx, sy in PLAN]

    # 先验再存：被裁的素材一旦写出去，后面所有环节（预览图、对齐图、交付包）
    # 都会带着这个缺陷，而且肉眼看不太出来。
    print("静态本体余量检查：")
    assert_headroom([base_frame], "本体")
    print("逐帧余量检查：")
    assert_headroom(frames)

    sheet = Image.new("RGBA", (CELL * COLS, CELL * ROWS), (0, 0, 0, 0))
    for i, f in enumerate(frames):
        sheet.alpha_composite(f, ((i % COLS) * CELL, (i // COLS) * CELL))
    sheet_path = out / "sheet.png"
    sheet.save(sheet_path, optimize=True)

    manifest = {
        "name": "clickBounce",
        "version": 1,
        "type": "sheet",
        "sheet": "sheet.png",
        "frameWidth": CELL,
        "frameHeight": CELL,
        "cols": COLS,
        "rows": ROWS,
        "count": len(frames),
        "fps": FPS,
        "loop": False,
        "anchor": {"x": ANCHOR_X, "y": FOOT_Y},
        "characterHeight": CHAR_H,
        "returnTo": "base",
        "sound": "",
    }
    (out / "sheet.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")

    print(f"精灵图 {sheet.size}  {sheet_path.stat().st_size / 1048576:.2f} MB")
    print(f"帧表 {len(frames)} 帧 @ {FPS} fps → {out / 'sheet.json'}")
    print(f"配套本体 whale.png → {out / 'whale.png'}")

    # ---- 预览条 ----
    sc, pad, label_h, bg = 200, 12, 24, (29, 32, 38, 255)
    pre = Image.new("RGBA", (len(frames) * sc + (len(frames) + 1) * pad,
                             sc + 2 * pad + label_h), bg)
    dr = ImageDraw.Draw(pre)
    for i, f in enumerate(frames):
        x = pad + i * (sc + pad)
        pre.alpha_composite(f.resize((sc, sc), Image.LANCZOS), (x, pad))
        dr.rectangle([x, pad, x + sc - 1, pad + sc - 1], outline=(70, 76, 86, 255))
        dr.text((x + 2, pad + sc + 6), f"frame {i}", fill=(190, 196, 205))
    pre_path = spec_dir / "样例-点击弹跳-8帧.png"
    pre.save(pre_path)
    print(f"预览条 → {pre_path}")

    # ---- 对齐校验图：静态本体（25% 透明）叠在最高帧上 ----
    ov = Image.new("RGBA", (CELL, CELL), (40, 44, 52, 255))
    ov.alpha_composite(frames[3], (0, 0))
    ghost = base_frame.copy()
    ghost.putalpha(ghost.getchannel("A").point(lambda v: v // 4))
    ov.alpha_composite(ghost, (0, 0))
    ov_path = spec_dir / "对齐校验-本体与最高帧叠加.png"
    ov.save(ov_path)
    print(f"对齐校验图 → {ov_path}（本体 25% 透明；只应有垂直位移，横向不得错位）")

    # ---- 「表情类」示意：轮廓不变，可直接用原画布 ----
    make_blink_sample(char, spec_dir / "样例-眨眼示意-4帧.png", spec_dir)
    return 0


def make_blink_sample(char: Image.Image, preview: pathlib.Path, spec_dir: pathlib.Path) -> None:
    """轮廓完全不变的表情类动作示例（占位示意，不是真表情素材）。

    这类动作的 alpha 边界逐帧一致 → 零位移 → **不需要任何余量**，
    可以直接用现有 610×610 画布，本体也不用动。
    """
    out = ROOT / "outputs" / "样例-眨眼示意"
    out.mkdir(parents=True, exist_ok=True)

    # 站立 → 眼睑压下 → 闭眼 → 睁开
    LIDS = [0.0, 0.45, 1.0, 0.35]
    eye_y = round(FOOT_Y - CHAR_H * 0.62)     # 头部大致位置（占位示意）
    eye_w = round(char.width * 0.13)

    frames = []
    for t in LIDS:
        f = place(char, ANCHOR_X, FOOT_Y, 0, 1.0, 1.0)
        if t > 0:
            d = ImageDraw.Draw(f)
            lid_h = max(2, round(18 * t))
            for sign in (-1, 1):
                cx = round(ANCHOR_X + sign * char.width * 0.14)
                d.rounded_rectangle(
                    [cx - eye_w // 2, eye_y, cx + eye_w // 2, eye_y + lid_h],
                    radius=lid_h // 2, fill=(26, 30, 42, 255))
        frames.append(f)

    sheet = Image.new("RGBA", (CELL * 4, CELL), (0, 0, 0, 0))
    for i, f in enumerate(frames):
        sheet.alpha_composite(f, (i * CELL, 0))
    sheet.save(out / "sheet.png", optimize=True)

    (out / "sheet.json").write_text(json.dumps({
        "name": "blink", "version": 1, "type": "sheet", "sheet": "sheet.png",
        "frameWidth": CELL, "frameHeight": CELL, "cols": 4, "rows": 1,
        "count": 4, "fps": 12, "loop": False,
        "anchor": {"x": ANCHOR_X, "y": FOOT_Y}, "characterHeight": CHAR_H,
        "returnTo": "base", "sound": "",
    }, ensure_ascii=False, indent=2), encoding="utf-8")

    sc, pad, label_h, bg = 200, 12, 24, (29, 32, 38, 255)
    pre = Image.new("RGBA", (4 * sc + 5 * pad, sc + 2 * pad + label_h), bg)
    dr = ImageDraw.Draw(pre)
    for i, f in enumerate(frames):
        x = pad + i * (sc + pad)
        pre.alpha_composite(f.resize((sc, sc), Image.LANCZOS), (x, pad))
        dr.rectangle([x, pad, x + sc - 1, pad + sc - 1], outline=(70, 76, 86, 255))
        dr.text((x + 2, pad + sc + 6), f"frame {i}", fill=(190, 196, 205))
    pre.save(preview)

    print(f"眨眼示意（轮廓不变）→ {out/'sheet.png'} 与 {preview}")


if __name__ == "__main__":
    raise SystemExit(main())

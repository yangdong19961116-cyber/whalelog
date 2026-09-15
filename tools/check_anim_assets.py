"""动态素材交付前的自检工具。

画师（或任何做素材的人）在把文件放进 Assets\\ 之前跑一遍，
能提前发现三类最常见的返工原因：

  1. 横向漂移 —— 每帧角色的水平中心不一致，播放时会「扭」
  2. 画布裁切 —— 某帧的内容贴到了画布边缘，说明被切掉了
  3. 帧表对不上 —— frameWidth/count 和精灵图实际尺寸矛盾

用法：
    python tools/check_anim_assets.py <素材目录或 sheet.json 路径>

示例：
    python tools/check_anim_assets.py outputs/样例-点击弹跳
    python tools/check_anim_assets.py "C:/某处/Assets/anim/clickBounce"

退出码：0 = 全部通过；1 = 有必须修的问题；2 = 用法/文件错误。

判据全部是「阈值 + 打印实测值」，不猜、不掩盖 —— 有偏差就把数字摆出来，
由做素材的人决定是改素材还是调容差。

完整规格（画布、锚点、帧表字段、动作名、排查顺序）见 docs\\动态素材规格.md。
"""

from __future__ import annotations

import json
import pathlib
import sys

from PIL import Image

# ---------------- 判定阈值（要调就改这里） ----------------

# 角色 alpha 边界框的横向中心允许的偏差。产生于缩放取整，
# ±1px 是四舍五入的正常结果；超过 2px 就是真的漂了。
MAX_CENTER_DRIFT_PX = 2

# 内容距离画布边缘的安全余量。贴着边（0px）几乎一定是被裁了。
MIN_EDGE_MARGIN_PX = 2

# alpha 低于这个值视为「透明」，用来算角色边界。
# 用 8 而不是 0：JPEG 残留噪点、缩放产生的极淡边缘都不该算进角色。
ALPHA_THRESHOLD = 8

# 每帧高度差异超过这个比例就警告（squash & stretch 是允许的，
# 但整帧高度突变通常意味着素材做错了）
MAX_HEIGHT_VARIANCE = 0.55


class Report:
    def __init__(self) -> None:
        self.failures: list[str] = []
        self.warnings: list[str] = []
        self.passes: list[str] = []

    def ok(self, msg: str) -> None:
        self.passes.append(msg)

    def fail(self, msg: str) -> None:
        self.failures.append(msg)

    def warn(self, msg: str) -> None:
        self.warnings.append(msg)


def alpha_bounds(img: Image.Image) -> tuple[int, int, int, int] | None:
    """返回角色边界 (left, top, right, bottom)，右/下为**含**端点。全透明返回 None。"""
    a = img.convert("RGBA").getchannel("A")
    bb = a.point(lambda v: 255 if v > ALPHA_THRESHOLD else 0).getbbox()
    if bb is None:
        return None
    return (bb[0], bb[1], bb[2] - 1, bb[3] - 1)


def check_manifest(manifest: dict, sheet_dir: pathlib.Path, rep: Report) -> None:
    """校验帧表本身以及它和精灵图的一致性。"""
    required = ("sheet", "frameWidth", "frameHeight", "cols", "rows", "count")
    missing = [k for k in required if k not in manifest]
    if missing:
        rep.fail(f"帧表缺字段：{'、'.join(missing)}")
        return
    rep.ok(f"帧表字段完整（{len(required)} 项必需字段都在）")

    fw, fh = int(manifest["frameWidth"]), int(manifest["frameHeight"])
    cols, rows = int(manifest["cols"]), int(manifest["rows"])
    count = int(manifest["count"])

    if fw <= 0 or fh <= 0:
        rep.fail(f"帧尺寸不合法：{fw}×{fh}")
        return
    if cols * rows < count:
        rep.fail(f"网格放不下 {count} 帧：{cols} 列 × {rows} 行 = {cols * rows}")
    else:
        rep.ok(f"网格容得下：{cols}×{rows} = {cols * rows} ≥ {count} 帧")

    sheet_path = sheet_dir / str(manifest["sheet"])
    if not sheet_path.exists():
        rep.fail(f"找不到精灵图：{sheet_path.name}")
        return

    sheet = Image.open(sheet_path)
    if sheet.mode not in ("RGBA", "LA", "P"):
        rep.fail(f"精灵图没有透明通道（模式 {sheet.mode}）—— 必须带 alpha")
    else:
        rep.ok(f"精灵图带透明通道（模式 {sheet.mode}）")

    sheet = sheet.convert("RGBA")
    expect_w, expect_h = fw * cols, fh * rows
    if sheet.size != (expect_w, expect_h):
        rep.fail(f"精灵图尺寸 {sheet.width}×{sheet.height}，"
                 f"按帧表应为 {expect_w}×{expect_h}"
                 f"（{fw}×{cols} × {fh}×{rows}）")
        return
    rep.ok(f"精灵图尺寸与帧表一致：{sheet.width}×{sheet.height}")

    if sheet.width % fw or sheet.height % fh:
        rep.fail(f"网格不能整除：{sheet.width}÷{fw}、{sheet.height}÷{fh}")
    else:
        rep.ok(f"网格可整除：{sheet.width // fw} 列 × {sheet.height // fh} 行")

    # ---------------- 逐帧几何 ----------------
    centers: list[float] = []
    heights: list[int] = []
    edge_hits: list[str] = []

    for i in range(count):
        cell = sheet.crop(((i % cols) * fw, (i // cols) * fh,
                           (i % cols + 1) * fw, (i // cols + 1) * fh))
        bb = alpha_bounds(cell)
        if bb is None:
            rep.fail(f"帧 {i} 整帧透明 —— 是不是漏画了？")
            continue

        left, top, right, bottom = bb
        cx = (left + right) / 2.0
        centers.append(cx)
        heights.append(bottom - top + 1)

        touching = []
        if left <= MIN_EDGE_MARGIN_PX - 1:
            touching.append("左")
        if top <= MIN_EDGE_MARGIN_PX - 1:
            touching.append("上")
        if right >= fw - MIN_EDGE_MARGIN_PX:
            touching.append("右")
        if bottom >= fh - MIN_EDGE_MARGIN_PX:
            touching.append("下")
        if touching:
            edge_hits.append(f"帧{i}（{'/'.join(touching)}）")

    if not centers:
        rep.fail("没有任何一帧有可见内容")
        return

    rep.ok(f"{count} 帧都有内容")

    # 横向漂移
    spread = max(centers) - min(centers)
    if spread <= MAX_CENTER_DRIFT_PX:
        rep.ok(f"横向中心一致：{min(centers):.1f} ~ {max(centers):.1f}"
               f"（漂移 {spread:.1f}px ≤ {MAX_CENTER_DRIFT_PX}px）")
    else:
        detail = "  ".join(f"帧{i}:{c:.1f}" for i, c in enumerate(centers))
        rep.fail(f"横向漂移 {spread:.1f}px 超过 {MAX_CENTER_DRIFT_PX}px —— "
                 f"播放时会左右扭。各帧中心：{detail}")

    # 锚点一致性：帧表声明了 anchor 就顺便核对一下
    anchor = manifest.get("anchor") or {}
    if isinstance(anchor, dict) and "x" in anchor:
        declared = float(anchor["x"])
        if abs((min(centers) + max(centers)) / 2 - declared) <= MAX_CENTER_DRIFT_PX:
            rep.ok(f"实测中心与帧表 anchor.x={declared:g} 吻合")
        else:
            rep.warn(f"实测中心 {sum(centers)/len(centers):.1f} 与帧表 "
                     f"anchor.x={declared:g} 差得较多，请核对")

    # 裁切
    if edge_hits:
        rep.fail(f"有 {len(edge_hits)} 帧贴到画布边缘（内容可能被裁）："
                 + "、".join(edge_hits))
    else:
        rep.ok(f"没有帧贴到画布边缘（余量 ≥ {MIN_EDGE_MARGIN_PX}px）")

    # 高度波动
    if heights:
        lo, hi = min(heights), max(heights)
        ratio = (hi - lo) / hi if hi else 0
        if ratio <= MAX_HEIGHT_VARIANCE:
            rep.ok(f"各帧角色高度在合理范围：{lo} ~ {hi}px（波动 {ratio * 100:.0f}%）")
        else:
            rep.warn(f"各帧角色高度差异较大：{lo} ~ {hi}px（波动 {ratio * 100:.0f}%）"
                     f"—— 若是刻意动画可忽略")

    # 帧率
    fps = manifest.get("fps")
    if isinstance(fps, (int, float)) and fps > 0:
        if fps <= 30:
            rep.ok(f"帧率 {fps:g} fps（建议 ≤ 30，悬浮窗尺寸小，更高是浪费）")
        else:
            rep.warn(f"帧率 {fps:g} fps 偏高，建议 ≤ 30")
    else:
        rep.warn("帧表没写 fps，会按默认值播放")


def check_static(sheet_dir: pathlib.Path, manifest: dict, rep: Report) -> None:
    """静态本体应当和帧表用同一套画布，否则切换动作时会跳。"""
    whale = sheet_dir / "whale.png"
    if not whale.exists():
        rep.warn("目录里没有 whale.png —— 建议和精灵图/帧表一起交付，"
                 "换成同一画布下的静态图，否则收起/静止时会与原形象不一致")
        return

    im = Image.open(whale)
    fw, fh = int(manifest.get("frameWidth", 0)), int(manifest.get("frameHeight", 0))
    if (im.width, im.height) != (fw, fh):
        rep.fail(f"whale.png 是 {im.width}×{im.height}，"
                 f"但帧表画布是 {fw}×{fh} —— 必须同一套画布")
        return
    rep.ok(f"静态本体与帧表同画布：{im.width}×{im.height}")

    bb = alpha_bounds(im)
    if bb is None:
        rep.fail("whale.png 整张透明，没有角色")
        return

    left, top, right, bottom = bb
    anchor = manifest.get("anchor") or {}
    if isinstance(anchor, dict) and "x" in anchor:
        cx = (left + right) / 2.0
        if abs(cx - float(anchor["x"])) <= MAX_CENTER_DRIFT_PX:
            rep.ok(f"静态本体的横向中心 {cx:.1f} 与帧表锚点一致")
        else:
            rep.fail(f"静态本体横向中心 {cx:.1f} 与帧表 anchor.x="
                     f"{float(anchor['x']):g} 不一致 —— 会出现横向跳变")


def resolve(target: pathlib.Path) -> tuple[pathlib.Path, pathlib.Path] | None:
    """接受目录或 sheet.json 路径，返回 (目录, 帧表路径)。"""
    if target.is_dir():
        candidates = sorted(target.glob("*.json"))
        if not candidates:
            print(f"目录里没有 JSON 帧表：{target}", file=sys.stderr)
            return None
        # 优先 sheet.json，否则用第一个
        manifest = next((c for c in candidates if c.name == "sheet.json"), candidates[0])
        return target, manifest
    if target.is_file() and target.suffix.lower() == ".json":
        return target.parent, target
    print(f"不认识的目标：{target}（给目录或 sheet.json）", file=sys.stderr)
    return None


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__)
        return 2

    target = pathlib.Path(sys.argv[1])
    resolved = resolve(target)
    if resolved is None:
        return 2
    sheet_dir, manifest_path = resolved

    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    except Exception as exc:
        print(f"帧表读不出来：{manifest_path}\n  {exc}", file=sys.stderr)
        return 2

    print(f"素材目录：{sheet_dir}")
    print(f"帧表：{manifest_path.name}")
    print()

    rep = Report()
    check_manifest(manifest, sheet_dir, rep)
    print()
    check_static(sheet_dir, manifest, rep)

    print()
    print("=" * 62)
    for m in rep.passes:
        print(f"  ✓ {m}")
    for m in rep.warnings:
        print(f"  ! {m}")
    for m in rep.failures:
        print(f"  ✗ {m}")
    print("=" * 62)

    if rep.failures:
        print(f"结论：{len(rep.failures)} 项必须修，{len(rep.warnings)} 项提示")
        return 1
    print(f"结论：全部通过（{len(rep.passes)} 项）"
          + (f"，{len(rep.warnings)} 项提示" if rep.warnings else ""))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

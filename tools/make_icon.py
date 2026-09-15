# -*- coding: utf-8 -*-
"""
WhaleLog 图标生成器

把 DSniang1.png（610x610 RGBA 抠图）转换成一个多尺寸 Windows .ico。

为什么不用 Pillow 的 save(format='ICO', sizes=[...])：
  它对所有尺寸都用同一张源图缩放，无法针对小尺寸单独裁切。鲸鱼娘
  是全身像 + 大量细节，缩到 16/24/32 会糊成一团，所以小尺寸要改成
  头部特写。

ICO 条目统一写成 32bpp DIB（BITMAPINFOHEADER + BGRA XOR 位图 + AND
掩码），兼容性比 PNG 内嵌条目更好 —— Vista 之后虽然支持 PNG 条目，
但部分老 API 和第三方工具读 PNG 条目会失败。
"""

import struct
import sys
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "src" / "WhaleLog" / "Resources" / "Assets" / "DSniang1.png"
OUT = ROOT / "src" / "WhaleLog" / "Resources" / "Assets" / "whalelog.ico"

# 小尺寸用头部特写，大尺寸用全身
HEAD_MAX_SIZE = 32
# 头部特写取内容区域顶部的比例
HEAD_RATIO = 0.60
# 四周留白（相对边长的比例），避免图案顶到图标边缘
PADDING_RATIO = 0.03


def load_source() -> Image.Image:
    im = Image.open(SRC)
    if im.mode != "RGBA":
        im = im.convert("RGBA")
    bbox = im.getbbox()
    if bbox:
        im = im.crop(bbox)
    return im


def head_crop(im: Image.Image) -> Image.Image:
    """裁出头部区域：内容顶部 HEAD_RATIO，水平方向居中取一个正方形。"""
    w, h = im.size
    side = int(min(w, h * HEAD_RATIO))
    # 头部大致水平居中
    left = max(0, (w - side) // 2)
    top = 0
    return im.crop((left, top, left + side, top + side))


def square_pad(im: Image.Image) -> Image.Image:
    """补成正方形（透明填充），再加一点内边距。"""
    w, h = im.size
    side = max(w, h)
    canvas = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    canvas.paste(im, ((side - w) // 2, (side - h) // 2), im)
    if PADDING_RATIO > 0:
        inner = max(1, int(side * (1 - PADDING_RATIO * 2)))
        canvas = canvas.resize((inner, inner), Image.LANCZOS)
        padded = Image.new("RGBA", (side, side), (0, 0, 0, 0))
        off = (side - inner) // 2
        padded.paste(canvas, (off, off), canvas)
        canvas = padded
    return canvas


def render(im: Image.Image, size: int) -> Image.Image:
    src = head_crop(im) if size <= HEAD_MAX_SIZE else im
    src = square_pad(src)
    return src.resize((size, size), Image.LANCZOS)


def dib_bytes(im: Image.Image) -> bytes:
    """把图像编码成 ICO 用的 32bpp DIB。"""
    w, h = im.size
    header = struct.pack(
        "<IiiHHIIiiII",
        40,          # biSize
        w,           # biWidth
        h * 2,       # biHeight —— XOR + AND 两张图叠起来的高度
        1,           # biPlanes
        32,          # biBitCount
        0,           # biCompression = BI_RGB
        0,           # biSizeImage
        0, 0,        # 像素/米
        0, 0,        # 调色板
    )

    # XOR 位图：BGRA 自下而上
    xor = im.transpose(Image.FLIP_TOP_BOTTOM).tobytes("raw", "BGRA")

    # AND 掩码：1bpp，每行补齐到 4 字节。32bpp 已带 alpha，掩码全 0 即可。
    row_bytes = ((w + 31) // 32) * 4
    and_mask = b"\x00" * (row_bytes * h)

    return header + xor + and_mask


def main() -> int:
    if not SRC.exists():
        print(f"找不到源图：{SRC}", file=sys.stderr)
        return 1

    im = load_source()
    print(f"源图裁切后尺寸：{im.size}")

    sizes = [16, 24, 32, 48, 64, 128, 256]
    images = [(s, render(im, s)) for s in sizes]

    entries = []
    offset = 6 + 16 * len(images)  # ICONDIR + ICONDIRENTRY * n
    blobs = []
    for size, img in images:
        data = dib_bytes(img)
        entries.append(
            struct.pack(
                "<BBBBHHII",
                0 if size >= 256 else size,   # 256 在 ICO 里记作 0
                0 if size >= 256 else size,
                0,                            # 调色板数
                0,                            # 保留
                1,                            # 色彩平面
                32,                           # bpp
                len(data),
                offset,
            )
        )
        blobs.append(data)
        offset += len(data)

    out = bytearray()
    out += struct.pack("<HHH", 0, 1, len(images))
    for e in entries:
        out += e
    for b in blobs:
        out += b

    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_bytes(bytes(out))
    print(f"已生成 {OUT}  ({len(out)} 字节, {len(images)} 个尺寸: {sizes})")

    # 顺手导出一张 256 预览图，README / 关于页用得上
    preview = ROOT / "src" / "WhaleLog" / "Resources" / "Assets" / "whalelog-256.png"
    render(im, 256).save(preview)
    print(f"已生成预览图 {preview}")

    # 悬浮窗收起状态专用：头部特写大图，贴在屏幕边缘当小胶囊用
    head = ROOT / "src" / "WhaleLog" / "Resources" / "Assets" / "whale-head.png"
    head_img = square_pad(head_crop(im)).resize((256, 256), Image.LANCZOS)
    head_img.save(head)
    print(f"已生成收起态头像 {head}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

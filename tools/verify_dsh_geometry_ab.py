"""A/B 验证：新几何（640x360 不裁）必须**完整包含**旧几何（384 方形裁切）的画面。

这是这次改动的核心断言。旧画布的 cell 坐标 (x, y) 对应源坐标 (x+128, y-6)
（旧滤镜 = crop=384:360:128:0 再 pad 384x384 偏移 6）。所以：

    新[x+128, y-6] == 旧[x, y]        对所有旧画面里有内容的像素

只要这条成立，就证明"以前能看到的东西现在一个都没少"；
再把新画面里落在旧裁切窗口之外的内容量打印出来 —— 那正是过去被切掉的量。

用法: python tools/verify_dsh_geometry_ab.py <迁移后的 dsh 目录> <备份的旧 dsh 目录> [动作名...]
"""
import json
import subprocess
import sys
from pathlib import Path

import numpy as np

FF = r"C:/Users/Administrator/WorkBuddy/workwork/src/WhaleLog/bin/Release/net8.0-windows/ffmpeg.exe"

NEW = Path(sys.argv[1])
OLD = Path(sys.argv[2])
ONLY = sys.argv[3:]
THR = 8

OLD_X0, OLD_TOP_PAD = 128, 6
OLD_CELL = 384
CROP_X0, CROP_X1 = 128, 511


def frame_bgra(video: Path, vf: str, w: int, h: int, index: int = 0) -> np.ndarray | None:
    """解出第 index 帧（bgra）。"""
    args = [FF, "-hide_banner", "-loglevel", "error", "-c:v", "libvpx-vp9", "-i", str(video)]
    if vf:
        args += ["-vf", vf]
    args += ["-pix_fmt", "bgra", "-f", "rawvideo", "-"]
    p = subprocess.Popen(args, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
    n = w * h * 4
    out = None
    try:
        for i in range(index + 1):
            buf = p.stdout.read(n)
            if not buf or len(buf) < n:
                break
            if i == index:
                out = np.frombuffer(buf, dtype=np.uint8).reshape(h, w, 4)
    finally:
        p.stdout.close()
        p.kill()
        p.wait()
    return out


ok_all = True
rows = []

for d in sorted(x for x in (NEW / "anim").iterdir() if x.is_dir()):
    if ONLY and d.name not in ONLY:
        continue
    old_dir = OLD / "anim" / d.name
    if not old_dir.is_dir():
        continue

    man = json.loads((d / "anim.json").read_text(encoding="utf-8"))
    old_man = json.loads((old_dir / "anim.json").read_text(encoding="utf-8"))

    new = frame_bgra(d / man["video"], man["filter"], man["frameWidth"], man["frameHeight"])
    old = frame_bgra(old_dir / old_man["video"], old_man["filter"],
                     old_man["frameWidth"], old_man["frameHeight"])
    if new is None or old is None:
        print(f"❌ {d.name}：解不出帧")
        ok_all = False
        continue

    # 旧画面里所有不透明像素，搬到新坐标去比对
    ys, xs = np.where(old[:, :, 3] > THR)
    if len(xs) == 0:
        rows.append((d.name, 0, 0, 0, True))
        continue

    ny, nx = ys - OLD_TOP_PAD, xs + OLD_X0
    inside = (ny >= 0) & (ny < new.shape[0]) & (nx >= 0) & (nx < new.shape[1])
    if not inside.all():
        print(f"❌ {d.name}：旧画面有 {int((~inside).sum())} 个像素落到新画布之外")
        ok_all = False
        continue

    a_old = old[ys, xs, 3].astype(int)
    a_new = new[ny, nx, 3].astype(int)
    # alpha 只准变大不准变小：变小说明新画面在那个位置丢了内容（缩放/错位）
    worse = (a_old - a_new)
    alpha_bad = int((worse > 2).sum())

    rgb_old = old[ys, xs, :3].astype(int)
    rgb_new = new[ny, nx, :3].astype(int)
    rgb_diff = np.abs(rgb_old - rgb_new).max(axis=1)
    rgb_bad = int((rgb_diff > 2).sum())

    # 新画面里落在旧裁切窗口之外的内容（= 过去被切掉的道具）
    ys2, xs2 = np.where(new[:, :, 3] > THR)
    outside = int(((xs2 < CROP_X0) | (xs2 > CROP_X1)).sum())
    total = len(xs2)

    good = alpha_bad == 0 and rgb_bad == 0
    ok_all &= good
    rows.append((d.name, alpha_bad, rgb_bad, outside, good))

print()
print(f"{'动作':<34}{'alpha 变小':>10}{'颜色不符':>10}{'旧裁切窗外内容':>16}  判定")
bad = 0
for name, ab, rb, out_, good in rows:
    if not good:
        bad += 1
    print(f"{name:<34}{ab:>10}{rb:>10}{out_:>16}  {'✅' if good else '❌'}")

print()
print(f"共 {len(rows)} 个动作；像素级不符 {bad} 个")
with_out = [r for r in rows if r[3] > 0]
print(f"有内容落在旧裁切窗口之外的：{len(with_out)} 个"
      f"（这些动作在旧几何下显示就是被切过的）")
if with_out:
    top = sorted(with_out, key=lambda r: -r[3])[:8]
    for name, _, _, out_, _ in top:
        print(f"  {name:<34} {out_} 像素")
print()
print("结论：" + ("✅ 新画布完整包含旧画面，且把过去被切的道具收回来了"
                 if ok_all else "❌ 有动作不满足断言"))
sys.exit(0 if ok_all else 1)

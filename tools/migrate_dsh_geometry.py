"""把已产出的 dsh 素材从「方形画布 384 + crop」迁到「矩形画布 640x360 + 不裁」。

背景（2026-09-21）
------------------
用户报"新形象显示被裁"。量下来是：源画布 640x360，运行时却先 crop=384:360:128:0
再 pad 成 384x384 —— 左右各切掉 128/128，106 个动作里 25 个的道具（蝴蝶、烟花、
气球）被齐刷刷切断。

全帧扫过 25423 帧，内容并集是 x 4..631、y 0..359 —— **源画布已被用满**，
所以正确做法只有一个：画布就用源画布全幅，恒等映射，什么都不裁。

为什么不需要重新编码视频
------------------------
`video.webm` 当初就是**原样搬运**的源文件（见 emit_one_video），crop 只发生在
运行时解码那一侧。所以改几何 = 改 anim.json 里的 filter/尺寸，视频一个字节都不用动。

为什么复用 tools/convert_dsh_webm.py
------------------------------------
"源坐标 ↔ 画布坐标"的换算只允许有一份实现。这里 import 那个脚本的 Geometry
来算滤镜串，而不是自己再拼一个 —— 两份实现迟早会分叉，而分叉的表现是
"离线校验通过、运行时画面错位"，最难查。

用法：
    python tools/migrate_dsh_geometry.py <assets/characters/dsh 目录>
"""
import json
import pathlib
import sys

# 几何换算只允许有一处实现，所以直接 import 转换工具里的 Geometry
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

from convert_dsh_webm import (  # noqa: E402
    DEFAULT_BODY_BOX, Geometry, _write_attribution, _write_index,
    emit_one_video, extract_still, probe_source,
)

#: 目录名前缀 → 角色。和 ROLE_OF_FOLDER 的产物一一对应。
#: archive / cheer 的名字由 convert_dsh_webm.ACTION_NAME_OVERRIDES 定，
#: 这里只负责认出来（前者=归档后的清点，后者=完工庆祝，两个独立角色）。
ROLE_OF_PREFIX = {
    "idle": "idle",
    "clickbounce": "clickBounce",
    "drag": "drag",
    "alert": "alert",
    "ambient": "ambient",
    "move": "move",
    "turn": "turn",
    "archive": "archive",
    "cheer": "cheer",
}


def role_of(action: str) -> str:
    head = action.split("-", 1)[0].lower()
    return ROLE_OF_PREFIX.get(head, "ambient")


def main() -> int:
    pack = pathlib.Path(sys.argv[1]).resolve()
    anim = pack / "anim"
    if not anim.is_dir():
        print(f"❌ 没有 anim 目录：{anim}", file=sys.stderr)
        return 1

    dirs = sorted(d for d in anim.iterdir() if d.is_dir())
    print(f"形象包：{pack}")
    print(f"动作：{len(dirs)} 个\n")

    # 源画布尺寸从第一个动作里读 —— 路线是"画布 = 源画布全幅"，所以必须有这个数
    first_json = next((d / "anim.json" for d in dirs if (d / "anim.json").is_file()), None)
    if first_json is None:
        print("❌ 一个 anim.json 都没有", file=sys.stderr)
        return 1

    first_video = json.loads(first_json.read_text(encoding="utf-8")).get("video", "video.webm")
    info0 = probe_source(first_json.parent / first_video)
    src_w, src_h = info0["width"], info0["height"]

    geom = Geometry(
        cell=0, char_h=0, body_box=DEFAULT_BODY_BOX,
        src_w=src_w, src_h=src_h, top_pad=0,
        canvas=(src_w, src_h),
    )
    print(f"源画布 {src_w}×{src_h}")
    print(geom.describe())
    print(f"滤镜：{geom.ffmpeg_filter()}\n")

    done, skipped, failed = [], [], []
    for d in dirs:
        mf = d / "anim.json"
        if not mf.is_file():
            skipped.append(d.name)
            continue

        old = json.loads(mf.read_text(encoding="utf-8"))
        vid = d / old.get("video", "video.webm")
        if not vid.is_file():
            failed.append((d.name, f"缺 {vid.name}"))
            continue

        it = {"src": vid, "action": d.name, "role": role_of(d.name)}
        try:
            # 不传 force：src 和 target 是同一个文件，force 会让 copyfile 自杀
            r = emit_one_video(it, pack, geom, src_w, src_h, force=False)
        except Exception as e:  # noqa: BLE001
            failed.append((d.name, str(e)))
            continue

        # 保留原来的 source 出处（emit_one_video 只会写成 video.webm 这个名字）
        new = json.loads(mf.read_text(encoding="utf-8"))
        new["source"] = old.get("source", new.get("source", ""))
        mf.write_text(json.dumps(new, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

        r["role"] = it["role"]
        done.append(r)

    # 静态本体：走**同一个滤镜**从待机动作首帧取，保证「静止 → 播第一帧」不跳
    idle = next((d for d in dirs if d.name.lower().startswith("idle-")), None)
    if idle is None:
        print("⚠️  没找到 idle-* 动作，静态本体没重新生成", file=sys.stderr)
        still = None
    else:
        still_video = idle / json.loads((idle / "anim.json").read_text(encoding="utf-8")).get(
            "video", "video.webm")
        size = extract_still(still_video, geom, pack / "whale.png")
        still = size
        print(f"静态本体 whale.png {size[0]}×{size[1]}，取自 {idle.name}")

    _write_attribution(pack)
    _write_index(pack, geom, done, emit="video")

    print()
    print(f"完成：{len(done)} 个动作已迁移，跳过 {len(skipped)}，失败 {len(failed)}")
    for name, err in failed[:10]:
        print(f"  ❌ {name}：{err}", file=sys.stderr)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())

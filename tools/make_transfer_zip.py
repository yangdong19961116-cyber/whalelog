# -*- coding: utf-8 -*-
"""
把仓库打成一个「换台设备继续干」的交接包。

带上：源码、工具、素材、样例、文档、项目记忆，以及 `.git`（**保留完整提交历史**）。
排除：`bin/obj/build` 这类纯产物（新机器上重新构建即可）、探针临时目录、日志。
解压后顶层目录是 `鲸记WhaleLog/`，换设备继续开发照着 `新设备继续指令.md` 走。

为什么不用 `git archive`：那玩意儿会把 `.git` 丢掉，历史就没了。
为什么要校验：打包脚本最容易静默漏文件，所以跑完拿 `git ls-files` 对一遍
（注意中文文件名要加 `-c core.quotepath=false` 才比得上）。

用法（在仓库根目录；下面的反斜杠是给 cmd 看的，bash 里写 / 就行）：
    python tools/make_transfer_zip.py                       # 输出到 E:\\jingji\\鲸记 WhaleLog 项目交接包 <今天>.zip
    python tools/make_transfer_zip.py . <输出目录或 .zip>     # 想放别处再自己指定
"""
import os
import sys
import zipfile
from datetime import date
from pathlib import Path

# 目录名精确匹配：整个目录都跳过
SKIP_DIRS = {"bin", "obj", ".vs", ".idea", ".vscode", "build", "TestResults", "Publish", "publish"}

# 目录名模式：前缀匹配（.tmp-* / tmp）
SKIP_PREFIXES = (".tmp-",)

# 文件名后缀：跳过
SKIP_SUFFIXES = (".log", ".tmp", ".user", ".suo", ".pdb")

# 产物统一放这儿（2026-09-17 定的规矩）：E:\jingji，与 make_portable_zip.py 同一个目录。
ARTIFACT_DIR = Path(r"E:\jingji")


def artifact_dir() -> Path:
    """产物目录，建不出来就退回桌面并说明原因。

    换台没有 E 盘的机器时，这条退路才是能继续干活的那条 ——
    别为了守一个目录约定把打包整个卡死。
    """
    try:
        ARTIFACT_DIR.mkdir(parents=True, exist_ok=True)
        return ARTIFACT_DIR
    except OSError as e:
        fallback = Path.home() / "Desktop"
        fallback = fallback if fallback.exists() else Path.home()
        print(f"提示：建不出 {ARTIFACT_DIR}（{e}），这次改放 {fallback}", file=sys.stderr)
        return fallback


def should_skip_dir(rel: Path, name: str) -> bool:
    if name in SKIP_DIRS:
        return True
    if name.startswith(SKIP_PREFIXES):
        return True
    return False


def main() -> int:
    args = sys.argv[1:]

    # 源目录默认就是仓库根；输出默认落到 E:\jingji，文件名带当天日期，
    # 这样"最新那份"永远是文件名里日期最大的那个，不用翻时间戳。
    src = Path(args[0]).resolve() if args else Path(__file__).resolve().parents[1]
    if len(args) >= 2:
        dst = Path(args[1]).resolve()
    else:
        dst = artifact_dir() / f"鲸记 WhaleLog 项目交接包 {date.today():%Y-%m-%d}.zip"

    if not src.is_dir():
        print(f"源目录不存在：{src}")
        return 1

    # 解压后顶着这个目录名，不用跟仓库所在目录同名
    root_name = "鲸记WhaleLog"

    files: list[tuple[Path, str]] = []
    for dirpath, dirnames, filenames in os.walk(src):
        here = Path(dirpath)
        rel_here = here.relative_to(src)

        # 原地裁剪 dirnames，os.walk 就不会往里走
        dirnames[:] = [d for d in dirnames if not should_skip_dir(rel_here / d, d)]

        for fn in filenames:
            if fn.endswith(SKIP_SUFFIXES):
                continue
            p = here / fn
            arc = f"{root_name}/{(rel_here / fn).as_posix()}"
            files.append((p, arc))

    dst.parent.mkdir(parents=True, exist_ok=True)
    total_raw = 0
    with zipfile.ZipFile(dst, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for p, arc in sorted(files, key=lambda t: t[1]):
            total_raw += p.stat().st_size
            z.write(p, arc)

    size = dst.stat().st_size
    print(f"源目录   {src}")
    print(f"输出     {dst}")
    print(f"文件数   {len(files)}")
    print(f"原始大小 {total_raw / 1024 / 1024:.1f} MB  →  压缩后 {size / 1024 / 1024:.1f} MB")

    # 让人一眼确认关键的几样都在里面
    names = {a for _, a in files}
    must = [
        f"{root_name}/.gitignore",
        f"{root_name}/HANDOFF-鲸记WhaleLog.md",
        f"{root_name}/WhaleLog.sln",
        f"{root_name}/.workbuddy/memory/MEMORY.md",
        f"{root_name}/docs/动态素材规格.md",
        f"{root_name}/src/WhaleLog/WhaleLog.csproj",
        f"{root_name}/src/WhaleLog/Views/SpritePlayer.cs",
        f"{root_name}/tools/selftest/Program.cs",
    ]
    print()
    for m in must:
        print(("OK  " if m in names else "MISS") + "  " + m)
    has_git = any(n.startswith(f"{root_name}/.git/") for n in names)
    print(("OK  " if has_git else "MISS") + f"  {root_name}/.git/  （提交历史）")

    missing = [m for m in must if m not in names]
    if missing or not has_git:
        print("\n缺东西，别急着用这个包。")
        return 1
    print("\n包检查通过。")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

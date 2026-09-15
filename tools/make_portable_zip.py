"""把鲸记打包成"解压就能用"的自包含压缩包。

正常情况下这件事应该由 `dotnet publish -r win-x86 --self-contained` 完成，
但那需要 NuGet 上的运行时包（Microsoft.NETCore.App.Runtime.* /
Microsoft.WindowsDesktop.App.Runtime.*）。这台开发机只装了 SDK + 运行时，
没装运行时包，而且 restore 在受限会话里不可用 —— 所以这里手工拼：

    自包含发布 = 应用本体 + hostfxr.dll + 两个共享框架的全部文件
                 + 一份声明"框架就在程序目录里"的 runtimeconfig.json
                 + 一份列出全部框架程序集的 deps.json

其中 deps.json 是关键：宿主靠它构造程序集清单（TPA），只复制文件而清单不对，
启动时会报 "Could not resolve CoreCLR path"。托管 / 原生 DLL 的区分看 PE 头的
第 14 个数据目录（CLR Runtime Header）是否非 0。

用法：
    python tools/make_portable_zip.py                 # 输出到桌面
    python tools/make_portable_zip.py <输出目录或 .zip 路径>
"""

from __future__ import annotations

import json
import os
import pathlib
import shutil
import struct
import subprocess
import sys
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[1]
APP_NAME = "鲸记 WhaleLog"
ENTRY = "WhaleLog.exe"

# 两个框架里同名的程序集，WindowsDesktop 的版本优先（和 FrameworkList 的优先级一致）。
# 不处理的话 WindowsBase.dll 会被 NETCore.App 里那个空壳覆盖，WPF 直接起不来。
WPF_OVERRIDES = ("WindowsBase.dll", "System.Drawing.dll", "Microsoft.VisualBasic.dll")

FRAMEWORKS = ("Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App")


# ----------------------------------------------------------------------
# PE 读取
# ----------------------------------------------------------------------

def pe_machine(path: pathlib.Path) -> int:
    """PE 头里的机器类型：0x14C = x86，0x8664 = x64。"""
    with path.open("rb") as f:
        head = f.read(0x400)
    pe = struct.unpack_from("<I", head, 0x3C)[0]
    if head[pe:pe + 4] != b"PE\0\0":
        raise ValueError(f"{path.name} 不是有效的 PE 文件")
    return struct.unpack_from("<H", head, pe + 4)[0]


def is_managed(path: pathlib.Path) -> bool:
    """数据目录第 14 项（CLR Runtime Header）非 0 → 是托管程序集。"""
    try:
        with path.open("rb") as f:
            head = f.read(0x400)
        pe = struct.unpack_from("<I", head, 0x3C)[0]
        magic = struct.unpack_from("<H", head, pe + 24)[0]
        dd = pe + 24 + (96 if magic == 0x10B else 112)
        rva, size = struct.unpack_from("<II", head, dd + 14 * 8)
        return rva != 0 and size != 0
    except Exception:
        return False


# ----------------------------------------------------------------------
# 找运行时
# ----------------------------------------------------------------------

def find_runtime(arch: str) -> tuple[pathlib.Path, str]:
    """返回 (dotnet 根目录, 版本号)。arch 取 x86 / x64，必须和 apphost 一致。"""
    candidates = [
        pathlib.Path(r"C:\Program Files\dotnet"),
        pathlib.Path(r"C:\Program Files (x86)\dotnet"),
    ] if arch == "x64" else [
        pathlib.Path(r"C:\Program Files (x86)\dotnet"),
        pathlib.Path(r"C:\Program Files\dotnet"),
    ]

    want_machine = 0x8664 if arch == "x64" else 0x14C

    for base in candidates:
        # 用 coreclr.dll 的架构确认这个目录就是目标架构的运行时
        ver_dir = base / "shared" / "Microsoft.NETCore.App"
        if not ver_dir.exists():
            continue
        for version in sorted((p.name for p in ver_dir.iterdir() if p.is_dir()), reverse=True):
            coreclr = ver_dir / version / "coreclr.dll"
            if not coreclr.exists() or pe_machine(coreclr) != want_machine:
                continue
            if all((base / "shared" / fw / version).exists() for fw in FRAMEWORKS):
                return base, version

    raise SystemExit(
        f"找不到 {arch} 架构的 .NET 运行时（需要 Microsoft.NETCore.App 和 "
        f"Microsoft.WindowsDesktop.App 同在，且架构与 apphost 一致）"
    )


# ----------------------------------------------------------------------
# 生成清单
# ----------------------------------------------------------------------

def runtimeconfig(version: str) -> str:
    """自包含的运行时配置：includedFrameworks 表示框架就在程序目录里。"""
    return json.dumps({
        "runtimeOptions": {
            "tfm": "net8.0",
            "includedFrameworks": [
                {"name": "Microsoft.NETCore.App", "version": version},
                {"name": "Microsoft.WindowsDesktop.App", "version": version},
            ],
            "configProperties": {
                "System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization": False,
            },
        }
    }, indent=2, ensure_ascii=False)


def deps_json(base: pathlib.Path, version: str, app_name: str) -> tuple[str, int, int]:
    """按框架目录里的实际文件生成 deps.json，返回 (json, 托管数, 原生数)。"""
    targets: dict[str, dict] = {
        f"{app_name}/1.0.0": {"runtime": {f"{app_name}.dll": {}}},
    }
    libraries: dict[str, dict] = {
        f"{app_name}/1.0.0": {"type": "project", "serviceable": False, "sha512": ""},
    }

    managed_total = native_total = 0
    seen: set[str] = set()

    # 顺序很重要：WindowsDesktop 在后面，同名的程序集覆盖 NETCore 的版本
    for fw in FRAMEWORKS:
        directory = base / "shared" / fw / version
        managed, native = {}, {}

        for f in sorted(directory.glob("*.dll")):
            if fw != FRAMEWORKS[-1] and f.name in WPF_OVERRIDES:
                continue                      # 让后面那个框架提供
            if f.name in seen:
                continue
            seen.add(f.name)
            (managed if is_managed(f) else native)[f.name] = {}

        entry: dict = {"runtime": managed}
        if native:
            entry["native"] = native

        targets[f"{fw}/{version}"] = entry
        libraries[f"{fw}/{version}"] = {
            "type": "package",
            "serviceable": True,
            "sha512": "",
            "path": f"{fw.lower()}/{version}",
            "hashPath": "",
        }
        managed_total += len(managed)
        native_total += len(native)

    doc = {
        "runtimeTarget": {"name": ".NETCoreApp,Version=v8.0", "signature": ""},
        "compilationOptions": {},
        "targets": {".NETCoreApp,Version=v8.0": targets},
        "libraries": libraries,
    }
    return json.dumps(doc, indent=2), managed_total, native_total


# ----------------------------------------------------------------------
# 说明文件
# ----------------------------------------------------------------------

README = """鲸记 WhaleLog · 桌面工作记录台
==================================

怎么用
------
解压到任意目录，双击 WhaleLog.exe 即可。不需要安装 .NET —— 运行时已经
打包在里面了（32 位版，32 / 64 位 Windows 10、11 都能跑）。

首次启动会同时出现主面板和桌面上的小鲸鱼，然后常驻系统托盘。

想放到桌面/开始菜单：托盘图标上点右键 →「创建桌面快捷方式」，
或者打开主面板 →「设置」→「创建桌面快捷方式」。

日常操作
--------
  记一笔           全局快捷键 Ctrl+Alt+N（可在设置里改）
  翻看历史         点日期栏展开整月日历，或用底部的「跳到」框（9/23、昨天 都认）
  归档文件         把文件拖进主面板，自动分类、记录位置、标记版本
  日报 / 周报      今日记录页右下角，或托盘右键菜单

换成你自己的形象
----------------
程序旁边有个 Assets 目录，里面放着当前的小鲸鱼图和图标。把同名文件替换掉，
再到托盘图标上点右键 →「重新加载素材」，界面立刻换过来，不用重新编译。
每个文件要什么规格，见 Assets\\素材说明.txt。删掉某个文件就恢复内置默认。

让小鲸鱼动起来
--------------
Assets\\anim\\ 里可以放精灵图动画（单击弹跳 / 闲置眨眼 / 到点提醒）。一个动作一个子目录，
放完点「重新加载素材」就生效。画布、锚点、帧表的完整规格见程序目录下的
《动态素材规格.md》，里面还有"看不到效果时按什么顺序查"的一张表。

不想让它动：托盘右键 →「小鲸鱼动画」，素材文件不用删。

数据存在哪儿
------------
默认在 %AppData%\\WhaleLog（笔记 Markdown、文件索引、设置、日志都在这里）。

想放 U 盘上便携使用：在 WhaleLog.exe 旁边新建一个空文件，命名成
portable.txt，数据就会改存到它旁边的 data\\ 目录里。

重点：不会动你的文件
--------------------
归档只读取文件名、大小、时间和内容指纹，**绝不移动、重命名或删除原文件**。
「开启快照」是可选项，开了才会额外复制副本到数据目录。

出问题怎么办
------------
在命令行里跑一次：

    WhaleLog.exe --selfcheck

它会把快捷键、托盘图标、各窗口、界面状态等挨个检查一遍，结果写到
%AppData%\\WhaleLog\\logs\\selfcheck.log，把这个文件发过来就能定位。

卸载
----
删掉这个目录就卸载完了。数据在 %AppData%\\WhaleLog，要清干净需要自己删。
"""


# ----------------------------------------------------------------------
# 主流程
# ----------------------------------------------------------------------

def main() -> int:
    target = pathlib.Path(sys.argv[1]) if len(sys.argv) > 1 else None
    if target is None:
        desktop = pathlib.Path.home() / "Desktop"
        target = (desktop if desktop.exists() else pathlib.Path.home()) / f"{APP_NAME}.zip"
    elif target.is_dir():
        target = target / f"{APP_NAME}.zip"
    target = target.resolve()

    app_dir = ROOT / "src" / "WhaleLog" / "bin" / "Release" / "net8.0-windows"
    app_dll = app_dir / "WhaleLog.dll"
    app_exe = app_dir / ENTRY
    if not app_dll.exists() or not app_exe.exists():
        print(f"找不到编译产物：{app_dir}", file=sys.stderr)
        print("先跑一次 build-whalelog-offline.cmd（或 build-whalelog.cmd）。", file=sys.stderr)
        return 1

    machine = pe_machine(app_exe)
    arch = {0x14C: "x86", 0x8664: "x64"}.get(machine)
    if arch is None:
        print(f"认不出 apphost 的架构（machine=0x{machine:X}）", file=sys.stderr)
        return 1

    base, version = find_runtime(arch)
    print(f"apphost 架构：{arch}")
    print(f"运行时：{base}  {version}")

    # 铺目录
    stage_root = ROOT / "build" / "portable"
    shutil.rmtree(stage_root, ignore_errors=True)
    bundle = stage_root / APP_NAME
    bundle.mkdir(parents=True)

    for name in (ENTRY, "WhaleLog.dll"):
        shutil.copy2(app_dir / name, bundle / name)

    shutil.copy2(base / "host" / "fxr" / version / "hostfxr.dll", bundle / "hostfxr.dll")

    copied = 0
    for fw in FRAMEWORKS:
        for f in (base / "shared" / fw / version).glob("*.dll"):
            shutil.copy2(f, bundle / f.name)
            copied += 1

    (bundle / "WhaleLog.runtimeconfig.json").write_text(
        runtimeconfig(version), encoding="utf-8")

    deps, managed, native = deps_json(base, version, "WhaleLog")
    (bundle / "WhaleLog.deps.json").write_text(deps, encoding="utf-8")

    (bundle / "使用说明.txt").write_text(README, encoding="utf-8-sig")

    # 动态素材规格一起放进包：《使用说明》和 Assets\素材说明.txt 都指向它，
    # 不带上就是一个死链接 —— 而"怎么做动画"恰恰是唯一需要看文档的部分。
    spec = ROOT / "docs" / "动态素材规格.md"
    if spec.exists():
        shutil.copy2(spec, bundle / spec.name)
        print(f"已附带规格文档：{spec.name}")
    else:
        print(f"提示：没有 {spec}，包里的《使用说明》会指向一个不存在的文件", file=sys.stderr)

    # 出厂素材目录：让用户解压后立刻看到该换哪些文件，不用等首次启动。
    # 交给程序自己导出，而不是在这里重写一遍素材清单 ——
    # 否则"说明文件里写了什么"会有两份来源，改一处忘一处就开始漂移。
    assets_dir = bundle / "Assets"
    probe_data = ROOT / "build" / "asset-probe-data"     # 别让它进压缩包
    try:
        subprocess.run(
            [str(app_exe), "--export-assets", str(assets_dir), "--quiet"],
            env=dict(os.environ, WHALELOG_DATA_DIR=str(probe_data)),
            capture_output=True, text=True, timeout=180)
    finally:
        shutil.rmtree(probe_data, ignore_errors=True)

    assets = sorted(p.name for p in assets_dir.iterdir()) if assets_dir.exists() else []
    if assets:
        print(f"出厂素材目录：{len(assets)} 个文件 —— {'、'.join(assets)}")
    else:
        print("警告：素材目录没生成，这个包里不会有 Assets", file=sys.stderr)

    size = sum(f.stat().st_size for f in bundle.iterdir() if f.is_file())
    print(f"已铺 {copied} 个框架文件（托管 {managed} / 原生 {native}），共 {size / 1048576:.1f} MB")

    # 压缩
    target.parent.mkdir(parents=True, exist_ok=True)
    if target.exists():
        target.unlink()

    print(f"正在压缩 → {target.name}")
    with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as z:
        for f in sorted(bundle.rglob("*")):
            if f.is_file():
                z.write(f, f.relative_to(stage_root))

    print(f"完成：{target}")
    print(f"压缩包大小 {target.stat().st_size / 1048576:.1f} MB，内含 {len(list(bundle.rglob('*')))} 个条目")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

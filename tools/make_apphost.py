# -*- coding: utf-8 -*-
"""
离线生成 Windows 应用宿主 WhaleLog.exe（apphost）。

为什么需要它
------------
`dotnet WhaleLog.dll` 也能跑，但 dotnet.exe 是**控制台子系统**程序，
从桌面快捷方式或资源管理器启动时会弹出一个黑控制台窗口，对整个程序生命周期都不消失。
框架依赖构建又拿不到 SDK 自动生成的那个 exe（SDK 生成 apphost 需要 NuGet 还原）。

做法
----
SDK 自带的 apphost 模板里预留了两个定长占位符，本工具把它们替换成实际路径，
再把 PE 头里的子系统从"控制台"改成"GUI"，最后把我们的图标写进资源段 ——
这三步正是 `Microsoft.NET.HostModel.AppHost.CreateAppHost` 在构建时做的事，
只是这里手工完成。

产物是**框架依赖**的 exe（需要目标机器装 .NET 8 Desktop Runtime）。
正式发布请用 build-whalelog.cmd，那条路会产出带图标的自包含单文件。
"""

import ctypes
import struct
import sys
from ctypes import wintypes
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
EXE_NAME = "WhaleLog"
DLL_NAME = "WhaleLog.dll"

# apphost 模板里的占位符。同一个串在二进制里出现多次（.rdata 里还有一处
# 把两个占位符连在一起写的 64 字节版本），必须**全部**替换 ——
# 只替换第一处的话，宿主仍然会读到没被替换的那份，报
# "The application to execute does not exist: <占位符原样>"。
PLACEHOLDER_BINARY_PATH = b"74e592c2fa383d4a3960714caef0c4f2"
PLACEHOLDER_APP_PATH = b"c3ab8ff13720e8ad9047dd39466b3c89"
PLACEHOLDER_COMBINED = PLACEHOLDER_APP_PATH + PLACEHOLDER_BINARY_PATH

IMAGE_SUBSYSTEM_WINDOWS_GUI = 2
IMAGE_SUBSYSTEM_WINDOWS_CUI = 3

RT_ICON = 3
RT_GROUP_ICON = 14


def detect_rid() -> str:
    """按本机实际安装的 .NET 运行时架构挑 apphost —— 拿错架构会直接启动失败。"""
    if Path(r"C:\Program Files\dotnet\host\fxr").exists():
        return "win-x64"
    if Path(r"C:\Program Files (x86)\dotnet\host\fxr").exists():
        return "win-x86"
    return "win-x64"


def find_apphost(rid: str) -> Path:
    roots = [Path(r"C:\Program Files\dotnet\packs"), Path(r"C:\Program Files (x86)\dotnet\packs")]
    for root in roots:
        found = sorted(
            root.glob(f"Microsoft.NETCore.App.Host.{rid}/*/runtimes/{rid}/native/apphost.exe"),
            reverse=True,
        )
        for c in found:
            if c.exists():
                return c

    for sdk_root in (r"C:\Program Files\dotnet\sdk", r"C:\Program Files (x86)\dotnet\sdk"):
        for sdk in sorted(Path(sdk_root).glob("*/AppHostTemplate/apphost.exe"), reverse=True):
            if sdk.exists():
                return sdk

    raise SystemExit(f"找不到 {rid} 的 apphost.exe 模板，请确认已安装 .NET SDK")


def pe_subsystem_offset(data: bytes) -> int:
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    if data[pe : pe + 4] != b"PE\0\0":
        raise SystemExit("不是有效的 PE 文件")
    # COFF 头 20 字节 + 可选头里 Subsystem 的偏移是 68
    return pe + 4 + 20 + 68


def patch_placeholders(data: bytearray, value: str) -> int:
    """
    把所有占位符副本替换成实际文件名，剩余空间补 0（宿主按 C 字符串读取）。

    槽位长度 = 占位符本身 + 紧随其后的连续 0。补 0 只会把本来就是 0 的填充位
    再写成 0，不会误伤相邻数据。
    """
    raw = value.encode("utf-8")
    total = 0

    # 先替换 64 字节的连写版本，否则它被 32 字节的替换打断后就再也匹配不到了
    for placeholder in (PLACEHOLDER_COMBINED, PLACEHOLDER_APP_PATH, PLACEHOLDER_BINARY_PATH):
        cursor = 0
        while True:
            off = data.find(placeholder, cursor)
            if off < 0:
                break

            end = off + len(placeholder)
            while end < len(data) and data[end] == 0:
                end += 1
            slot = end - off

            if len(raw) + 1 > slot:
                raise SystemExit(f"槽位只有 {slot} 字节，放不下 {value!r}")

            data[off : off + slot] = raw + b"\0" * (slot - len(raw))
            total += 1
            cursor = off + len(raw) + 1

    if total == 0:
        raise SystemExit("apphost 模板里一个占位符都没找到，模板版本可能不兼容")

    return total


def set_subsystem(data: bytearray, subsystem: int) -> int:
    off = pe_subsystem_offset(bytes(data))
    old = struct.unpack_from("<H", data, off)[0]
    struct.pack_into("<H", data, off, subsystem)
    return old


# ----------------------------------------------------------------------
# 图标注入
# ----------------------------------------------------------------------

def parse_ico(ico_bytes: bytes):
    """解析 ICO，返回 [(id, 目录项字段, 图像数据)]。"""
    reserved, ico_type, count = struct.unpack_from("<HHH", ico_bytes, 0)
    if reserved != 0 or ico_type != 1:
        raise SystemExit("不是有效的 ICO 文件")

    images = []
    for i in range(count):
        base = 6 + i * 16
        w, h, colors, _res, planes, bpp, size, offset = struct.unpack_from(
            "<BBBBHHII", ico_bytes, base
        )
        blob = ico_bytes[offset : offset + size]
        if len(blob) != size:
            raise SystemExit(f"ICO 第 {i} 张图数据不完整")
        images.append(((w, h, colors, planes, bpp, size), blob))
    return images


def build_group_icon(images) -> bytes:
    """构造 RT_GROUP_ICON 资源内容：GRPICONDIR + GRPICONDIRENTRY[]。"""
    out = bytearray()
    out += struct.pack("<HHH", 0, 1, len(images))
    for index, (meta, blob) in enumerate(images, start=1):
        w, h, colors, planes, bpp, size = meta
        out += struct.pack("<BBBBHHIH", w, h, colors, 0, planes, bpp, size, index)
    return bytes(out)


def inject_icon(exe_bytes: bytearray, ico_bytes: bytes, target_dir: Path) -> int:
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

    BeginUpdateResourceW = kernel32.BeginUpdateResourceW
    BeginUpdateResourceW.argtypes = [wintypes.LPCWSTR, wintypes.BOOL]
    BeginUpdateResourceW.restype = wintypes.HANDLE

    UpdateResourceW = kernel32.UpdateResourceW
    UpdateResourceW.argtypes = [
        wintypes.HANDLE, wintypes.LPCWSTR, wintypes.LPCWSTR,
        wintypes.WORD, ctypes.c_void_p, wintypes.DWORD,
    ]
    UpdateResourceW.restype = wintypes.BOOL

    EndUpdateResourceW = kernel32.EndUpdateResourceW
    EndUpdateResourceW.argtypes = [wintypes.HANDLE, wintypes.BOOL]
    EndUpdateResourceW.restype = wintypes.BOOL

    # 先落盘一个临时文件（BeginUpdateResource 是按路径操作的）
    tmp = target_dir / f".{EXE_NAME}.icontmp.exe"
    tmp.write_bytes(exe_bytes)
    try:
        images = parse_ico(ico_bytes)
        handle = BeginUpdateResourceW(str(tmp), False)
        if not handle:
            raise SystemExit(f"BeginUpdateResource 失败：{ctypes.get_last_error()}")

        try:
            # 每张图各占一个 RT_ICON，ID 从 1 开始
            for index, (_meta, blob) in enumerate(images, start=1):
                buffer = ctypes.create_string_buffer(blob, len(blob))
                ok = UpdateResourceW(
                    handle, wintypes.LPCWSTR(RT_ICON), wintypes.LPCWSTR(index),
                    0x0409, ctypes.cast(buffer, ctypes.c_void_p), len(blob),
                )
                if not ok:
                    raise SystemExit(f"写入 RT_ICON {index} 失败：{ctypes.get_last_error()}")

            group = build_group_icon(images)
            buffer = ctypes.create_string_buffer(group, len(group))
            ok = UpdateResourceW(
                handle, wintypes.LPCWSTR(RT_GROUP_ICON), wintypes.LPCWSTR(1),
                0x0409, ctypes.cast(buffer, ctypes.c_void_p), len(group),
            )
            if not ok:
                raise SystemExit(f"写入 RT_GROUP_ICON 失败：{ctypes.get_last_error()}")
        finally:
            EndUpdateResourceW(handle, False)

        exe_bytes[:] = tmp.read_bytes()
        return len(images)
    finally:
        try:
            tmp.unlink()
        except OSError:
            pass


# ----------------------------------------------------------------------

def main() -> int:
    args = set(sys.argv[1:])
    out_dir = None
    rid = None
    for a in sys.argv[1:]:
        if a.startswith("--rid="):
            rid = a.split("=", 1)[1]
        elif not a.startswith("-"):
            out_dir = Path(a)

    if rid is None:
        rid = detect_rid()
    if out_dir is None:
        out_dir = ROOT / "src" / "WhaleLog" / "bin" / "Release" / "net8.0-windows"

    dll = out_dir / DLL_NAME
    if not dll.exists():
        print(f"找不到 {dll}，请先编译。", file=sys.stderr)
        return 1

    template = find_apphost(rid)
    data = bytearray(template.read_bytes())
    print(f"apphost 模板：{template}")
    print(f"目标架构：{rid}（按本机已安装的运行时自动选择，可用 --rid= 覆盖）")
    print(f"输出目录：{out_dir}")

    count = patch_placeholders(data, DLL_NAME)
    print(f"  替换了 {count} 处占位符 → {DLL_NAME}")

    old = set_subsystem(data, IMAGE_SUBSYSTEM_WINDOWS_GUI)
    print(f"  子系统 {old} → {IMAGE_SUBSYSTEM_WINDOWS_GUI}（{'控制台' if old == 3 else '其他'} → 图形界面，避免弹黑窗）")

    if "--no-icon" not in args:
        ico = ROOT / "src" / "WhaleLog" / "Resources" / "Assets" / "whalelog.ico"
        if ico.exists():
            icons = inject_icon(data, ico.read_bytes(), out_dir)
            print(f"  已注入图标：{icons} 个尺寸，来自 {ico.name}")
        else:
            print(f"  跳过图标注入（找不到 {ico}）")

    exe = out_dir / f"{EXE_NAME}.exe"
    exe.write_bytes(data)
    print(f"已生成 {exe}（{len(data)} 字节）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

"""把 Windows 的完整环境变量导出成一个 shell 脚本，好在"缺环境变量"的 bash 里跑 dotnet 构建。

为什么需要它
------------
受限会话里的 bash 拿不到 Windows 会话的环境变量（`ProgramData`、`APPDATA`、
`ALLUSERSPROFILE` 之类全缺），于是 `dotnet` 一 restore 就报：

    error MSB4018: Value cannot be null. (Parameter 'path1')
    NuGet.targets(745,5)

看着像"离线环境 restore 不可用"，其实**只是环境变量没传进来** ——
NuGet 取不到 `CommonApplicationData`，.NET 返回的是 null，而 PowerShell 5.1 能正常返回
`C:\\ProgramData`。所以别急着去写 offline_restore 那套，先跑这个脚本。

用法
----
    python tools/mkenv.py                 # → 写 .tmp-env.sh（用前 source 一下）
    python tools/mkenv.py env.sh          # → 写到别处
    source .tmp-env.sh && "C:/Program Files (x86)/dotnet/dotnet.exe" build ...

做法：机器级（`HKLM\\SYSTEM\\...\\Session Manager\\Environment`）+ 用户级（`HKCU\\Environment`）
两处读注册表合并，再用**当前进程已有的**变量覆盖（进程里的 PATH 是 CLI 拼出来的，更贴合当前会话），
最后补几个 .NET 构建必需但注册表里可能没有的。

`.tmp-env.sh` 已在 `.gitignore` 里（`.tmp-*`），不会进仓库。
"""

from __future__ import annotations

import os
import pathlib
import sys
import winreg

# 这两个键里的变量就是"登录时该有的那一套"
SOURCES = (
    (winreg.HKEY_LOCAL_MACHINE,
     r"SYSTEM\CurrentControlSet\Control\Session Manager\Environment"),
    (winreg.HKEY_CURRENT_USER, r"Environment"),
)

# 进程里已经有的优先，但这几个除外 —— 它们正是 CLI 里最可能被改写坏/截断的
SKIP_FROM_PROCESS = ("PATH", "PSModulePath", "PATHEXT", "ComSpec")

# .NET 构建必需、但 Session Manager 里不一定有的（值取本机常见默认）
FALLBACKS = {
    "ProgramData": r"C:\ProgramData",
    "ALLUSERSPROFILE": r"C:\ProgramData",
    "ComSpec": r"C:\WINDOWS\system32\cmd.exe",
    "SystemRoot": r"C:\WINDOWS",
    "windir": r"C:\WINDOWS",
    "ProgramFiles": r"C:\Program Files",
    "ProgramFiles(x86)": r"C:\Program Files (x86)",
    "ProgramW6432": r"C:\Program Files",
    "CommonProgramFiles": r"C:\Program Files\Common Files",
    "CommonProgramFiles(x86)": r"C:\Program Files (x86)\Common Files",
    "CommonProgramW6432": r"C:\Program Files\Common Files",
    "PROCESSOR_ARCHITECTURE": "AMD64",
}


def read_key(root: int, path: str) -> dict[str, str]:
    """读一个注册表键下的所有字符串值。读不到就当空 —— 不是错误。"""
    out: dict[str, str] = {}
    try:
        with winreg.OpenKey(root, path) as key:
            index = 0
            while True:
                try:
                    name, value, _ = winreg.EnumValue(key, index)
                except OSError:
                    break                # 枚举完了
                if isinstance(value, str):
                    out[name] = value
                index += 1
    except OSError:
        pass
    return out


def collect() -> dict[str, str]:
    merged: dict[str, str] = {}
    for root, path in SOURCES:
        merged.update(read_key(root, path))

    # 进程里已有的覆盖注册表 —— 但别用它那几个可能已经被截断的
    merged.update({k: v for k, v in os.environ.items()
                   if k not in SKIP_FROM_PROCESS and isinstance(v, str)})

    for name, value in FALLBACKS.items():
        merged.setdefault(name, value)

    # 带用户的路径要跟着当前用户走，不能写死 Administrator
    home = merged.get("USERPROFILE") or os.path.expanduser("~")
    merged.setdefault("USERPROFILE", home)
    merged.setdefault("APPDATA", os.path.join(home, "AppData", "Roaming"))
    merged.setdefault("LOCALAPPDATA", os.path.join(home, "AppData", "Local"))
    merged.setdefault("USERNAME", os.environ.get("USERNAME") or os.path.basename(home))

    return merged


def write_shell_script(vars_: dict[str, str], target: pathlib.Path) -> int:
    """写成 bash 能 source 的脚本。返回写了多少条。"""
    written = 0
    with target.open("w", encoding="utf-8", newline="\n") as f:
        f.write("#!/usr/bin/env bash\n")
        f.write("# 由 tools/mkenv.py 生成 —— 别手工改，重跑一遍就行。\n")
        for name in sorted(vars_):
            value = vars_[name]
            # 变量名得是 bash 认的；带括号的（ProgramFiles(x86)）得用 env 命令绕，这里直接跳过
            if not name.replace("_", "").isalnum():
                continue
            safe = value.replace("'", "'\"'\"'")     # bash 单引号里的转义写法
            f.write(f"export {name}='{safe}'\n")
            written += 1
    return written


def main(argv: list[str]) -> int:
    target = pathlib.Path(argv[1] if len(argv) > 1 else ".tmp-env.sh")

    collected = collect()
    written = write_shell_script(collected, target)

    was_missing = [k for k in ("APPDATA", "ProgramData", "USERPROFILE", "ALLUSERSPROFILE")
                   if k not in os.environ]

    print(f"已写入 {target}：{written} 个变量")
    print("当前进程里原本就缺的：" + ("、".join(was_missing) if was_missing else "无"))
    if was_missing:
        print("→ 这一类会话直接跑 dotnet 会报 MSB4018 / path1 为 null，先 source 这个文件")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))

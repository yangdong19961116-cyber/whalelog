# -*- coding: utf-8 -*-
"""
离线还原兜底：为「不引用任何 NuGet 包」的 SDK 项目补齐 restore 产物。

为什么需要这个文件
------------------
正常机器上 `dotnet restore` 一条命令就够了，不需要脚本。但在受限环境里
（例如没有完整 shell / 注册表访问权限的沙箱）NuGet 构造 XPlatMachineWideSetting 时
会调用 Environment.GetFolderPath(CommonApplicationData)，拿不到路径就抛
ArgumentNullException，restore 直接失败：

    NuGet.targets(745,5): error : Value cannot be null. (Parameter 'path1')

WhaleLog 一个 NuGet 包都不依赖（只用 .NET SDK 自带的 WPF / WinForms），
所以 restore 的产物是完全固定的空壳，可以手工生成。生成后用：

    dotnet build src/WhaleLog/WhaleLog.csproj --no-restore -p:SkipResolvePackageAssets=true

即可正常编译。**正常环境请仍然优先用 `dotnet restore` + `dotnet build`。**

生成的文件（都在 obj/ 下）：
    project.assets.json              资产文件（空 targets / libraries）
    <项目>.csproj.nuget.g.props      restore 注入的属性
    <项目>.csproj.nuget.g.targets    restore 注入的目标
    <项目>.csproj.nuget.dgspec.json  还原规格（WPF 的 XAML 编译步骤会复制它）
"""

import json
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

PROPS_TEMPLATE = """<?xml version="1.0" encoding="utf-8" standalone="no"?>
<Project ToolsVersion="14.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <PropertyGroup Condition=" '$(ExcludeRestorePackageImports)' != 'true' ">
    <RestoreSuccess Condition=" '$(RestoreSuccess)' == '' ">True</RestoreSuccess>
    <RestoreTool Condition=" '$(RestoreTool)' == '' ">NuGet</RestoreTool>
    <ProjectAssetsFile Condition=" '$(ProjectAssetsFile)' == '' ">$(MSBuildThisFileDirectory)project.assets.json</ProjectAssetsFile>
    <NuGetPackageRoot Condition=" '$(NuGetPackageRoot)' == '' ">{packages}</NuGetPackageRoot>
    <NuGetPackageFolders Condition=" '$(NuGetPackageFolders)' == '' ">{packages}</NuGetPackageFolders>
    <NuGetProjectStyle Condition=" '$(NuGetProjectStyle)' == '' ">PackageReference</NuGetProjectStyle>
    <NuGetToolVersion Condition=" '$(NuGetToolVersion)' == '' ">6.11.0</NuGetToolVersion>
  </PropertyGroup>
  <ItemGroup Condition=" '$(ExcludeRestorePackageImports)' != 'true' ">
  </ItemGroup>
</Project>
"""

TARGETS_TEMPLATE = """<?xml version="1.0" encoding="utf-8" standalone="no"?>
<Project ToolsVersion="14.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <PropertyGroup Condition=" '$(ExcludeRestorePackageImports)' != 'true' ">
    <RestoreSuccess Condition=" '$(RestoreSuccess)' == '' ">True</RestoreSuccess>
    <NuGetProjectStyle Condition=" '$(NuGetProjectStyle)' == '' ">PackageReference</NuGetProjectStyle>
  </PropertyGroup>
  <Target Name="_NuGetRestorePlaceholder" />
</Project>
"""


RUNTIMECONFIG_TEMPLATE = """{{
  "runtimeOptions": {{
    "tfm": "{tfm}",
    "frameworks": [
{frameworks}
    ],
    "configProperties": {{
      "System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization": false
    }}
  }}
}}
"""

DEPS_TEMPLATE = """{{
  "runtimeTarget": {{
    "name": ".NETCoreApp,Version=v8.0",
    "signature": ""
  }},
  "compilationOptions": {{}},
  "targets": {{
    ".NETCoreApp,Version=v8.0": {{
      "{name}/1.0.0": {{
        "runtime": {{
          "{name}.dll": {{}}
        }}
      }}
    }}
  }},
  "libraries": {{
    "{name}/1.0.0": {{
      "type": "project",
      "serviceable": false,
      "sha512": ""
    }}
  }}
}}
"""


def assembly_name_of(csproj: Path) -> str:
    """输出文件用 AssemblyName，没写才退回项目文件名。"""
    for line in csproj.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if line.startswith("<AssemblyName>"):
            return line.replace("<AssemblyName>", "").replace("</AssemblyName>", "").strip()
    return csproj.stem


def write_runtime_files(csproj: Path) -> list:
    """给已有的 bin 输出目录补 runtimeconfig.json / deps.json，让它能直接被 dotnet 启动。"""
    written = []
    name = assembly_name_of(csproj)
    tfm = tfm_of(csproj)
    bin_root = csproj.parent / "bin"
    if not bin_root.exists():
        return written

    use_wpf, use_winforms = uses_desktop(csproj)
    refs = ['      { "name": "Microsoft.NETCore.App", "version": "8.0.0" }']
    if use_wpf:
        refs.append('      { "name": "Microsoft.WindowsDesktop.App", "version": "8.0.0" }')
    elif use_winforms:
        refs.append('      { "name": "Microsoft.WindowsDesktop.App", "version": "8.0.0" }')

    for out_dir in sorted(p for p in bin_root.rglob(tfm) if p.is_dir()):
        if not (out_dir / f"{name}.dll").exists():
            continue

        (out_dir / f"{name}.runtimeconfig.json").write_text(
            RUNTIMECONFIG_TEMPLATE.format(tfm=tfm.split("-")[0] or "net8.0", frameworks=",\n".join(refs)),
            encoding="utf-8",
        )
        (out_dir / f"{name}.deps.json").write_text(
            DEPS_TEMPLATE.format(name=name), encoding="utf-8"
        )
        written.append(out_dir)

    return written


def find_dotnet() -> str:
    for candidate in (
        r"C:\Program Files (x86)\dotnet\dotnet.exe",
        r"C:\Program Files\dotnet\dotnet.exe",
    ):
        if Path(candidate).exists():
            return candidate
    return "dotnet"


def sdk_version(dotnet: str) -> str:
    try:
        out = subprocess.run([dotnet, "--version"], capture_output=True, text=True, check=True)
        v = out.stdout.strip()
        if v:
            return v
    except Exception:
        pass
    return "8.0.100"


def tfm_of(csproj: Path) -> str:
    for line in csproj.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if line.startswith("<TargetFramework>"):
            return (
                line.replace("<TargetFramework>", "")
                .replace("</TargetFramework>", "")
                .strip()
            )
    raise SystemExit(f"在 {csproj} 里找不到 TargetFramework")


def uses_desktop(csproj: Path) -> tuple:
    """读 csproj 判断是否需要 WPF / WinForms 框架引用。"""
    text = csproj.read_text(encoding="utf-8")
    return (
        "<UseWPF>true</UseWPF>" in text,
        "<UseWindowsForms>true</UseWindowsForms>" in text,
    )


def restore_block(csproj: Path, tfm: str, packages: str) -> dict:
    return {
        "projectUniqueName": str(csproj),
        "projectName": csproj.stem,
        "projectPath": str(csproj),
        "packagesPath": packages,
        "outputPath": str(csproj.parent / "obj") + "\\",
        "projectStyle": "PackageReference",
        "configFilePaths": [],
        "originalTargetFrameworks": [tfm],
        "sources": {},
        "frameworks": {tfm: {"targetAlias": tfm, "projectReferences": {}}},
        "warningProperties": {"warnAsError": ["NU1605"]},
    }


def write_all(csproj: Path, dotnet: str) -> None:
    tfm = tfm_of(csproj)
    obj = csproj.parent / "obj"
    obj.mkdir(parents=True, exist_ok=True)

    packages = str(Path.home() / ".nuget" / "packages").replace("/", "\\") + "\\"
    name = csproj.name  # 例如 WhaleLog.csproj

    use_wpf, use_winforms = uses_desktop(csproj)
    framework_refs = {"Microsoft.NETCore.App": {"privateAssets": "all"}}
    if use_wpf:
        framework_refs["Microsoft.WindowsDesktop.App.WPF"] = {"privateAssets": "all"}
    if use_winforms:
        framework_refs["Microsoft.WindowsDesktop.App.WindowsForms"] = {"privateAssets": "all"}

    assets = {
        "version": 3,
        "targets": {tfm: {}},
        "libraries": {},
        "projectFileDependencyGroups": {tfm: []},
        "packageFolders": {packages: {}},
        "project": {
            "version": "1.0.0",
            "restore": restore_block(csproj, tfm, packages),
            "frameworks": {
                tfm: {
                    "targetAlias": tfm,
                    "frameworkReferences": framework_refs,
                    "runtimeIdentifierGraphPath": str(
                        Path(dotnet).parent / "sdk" / sdk_version(dotnet) / "RuntimeIdentifierGraph.json"
                    ),
                }
            },
        },
    }

    dgspec = {
        "format": 1,
        "restore": {str(csproj): restore_block(csproj, tfm, packages)},
    }

    (obj / "project.assets.json").write_text(
        json.dumps(assets, indent=2, ensure_ascii=False), encoding="utf-8"
    )
    (obj / f"{name}.nuget.dgspec.json").write_text(
        json.dumps(dgspec, indent=2, ensure_ascii=False), encoding="utf-8"
    )
    (obj / f"{name}.nuget.g.props").write_text(
        PROPS_TEMPLATE.format(packages=packages), encoding="utf-8"
    )
    (obj / f"{name}.nuget.g.targets").write_text(TARGETS_TEMPLATE, encoding="utf-8")

    print(f"已为 {csproj.name} 补齐 restore 产物（tfm={tfm}）→ {obj}")


def main() -> int:
    targets = [a for a in sys.argv[1:] if not a.startswith("-")] or [
        "src/WhaleLog/WhaleLog.csproj"
    ]
    dotnet = find_dotnet()

    for rel in targets:
        csproj = (ROOT / rel).resolve()
        if not csproj.exists():
            print(f"找不到项目：{csproj}", file=sys.stderr)
            return 1
        write_all(csproj, dotnet)

        for out_dir in write_runtime_files(csproj):
            print(f"已补运行时清单 → {out_dir}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())

# 交接单 · 鲸记 WhaleLog（桌面工作记录台）

> 用途：把「桌面笔记 / 工作记录工具」的完整上下文迁到新任务，新任务读这一份即可接续。
> 交接时间：2026-09-15 17:05 · 上一轮工作止于 2026-09-15 17:00（发 1.3，悬浮闭眼差分）

---

## 0. 新任务开工第一件事：读这三份

> **换到另一台设备：先看 `新设备继续指令.md`**（怎么装环境、怎么验、粘贴什么话给 WorkBuddy）。
> 下面路径都相对**仓库根目录** —— 原来在 `C:\Users\Administrator\WorkBuddy\workwork\`，
> 换了机器就以解压出来的那个目录为根。

| 顺序 | 文件 | 作用 |
|---|---|---|
| 1 | 本文件 | 现状、未完成事项、下一步 |
| 2 | `.workbuddy/memory/MEMORY.md` | **技术决策与全部踩坑记录，必读** |
| 3 | `src/WhaleLog/README.md` | 功能全貌 + 构建 / 打包 / 自测 / 诊断用法 |

- **代码根目录**（在本工作空间之外）：`C:\Users\Administrator\WorkBuddy\workwork\`
- 上一轮原始日志：`.workbuddy\memory\` 下的 `2026-09-11.md`（分批记录，含每轮实跑抓到的真 bug）、
  `2026-09-14.md`（动态素材调研）、`2026-09-15.md`（播放器接入、发 1.2 / 1.3）
- 本工作空间内这份交接单与 `workwork\HANDOFF-鲸记WhaleLog.md` 内容相同，从哪个入口进来都能接上。

---

## 1. 这是什么

桌面常驻的每日工作记录台：一边手写今天做了什么，一边把经手的文件拖进来自动归类、记住位置、标记版本，
到点前由桌面上的小鲸鱼提醒收尾下班。

**技术栈**：WPF + .NET 8（`net8.0-windows`），纯手写无第三方 NuGet 依赖（除 AudioRoute 用的 NAudio，与本项目无关）。

> ### 一条不可动摇的硬性设计约束
> **绝不移动 / 重命名 / 删除用户的原文件。** 只读元数据 + 算指纹，原文件永远留在原位置。
> 快照模式（复制副本）默认关闭。
> 任何形式的「按规则自动搬文件」一律不做 —— 规则写错一次就是数据事故。
> 这条改动前必须跟用户确认。

同仓还有另一个**完全无关**的项目 `src/AudioRoute/`（Win10 音频设备切换托盘应用），别串了。

---

## 2. 现状快照（2026-09-15 实测，非推测）

| 项 | 值 |
|---|---|
| 版本 | `1.3.0`（`src/WhaleLog/WhaleLog.csproj` 的 `<Version>`，界面读 `Assembly.GetName().Version` 自动跟） |
| TFM | `net8.0-windows`（**不要加 OS 版本号**） |
| 平台 | AnyCPU（**不能设 x64**，见第 5 节） |
| 主程序 | `src/WhaleLog/`：App + MainWindow + `Views/`(6 个窗口 + `SpritePlayer`/`Converters`/`AssetImageExtension`) + `Services/`(21 个，含 `AnimSheet`/`AnimLibrary`) + `Models/`(4 个) |
| 可运行产物 | `src/WhaleLog/bin/Release/net8.0-windows/WhaleLog.exe`；解压态副本 `build/portable/鲸记 WhaleLog/` |
| 桌面安装包 | `C:\Users\Administrator\Desktop\鲸记 WhaleLog 1.3.zip`（自包含，解压即用） |
| 自测 | `tools/selftest/` 216 项断言，直接编译主程序非界面部分，测的是发货代码 |
| 诊断自检 | `WhaleLog.exe --selfcheck` → `logs\selfcheck.log`（164 项，19 个段落） |
| 版本控制 | ✅ 有 git 仓库（`workwork\.git`），基线 `f05c39b`，当前 HEAD 见 `git log` |

---

## 3. 已完成的功能（七块，均已实跑验证）

1. **当日记录** —— 每天一份 Markdown，带时间 / 分类 / 标签 / 完成状态 / 耗时；日期栏可展开**整月日历**（有记录的日子打点、今天描边、未来不可选、固定 6×7 格不跳高）；底部「跳到」输入框认 `9/23`、`9-23`、`9.23`、`0923`、`2026-09-23`、`9月23日`、`昨天/今天/前天/大前天`、只填日号（当月）。
2. **拖拽归档** —— 拖文件/文件夹到主面板即归档；规则引擎分类（借鉴 DropIt，只保留「条件」那半）；归属日期跟**当前查看的日期**走（支持补录过去某天，横幅显式写明目标日期）；「只看当天 / 全部」筛选，「全部」视图**按归档日期分栏**（栏头带今天/昨天/星期，跨年补年份）。
3. **版本标记** —— SHA-256 指纹判版本链（同路径同指纹=重复、同路径异指纹=v2/v3、链内已有同指纹=「与 vN 内容一致」）；版本链键 = 归一化主名（剥掉 `v2`/`第3版`/(1)/`_final`/`_副本`）；大文件（默认 >200MB）走 `sample:` 前缀的采样指纹，永不与全量指纹相等。
4. **快速记录** —— 全局快捷键 `Ctrl+Alt+N` 光标旁弹输入框，`Enter` 存、`Shift+Enter` 换行、`#标签` 打标；用 `HWND_MESSAGE` 消息专用窗口，托盘静默启动也能生效。
5. **日报 / 周报** —— 纯读取渲染，不改原始记录，可一键复制。
6. **桌面小鲸鱼** —— 无边框透明置顶，拖动松手吸边，闲置收成贴边小头像；位置/大小/透明度全记忆；大小范围 0.7×–1.6×，**窗口尺寸与内容缩放出自同一个系数**（`RootGrid.LayoutTransform`），滑杆拖动实时生效不等落盘节流。鼠标停上去时整张换成表情差分（默认闭眼，见第 4 节 B）。
7. **下班提醒 + 托盘** —— 默认 18:00 提前 15 分钟，气泡弹跳 + 音效 + 托盘通知三通道；文案按当天状态自动调语气；托盘常驻、开机自启、桌面快捷方式（程序内 `--create-shortcut` 创建，绕开 `.cmd` 中文编码坑）。

**配套机制**：外部素材目录（`Assets\` 同名文件优先于内置，`{v:AssetImage}` 扩展引用，托盘可「重新加载素材」）、
诊断自检（快捷键/图标/快捷方式/窗口/页签互斥/文件筛选/日期分组/日期解析/滑杆/悬浮窗缩放/小鲸鱼动画/悬浮态形象，且**探针零副作用**）、
离屏截图（`--shot` / `--shot-notes` / `--shot-floating`）、合成真实鼠标的交互探针（`tools/drag_probe.py`）、
差分图对齐工具（`tools/prepare_expression.py`）。

---

## 4. 未完成 / 在研 ← **重点看这节**

### A. 动态素材（悬浮窗动画）—— 播放器已接入，缺真实素材

9/14 做的这一批已经补记进 `workwork\.workbuddy\memory\2026-09-14.md`（原本是漏的，最容易被新任务忽略）。

**已经做完的：**

| 产出 | 说明 |
|---|---|
| `tools/animprobe/` | 独立 WPF 探针程序，实测「WPF 到底能播什么」。**结论写死在 `Program.cs` 末尾**，结果输出到 `%TEMP%\whalelog-anim-probe\wpf-probe.txt` |
| `tools/make_anim_sample.py` | 生成样例精灵图 + 帧表（同时是规格的参考实现），规格常量集中在文件头部「规范画布」一节 |
| `tools/check_anim_assets.py` | **交付前自检工具**，给画师/做素材的人跑。查三类返工原因：横向漂移（>2px 报错）、贴边裁切（余量 <2px 报错）、帧表与实际尺寸矛盾。退出码 0/1/2 |
| `outputs/样例-点击弹跳/sheet.{png,json}` | 8 帧弹跳样例（4×2 网格） |
| `outputs/样例-眨眼示意/sheet.{png,json}` | 4 帧眨眼样例 |
| `outputs/动画素材规格/*.png` | 4 张规格说明图（对齐校验-本体与最高帧叠加、样例-8帧弹跳、样例-点击弹跳-8帧、样例-眨眼示意-4帧） |

**探针实测结论（已经过实验，别再重测）：**

| 容器 | 动画 | 透明 | 便携性 |
|---|---|---|---|
| GIF | ✓ 帧数/时长准 | ✗ **仅 1 位，边缘只能是硬的** | 全球通用 |
| APNG | ✗ WPF 只解第 1 帧 | — | — |
| 动画 WebP | ✓ | ✓ 8 位可羽化 | ✗ 需 Store 包 `Microsoft.WebpImageExtension`，**非 Windows 默认组件** |
| PNG 序列 | ✓ 自己排帧 | ✓ 8 位 | 全球通用 |
| **精灵图 PNG** | ✓ 自己切帧 | ✓ 8 位 | 全球通用 ← **推荐** |

> 实测依据：同一组带羽化软边的帧，GIF 解出 2 级 alpha（0/255），WebP 与 PNG 都解出 122 级。
> **→ 动态素材首选「精灵图 PNG + 帧表 JSON」。**

**规范画布（因为原图会裁切才改的）：**

原 `DSniang1.png` 角色**贴着画布的右边和下边**（左留 45 / 上留 10 / 右 0 / 下 0），
任何向右位移、横向拉伸、向下压扁立刻被裁（实测 8 帧弹跳版有 6 帧被裁）。
所以规定：画布 **720×720**、角色高 600（占 83%）、横向居中 `x=360`、脚底基线 `y=660`
→ 上留 61 / 下留 59 / 左右各留 77，角色大小一点没变，只是四周有了余量。

唯一硬规则：**每帧角色的「横向中心」和「脚底」必须落在同一点上**（x=360，脚底=660，
跳跃帧整体上移即脚底 <660）。帧间只允许「整体垂直位移」和「绕基准点缩放」，横向中心绝对不漂。

帧表格式（`sheet.json`）：

```json
{
  "name": "clickBounce", "version": 1, "type": "sheet",
  "sheet": "sheet.png",
  "frameWidth": 720, "frameHeight": 720,
  "cols": 4, "rows": 2, "count": 8,
  "fps": 24, "loop": false,
  "anchor": { "x": 360, "y": 660 },
  "characterHeight": 600,
  "returnTo": "base", "sound": ""
}
```

**播放器已接入（2026-09-15 完成）：**

| 产出 | 说明 |
|---|---|
| `Services/AnimSheet.cs` | 帧表解析 + 校验 + **行优先**切帧（纯逻辑，刻意不碰 WPF，好让自测直接编译） |
| `Services/AnimLibrary.cs` | 扫 `Assets\anim\<动作名>\`，动作名归一化 + 别名；目录名与动作键一致者优先（解撞车） |
| `Views/SpritePlayer.cs` | `CroppedBitmap` 切帧（不复制像素）+ `CompositionTarget.Rendering` / `DispatcherTimer` 双通道驱动；**时间由时钟算**（已过时间 ÷ 单帧时长），不是计次 |
| `FloatingWindow.xaml.cs` | 接入三个动作：`clickBounce`（点击）、`blink`（闲置眨眼）、`alert`（提醒气泡时优先，播不了才退回原 `Bounce`）；播完回落静态本体 |
| 托盘开关 | 「小鲸鱼动画」关掉退回静态 168px，素材文件不用删 |
| `docs/动态素材规格.md` | 规格从两个 `.py` 的注释里独立出来，可直接发给做素材的人；`make_portable_zip.py` 会把它复制进便携包 |

**尺寸是关键，别踩回去**：装素材前后角色在屏幕上都是 **165px**，
鲸鱼框从静态 168px 变成 **198px**（= `165 × 720 ÷ 600`，按帧表 `characterHeight` 反算），
悬浮窗设计高 **296 → 326**（`ExpandedDesignHeight = 296 + (框高 − 168)`）。
**窗口高度和鲸鱼框必须出自同一个系数**，漏一处就会出现"鲸鱼变大了但窗口没跟上"。

**还没做的：**

- ❌ **出厂不带真实素材。** `Assets\anim\` 解压后是空的，用户/画师得按规格自己放。
  （`outputs/` 下有两个样例：`样例-点击弹跳`、`样例-眨眼示意`，可当参考实现。）
- ❌ 提醒动作 `alert` 只有代码路径，没有对应的样例素材；眨眼样例是 4 帧示意稿。
- ❌ 动作事件目前是**写死的映射**，没有做成可配置。

**实现上的两个坑（已踩过）：**
1. 采样渲染时钟必须跑真消息循环，用 `Thread.Sleep` 堵 UI 线程会得出「渲染时钟不可用」的错误结论。
2. 目标 24fps；`fps` 上限卡 30（悬浮窗尺寸小，更高纯属浪费）。

### B. 悬浮态形象（表情差分）—— 已完成

鼠标停在悬浮窗上时，小鲸鱼整张换成另一张形象（默认闭眼），移开换回来。

| 产出 | 说明 |
|---|---|
| `Resources/Assets/closeeye.png` | 闭眼差分，610×610，与 `DSniang1.png` 逐像素对齐（IoU 0.9894） |
| 素材位 `whale-hover` | `AssetCatalog` 新增；候选名 `whale-hover.png` / `closeeye.png` / `anim/closeeye.png`，没配就安静回落，悬浮毫无变化 |
| `FloatingWindow` 的 `_stillImage` / `_hoverImage` / `_hovering` / `RestingImage` | 切换逻辑；`SetHovering()` 里同时把播放器的 `BaseImage` 一起切过去 |
| `tools/prepare_expression.py` | **新工具**：抠白底 + 归到基准画布 + 搜索最佳平移对齐 + 报 IoU 与差异图 |

**两个必须守住的点（都写进断言了）：**

1. **`OnMouseLeave` 里「要不要自动收起」那个分支会提前 `return`**，
   形象还原必须排在它前面 —— 否则关掉自动收起的用户鼠标一移开就永远停在闭眼状态。
   自检的悬浮探针走的是**真实的处理函数**（`SimulateMouseEnter/Leave`），专门守这条顺序。
2. **悬浮期间动画播完，落的必须是悬浮那张。** 播放器播完落回 `BaseImage`，
   它得跟着悬浮状态一起切，不然会闪一下睁眼。

**差分图的硬要求**：和 `whale.png` **同画布、逐像素对齐**。不一致的话鼠标一移上去角色就跳一下，
而这在界面上只表现为"好像抖了一下"，极难归因。自检会把两张图的像素尺寸并排写进报告，不一致直接判失败。

### C. README 里自己列的待办（还没动）

- 周报 / 月报的对比视图（本周 vs 上周的文件与事项变化）
- 拖入文件时按当前选中的分类自动打标
- 历史日期的一键导出（Markdown / CSV）
- 快速记录支持语音输入

---

## 5. 本机环境限制（不读会白折腾一小时）

1. **`dotnet restore` 在受限会话不可用**，稳定报 `Value cannot be null. (Parameter 'path1')`
   —— NuGet 取不到 `CommonApplicationData`（对照实验：PowerShell 5.1 能返回 `C:\ProgramData`，.NET 8 返回 null）。
   鲸记不依赖任何 NuGet 包，用 `build-whalelog-offline.cmd` 绕过：
   `tools/offline_restore.py` 手写 restore 产物 → 带
   `-p:SkipResolvePackageAssets=true -p:GenerateDependencyFile=false -p:GenerateRuntimeConfigurationFiles=false` 编译。
2. **`dotnet.exe` 是 32 位宿主** → 项目**不能设 `PlatformTarget=x64`**（`FileLoadException`），用 AnyCPU；
   apphost 也只能按本机已装运行时架构选（本机只有 x86）。
3. **TFM 不要带 OS 版本号**：`net8.0-windows10.0.19041.0` 会要求 `Microsoft.Windows.SDK.NET.Ref`（需 restore）。
4. **`UseWPF` + `UseWindowsForms` 同开会有大量类型名歧义**（`Application`/`Point`/`MouseEventArgs`/`DragEventArgs`/`IDataObject`），
   已在 csproj 用 `<Using Remove="System.Windows.Forms" /><Using Remove="System.Drawing" />` 去掉隐式 using。
5. **改代码前先看有没有 WhaleLog 在运行**（`MSB3021/MSB3027 文件被锁定`）。
   节奏：**先在 Debug 版验功能**（输出目录不同不会被锁），最后才切 Release 并重生成 apphost、重指快捷方式。
6. **`dotnet.exe` 是控制台子系统程序**，从快捷方式启动会一直挂个黑控制台窗口
   → 必须用 `tools/make_apphost.py` 离线生成 GUI 子系统的 `WhaleLog.exe`。
7. **`build-whalelog-offline.cmd` 依赖 cmd.exe 与 PATH 里的 `dotnet`**。
   只能执行 bash 的会话（调 `cmd.exe` 会被安全策略拦）要手工复现它那四步：
   `offline_restore.py` → `dotnet build …`（`dotnet` 用完整路径
   `"C:\Program Files (x86)\dotnet\dotnet.exe"`）→ `offline_restore.py` → `make_apphost.py`。
   **`offline_restore.py` 自己会找 dotnet，不用喂。** 注意第 3 步不能省 ——
   带 `-p:GenerateRuntimeConfigurationFiles=false` 编译出来的产物没有 `runtimeconfig.json`，
   直接跑会报 `hostpolicy.dll not found`；重跑一遍会补上。

---

## 6. 常用命令速查

```bat
:: 编译（受限环境，推荐）
build-whalelog-offline.cmd

:: 编译（正常环境，需要能 restore）
build-whalelog.cmd

:: 只编译不发布
dotnet build WhaleLog.sln -c Release

:: 自测（216 项断言，直接编译主程序非界面部分）
dotnet build tools\selftest\selftest.csproj -c Release
dotnet tools\selftest\bin\Release\net8.0\WhaleLog.SelfTest.dll
::  加 --keep 保留临时目录，方便翻看产出的 Markdown 和 index.json

:: 诊断自检 → %AppData%\WhaleLog\logs\selfcheck.log
WhaleLog.exe --selfcheck          :: --quiet 只写日志不弹窗

:: 离屏截图（不弹到面前）
WhaleLog.exe --shot D:\tmp\files-page.png
WhaleLog.exe --shot-notes D:\tmp\notes-page.png
WhaleLog.exe --shot-floating D:\tmp\floating-scales.png
WhaleLog.exe --export-assets D:\tmp\Assets

:: 打包自包含 zip（不带参数会输出到桌面：鲸记 WhaleLog.zip；
:: 按本仓惯例要显式带版本号，例如 鲸记 WhaleLog 1.3.zip）
python tools\make_portable_zip.py
python tools\make_portable_zip.py D:\某目录
python tools\make_portable_zip.py "C:\Users\Administrator\Desktop\鲸记 WhaleLog 1.3.zip"

:: 素材
python tools\make_icon.py                                   :: 610×610 PNG → 7 尺寸 ICO
python tools\check_anim_assets.py outputs\样例-点击弹跳       :: 动态素材交付前校验
python tools\prepare_expression.py 新图.png out\whale-hover.png
                                                            :: 表情差分：抠白底 + 对齐基准画布 + 报 IoU
                                                            :: 需要 pip install pillow numpy

:: 交互行为探针（会真的移动鼠标光标、会先结束正在运行的鲸记 → 手动跑）
python tools\drag_probe.py
```

**两个环境变量**：`WHALELOG_DATA_DIR` 改数据目录（造临时数据，别往用户记录里塞测试文件）、
`WHALELOG_ASSET_DIR` 改素材目录。

**命令行开关的坑**：`--x` 和 `--x=值` 两种写法都要认（只做全等比较的话 `--shot=路径` 会被当成「没有这个开关」，
程序按普通方式启动不退出，命令行那边只看到「卡住」）。开关取值时要判断下一个参数是不是另一个开关，
否则 `--export-assets --quiet` 会把素材导出到名为 `--quiet` 的目录。

---

## 7. 接手时的注意事项（血泪总结，详见 MEMORY.md）

- **任何「隐式规则」都必须显示在界面上**（文件归「当前查看的日期」本身合理，但横幅必须写明目标日期）。
- **单选绝不能让两个控件各读各的 `IsChecked`** —— WPF 的 `ToggleButton` 不互斥。
  页签重叠、文件筛选点不动，两次同类 bug 都出在这里。
  必须有一个唯一 source of truth，目标值**从被点的 sender 推导**，
  且写法要带上 `: 当前值` 兜底（再点当前项会把 `IsChecked` 反选成 false，不兜住就「两个都不选」）。
- **探针/诊断工具必须零副作用。** 出过两次真事故：自检把用户桌面快捷方式改指到临时解压目录；
  探针把用户不透明度从 0.3 写成 1。现在统一 `Snapshot()`/`Restore()` + `CancelPendingSaves()`。
- **别用 Edit 工具当「看文件」的手段**（曾把下一行代码拼到注释后面，编译才发现）；要看内容用 Read/Grep。
- **给方法起名别用 BCL 常见类型名**（`Key`/`Point`/`Color`/`Brush`/`Path`/`Task`），
  一个 `Key(DateTime)` 就把 `Key.Enter` 顶掉（CS0119）。
- **只编译不运行是发现不了问题的**。WPF 里「把画刷当样式用」「事件在 XAML 解析阶段就被触发」
  这类问题编译期一个都不报，只有真的把窗口创建出来、真的去点一下才会暴露。

---

## 8. 上一轮「建议的下一步」执行情况（2026-09-15 全部完成）

| # | 事项 | 结果 |
|---|---|---|
| 1 | 接动画：写 `FloatingWindow` 的精灵图播放器 | ✅ 已接入，三个动作全部打通并实跑验证 |
| 2 | 整理 `docs\动态素材规格.md` | ✅ 已写（含垂直余量公式 + 排查顺序表），并随便携包附带 |
| 3 | 回填 `workwork\.workbuddy\memory\` | ✅ 补了 `2026-09-14.md`（动态素材调研）与 `2026-09-15.md`（播放器接入） |
| 4 | `git init` + 初始提交 | ✅ 已建仓库，基线 `f05c39b` |

顺带做完的：版本升到 **1.2.0**、重建 Release 与 apphost、打出
`桌面\鲸记 WhaleLog 1.2.zip`（57.3 MB）、修掉样例素材「最高帧头顶被裁」的缺陷、
修掉自测里一处 `CS8602` 警告。

之后用户提出「鼠标悬浮时换成闭眼形象」，做成后版本升到 **1.3.0**，
再重建 Release、打出 `桌面\鲸记 WhaleLog 1.3.zip`。详见第 4 节 B。

---

## 9. 建议的下一步

1. **`tools/drag_probe.py` 一直没跑过**（会真的移动鼠标光标、并先结束正在运行的鲸记，
   所以只能手动、且要先退掉你正开着的实例）。拖拽归档是本项目最核心的交互，
   但至今只靠离屏截图和自测覆盖，真实鼠标路径没验证过。
2. **悬浮态形象没有做过真实鼠标的目视确认。** 自检的悬浮探针走的是真实处理函数、
   9 条断言全过，但那是**代码路径**的验证；真光标移上去长什么样没看过
   （不自动移光标是既定纪律）。想确认就自己把鼠标挪上去看一眼。
3. **出厂素材是不是该补动画样例？** 现在 `Assets\anim\` 解压后是空的，
   用户开箱看不到动画效果，得自己按规格做。把 `outputs/样例-点击弹跳` 塞进包的
   `Assets\anim\clickBounce\` 就能开箱即见 —— 但这是产品决策（样例画风是否代表最终形象），
   要用户拍板。`Assets\whale-hover.png` 已经随包出厂了，所以悬浮那一下是开箱可见的。
4. **接入真实素材后**，先跑 `python tools\check_anim_assets.py <目录>` 过一遍（退出码 0 才算合格），
   再放进 `Assets\anim\<动作名>\` 点托盘「重新加载素材」。
   表情差分走 `python tools\prepare_expression.py 新图.png out\whale-hover.png`，IoU ≥ 0.98 才算合格。
5. **README 自列的四项待办**（第 4 节 C）：周报对比视图、拖入自动打标、历史导出、语音输入。
6. 桌面上累积了三个包：`鲸记 WhaleLog 1.1.zip`（无动画）、`1.2.zip`（有动画、无悬浮差分）、
   `1.3.zip`（当前）。**删除桌面文件需用户明确同意**，别自作主张清桌面 ——
   已经问过一次，用户还没表态。
7. **Git 远端已接上半程 —— 仓库建好了，但全量还没推上去。**
   仓库：`https://github.com/yangdong19961116-cyber/whalelog.git`（私有，默认分支 `main`）。
   2026-09-15 实测：
   - **仓库里目前只有文档快照**（`README.md` / 交接单 / `新设备继续指令.md` /
     `.workbuddy\memory\MEMORY.md` / `LICENSE` / `.gitignore`），是本机通过平台 GitHub
     连接器逐文件写上去的。
   - **全量（962 KB 文本 + 1 MB 二进制图）传不上去**：连接器是把文件内容当文本参数写的，
     等于要先把内容读进模型上下文，这个量级不允许；而本机 `github.com` 被代理拦
     （`CONNECT tunnel failed, response 502`），`git push` 走不通。
   - → **全量推送必须由新设备做一次**（7 行命令，见 `新设备继续指令.md` 第 6 节）。
     推完 `main` 才是唯一事实源。
   - ⚠️ **本机仍然推不了、也 `pull` 不了**（同为 `github.com` 被拦）。GitHub 这条通道
     只对新设备有效，本机继续走「拷包」老路。
   - 连接器权限边界（供后续参考）：**有文件写权限**（往不存在的仓库写返回 404 而非 403），
     **没有建仓库权限**（`create_repository` → `403 Resource not accessible by integration`）。
8. **仓库根 README 已归位。** 原先放在根目录的 `README.md` 其实是工作区里**另一个项目
   AudioRoute**（音频设备切换托盘应用）的，已移到 `src/AudioRoute/README.md`；
   根 README 重写成工作区总览，并加了「仓库暂不完整」的显眼警告。
   ⚠️ 注意：`workwork` 是**双项目工作区**（AudioRoute + WhaleLog），
   建基线时 AudioRoute 的 20 个文件也一并进了仓库 —— 交接时别把它只当鲸记一个项目看。

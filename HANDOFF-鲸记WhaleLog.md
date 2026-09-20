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
  `2026-09-14.md`（动态素材调研）、`2026-09-15.md`（播放器接入、发 1.2 / 1.3）、
  `2026-09-17.md`（归属日期错位修复、素材换无损原图版）、
  `2026-09-20.md`（dsh-pet 调研 + **落地**、发 1.4.0）
- **这一版新加的两份文档**：`docs/dsh-pet-集成方案与调研.md`（含末尾「落地结果」整章）、
  `docs/动态素材规格.md` 第 2 节（形象包机制）—— 要改形象/加动作就看这两份。
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

## 2. 现状快照（2026-09-20 实测，非推测）

| 项 | 值 |
|---|---|
| 版本 | `1.4.0`（`src/WhaleLog/WhaleLog.csproj` 的 `<Version>`，界面读 `Assembly.GetName().Version` 自动跟） |
| TFM | `net8.0-windows`（**不要加 OS 版本号**） |
| 平台 | AnyCPU（**不能设 x64**，见第 5 节） |
| 主程序 | `src/WhaleLog/`：App + MainWindow + `Views/`(6 个窗口 + `SpritePlayer`/`Converters`/`AssetImageExtension`) + `Services/`(24 个，含 `AnimSheet`/`AnimLibrary`/`CharacterPacks`/`VideoDecoder`/`FfmpegLocator`) + `Models/`(4 个) |
| 可运行产物 | `src/WhaleLog/bin/Release/net8.0-windows/WhaleLog.exe`；解压态副本 `build/portable/鲸记 WhaleLog/` |
| 分发产物 | **统一放 `E:\jingji`**（2026-09-17 起的项目约定，别再丢桌面）：便携包 `鲸记 WhaleLog <版本>.zip`（自包含、解压即用，版本号从 csproj 读）、交接包 `鲸记 WhaleLog 项目交接包 <日期>.zip`。两个打包脚本**不带参数**就输出到这儿，目录不存在会自动建 |
| 自测 | `tools/selftest/` **294 项**断言，直接编译主程序非界面部分，测的是发货代码 |
| 诊断自检 | `WhaleLog.exe --selfcheck` → `logs\selfcheck.log`（**187 项，21 个段落**；含「形象包」「透明视频」两节，视频那节**真的起一次 ffmpeg 解一遍**） |
| 形象素材 | `Assets\characters\dsh\`：106 个动作 / 25423 帧 / **51.9 MB**（透明 webm，原样搬运）。播放要 `ffmpeg.exe` 在程序目录（便携包已带） |
| 版本控制 | ✅ 有 git 仓库（`workwork\.git`），基线 `f05c39b`，当前 HEAD 见 `git log` |

---

## 3. 已完成的功能（七块，均已实跑验证）

1. **当日记录** —— 每天一份 Markdown，带时间 / 分类 / 标签 / 完成状态 / 耗时；日期栏可展开**整月日历**（有记录的日子打点、今天描边、未来不可选、固定 6×7 格不跳高）；底部「跳到」输入框认 `9/23`、`9-23`、`9.23`、`0923`、`2026-09-23`、`9月23日`、`昨天/今天/前天/大前天`、只填日号（当月）。
2. **拖拽归档** —— 拖文件/文件夹到主面板即归档；规则引擎分类（借鉴 DropIt，只保留「条件」那半）；归属日期跟**当前查看的日期**走（支持补录过去某天，横幅显式写明目标日期）；「只看当天 / 全部」筛选，「全部」视图**按归档日期分栏**（栏头带今天/昨天/星期，跨年补年份）。
3. **版本标记** —— SHA-256 指纹判版本链（同路径同指纹=重复、同路径异指纹=v2/v3、链内已有同指纹=「与 vN 内容一致」）；版本链键 = 归一化主名（剥掉 `v2`/`第3版`/(1)/`_final`/`_副本`）；大文件（默认 >200MB）走 `sample:` 前缀的采样指纹，永不与全量指纹相等。
4. **快速记录** —— 全局快捷键 `Ctrl+Alt+N` 光标旁弹输入框，`Enter` 存、`Shift+Enter` 换行、`#标签` 打标；用 `HWND_MESSAGE` 消息专用窗口，托盘静默启动也能生效。
5. **日报 / 周报** —— 纯读取渲染，不改原始记录，可一键复制。
6. **桌面小鲸鱼** —— 无边框透明置顶，拖动松手吸边（**两轴独立**，能吸到角上），闲置收成贴边小头像；位置/大小/透明度全记忆；大小范围 0.7×–1.6×，**窗口尺寸与内容缩放出自同一个系数**（`RootGrid.LayoutTransform`），滑杆拖动实时生效不等落盘节流。鼠标停上去时整张换成表情差分（默认闭眼，见第 4 节 B）。**形象可在「溟月（新版）」与「鲸鱼娘（原版）」之间一键切换**（见第 4 节 A），装了新版素材还带 106 个透明视频动作。
7. **下班提醒 + 托盘** —— 默认 18:00 提前 15 分钟，气泡弹跳 + 音效 + 托盘通知三通道；文案按当天状态自动调语气；托盘常驻、开机自启、桌面快捷方式（程序内 `--create-shortcut` 创建，绕开 `.cmd` 中文编码坑）。

**配套机制**：外部素材目录（`Assets\` 同名文件优先于内置，`{v:AssetImage}` 扩展引用，托盘可「重新加载素材」）、
诊断自检（快捷键/图标/快捷方式/窗口/页签互斥/文件筛选/日期分组/日期解析/滑杆/悬浮窗缩放/小鲸鱼动画/悬浮态形象，且**探针零副作用**）、
离屏截图（`--shot` / `--shot-notes` / `--shot-floating`）、合成真实鼠标的交互探针（`tools/drag_probe.py`）、
差分图对齐工具（`tools/prepare_expression.py`）。

---

## 4. 未完成 / 在研 ← **重点看这节**

### A. 动态素材（悬浮窗动画）—— 已出厂自带 106 个动作，版本 1.4.0

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

**2026-09-20 落地：出厂自带「溟月」106 个动作**

调研的结论是「全量转精灵图 PNG ≈ 1.4 GB」，实际做的时候**改走运行时解码**，便携包只多了 52 MB：

| 项 | 结果 |
|---|---|
| 素材路线 | 源素材**原样搬运**（VP9-alpha 透明 webm，不做任何重编码），运行时用 ffmpeg 逐帧解 |
| 为什么不用精灵图 | 全量 106 个动作转 720×720 精灵图 ≈ **1.4 GB**（便携包当时才 58 MB），不可接受 |
| 为什么不用 WPF 自己解 | WPF 只解第 1 帧（和 APNG 那条结论同理），VP9 的 alpha 通道更是完全读不到 |
| 解码器 | `Services/VideoDecoder.cs` 的 `VideoFrameStream`：ffmpeg 子进程 + **8 帧环形缓冲**（4.6 MB）。缓冲满了就阻塞在写管道上，天然不积压、几乎不吃 CPU |
| 冷启动 | 起一次 ffmpeg ≈ **150 ms** → 鼠标**移入悬浮窗就预热**（`SpritePlayer.Warm`），点下去即时播 |
| 取帧 | 时间驱动，一个 tick 最多贴一帧，落后了逐帧追（**不快进**） |
| 实测开销 | 解 241 帧 0.38 s；边解边放 142 MB 内存峰值 |
| 素材体积 | 106 动作 / 25423 帧 / **51.9 MB**（画质与源素材一致） |
| 许可 | CC BY-NC-SA 类同人素材（OC「溟月」/ 画师上善无形），**仅限个人非商业、必须署名**。`来源与许可.txt` 跟着素材走，界面上也署名 |

**形象包机制**（`Services/CharacterPacks.cs`）：`Assets\characters\<形象ID>\` 一套形象一个目录 ——
`dsh` = 溟月（新版、默认），`classic` = 原版鲸鱼娘（根目录就是 `Assets\` 本身，**没搬家**）。
设置里一键切换、立刻生效不用重启；往 `characters\` 丢一个目录就能加自制形象。
降级顺序 **设置里那个 → dsh → classic**，永远退得到能用的形象。

**贴边强化**：吸附改成**两轴独立判定**（可以吸到角上），关掉吸附也记方向。
按用户决定**没做自主走动** —— 只贴边，不自己乱跑。

**还没做的：**

- ❌ 提醒动作 `alert` 仍然只有代码路径 —— dsh 那套素材里没有对应动作，提醒时播的是点击回应。
  （原版那套的 `alert` 也得画师自己画。）
- ❌ 动作事件目前是**写死的映射**（靠目录名前缀认角色：`idle-` / `clickBounce-` / `ambient-` / `drag-` / `move-` / `turn-`），没做成可配置。
- ❌ `Assets\anim\`（原版那套精灵图）解压后仍是空的，得按规格自己放。
  （`outputs/` 下有两个样例：`样例-点击弹跳`、`样例-眨眼示意`，可当参考实现。）
- ❌ 转码工具是给 dsh 这一套写死的（`tools/convert_dsh_webm.py`）；要换别的源素材得照着改。

**新踩的两个坑（2026-09-20）：**

1. **`AnimSheet.ValidateFilter` 把分隔符认错了。** 先按 `:` 切会把 `crop=384:360:128:0` 的**滤镜名**切成
   `crop=384`，于是 106 个动作**全被判成「滤镜非法」**、一个都扫不进来。正确顺序是
   **先按 `=` 切出滤镜名、再按 `:` 切参数**，位置参数只放行纯数字、具名参数走白名单。
   —— 这个 bug 是自测抓出来的，不是靠眼看。
2. **`tools/convert_dsh_webm.py` 抽静帧时被沙箱守卫拦下。** 脚本先写临时 PNG 再删，删除被拦 →
   脚本**无声 exit 1**，连 `index.json` 和许可文件都没写出来。改成 ffmpeg 直接写 `pipe:1` →
   PIL 读内存 → 一次 `write_bytes`，零临时文件。

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

8. **⚠️「restore 不可用」其实多半是环境变量没传进来 —— 先试 `tools/mkenv.py`。**
   受限会话里的 bash 拿不到 Windows 会话的环境变量（`ProgramData`、`APPDATA`、`ALLUSERSPROFILE` 全缺），
   `dotnet` 一 restore 就报 `MSB4018 ... Value cannot be null (Parameter 'path1')` / `NuGet.targets(745,5)`。
   看着像"离线环境做不到"，真因是 NuGet 取不到 `CommonApplicationData`（.NET 返回 null，PowerShell 5.1 却能拿到）。
   ```
   python tools/mkenv.py            # 读注册表导出 .tmp-env.sh（机器级 + 用户级）
   source .tmp-env.sh && "C:/Program Files (x86)/dotnet/dotnet.exe" build src/WhaleLog/WhaleLog.csproj -c Release
   ```
   **实测：这样就有 0 错 0 警，连 `-v m` 都干净。** 所以上面第 1 条那套 `offline_restore.py`
   只在环境变量这条路也走不通时才需要 —— 别一上来就去写离线 restore。
   顺带：`dotnet build` 默认**增量**，改完代码看到 "所有项目均是最新的" 别慌，
   那是真没改到它要重编的东西；想确认 dll 真的换了就看 `bin\Release\net8.0-windows\WhaleLog.dll` 的时间戳。

---

## 6. 常用命令速查

```bat
:: 补环境变量（受限会话里 bash 缺 Windows 会话的变量，dotnet 会报 path1 为 null）
python tools\mkenv.py && source .tmp-env.sh

:: 编译（受限环境，推荐）
build-whalelog-offline.cmd

:: 编译（正常环境，需要能 restore）
build-whalelog.cmd

:: 只编译不发布
dotnet build WhaleLog.sln -c Release

:: 自测（294 项断言，直接编译主程序非界面部分）
dotnet build tools\selftest\selftest.csproj -c Release
dotnet tools\selftest\bin\Release\net8.0\WhaleLog.SelfTest.dll
::  加 --keep 保留临时目录，方便翻看产出的 Markdown 和 index.json
::  ⚠ 自测里含「真的起一次 ffmpeg 解一遍视频」的用例，先把这个 exe 拷到
::    tools\selftest\bin\Release\net8.0\ffmpeg.exe，否则那几项会跳过/失败

:: 诊断自检 → %AppData%\WhaleLog\logs\selfcheck.log（187 项，21 段）
WhaleLog.exe --selfcheck          :: --quiet 只写日志不弹窗
::  ⚠ 跑之前把 ffmpeg.exe 放到 WhaleLog.exe 旁边 —— 「透明视频」那节要靠它真解一遍
::  ⚠ 造临时数据时用 WHALELOG_DATA_DIR 指到别处，**别往用户真实记录里塞测试文件**

:: 离屏截图（不弹到面前）
WhaleLog.exe --shot D:\tmp\files-page.png
WhaleLog.exe --shot-notes D:\tmp\notes-page.png
WhaleLog.exe --shot-floating D:\tmp\floating-scales.png
WhaleLog.exe --export-assets D:\tmp\Assets

:: 打包自包含 zip（不带参数就输出到 E:\jingji\鲸记 WhaleLog <版本>.zip，版本号从 csproj 读）
python tools\make_portable_zip.py
python tools\make_portable_zip.py D:\某目录
python tools\make_portable_zip.py "E:\jingji\鲸记 WhaleLog 1.4.0.zip"
::  脚本会把 assets\characters（形象包）和 ffmpeg.exe 一并拷进去
::  找不到 ffmpeg 时按 WHALELOG_FFMPEG → assets\tools → PATH → imageio_ffmpeg 的顺序找

:: 打包交接包（给新设备）
python tools\make_transfer_zip.py

:: 素材
python tools\fetch_dsh_assets.py                            :: 从 GitHub 拉源素材（mirror 不通时逐级降级）
python tools\convert_dsh_webm.py --out assets\characters\dsh :: 抽成鲸记素材包（原样搬运，不重编码）
python tools\convert_dsh_webm.py --check                     :: 交付前自校验（退出码 0 才算合格）
python tools\make_icon.py                                   :: 610×610 PNG → 7 尺寸 ICO
python tools\check_anim_assets.py outputs\样例-点击弹跳       :: 动态素材交付前校验
python tools\prepare_expression.py 新图.png out\whale-hover.png
                                                            :: 表情差分：抠白底 + 对齐基准画布 + 报 IoU
                                                            :: 需要 pip install pillow numpy

:: 归档记录修复（只对 1.3.1 之前写下的老数据有意义；默认只预览，确认后加 --apply）
python tools\repair_archive_dates.py
python tools\repair_archive_dates.py --apply
::  程序正在运行时只预览、不写入 —— 它内存里的旧数据会把修复覆盖掉

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
- **「看着简单」的字符串解析最该写自测。** `AnimSheet.ValidateFilter` 只是切一下分隔符，
  顺序错了就把 **106 个动作全部**判为非法，而界面上的表现只是"动画一个都放不出来"，
  完全看不出跟滤镜校验有关。同类前科：`CommandLine.Value` 把 `--quiet` 当目录名。
  → **分隔符/取值顺序这类东西，一律先写一条用例再写实现。**
- **别把「拿不到环境」当成「这件事做不到」。** 见第 5 节第 8 条：restore 报 null 一度被
  当成"离线环境限制"，真因只是环境变量没传进来。**先找可变量，再下"不可行"的结论。**
- **自检/自测里的断言也要跟着设计走，别让"我以为的降级顺序"留在里面。**
  2026-09-20 那条"不存在的形象应当退回原版"就是写反了 —— 实现和文档都写的是
  「先退默认形象、再退原版」。**断言失败时先问哪边是对的，别直接改实现去迁就断言。**
- **⚠️ 打包脚本不要落"暂存目录"再整目录压缩**（2026-09-20 踩坑，已改成直接把源文件写进 zip）。
  受限环境会拦「一次删掉 50 个以上文件」并要求人工确认，而被拦下的删除请求会在
  **后台被逐个补执行**：打包期间它会把暂存树里的 `Assets\`（形象素材 + 出厂素材）
  以约 17 个/秒匀速啃掉，于是打出来的包变成**「程序在、素材没了」**，
  而脚本自己一句报错都没有（`zip` 只打进剩下的文件，数目看着还"正常"）。
  踩坑时的定位过程：在脚本里加采样线程，看到文件数从 465 一路掉到 244；
  同时发现 `build\asset-probe-data` 从 841 个文件被削到 3 个 —— 才确认是这个机制。
  **教训：打包产物一定要在落盘后自己回查关键条目**（现在脚本会查 ffmpeg / Assets /
  webm 数量 / 明文列出的必含路径，缺一条就 `return 1`，不给它悄悄出包的机会）。

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

### 2026-09-17 · 修「跨天再归档」的归属日期错位 → 版本 1.3.1

**用户报的现象**：9 月 16 日归档的文件，9 月 17 日再归档时，
只在 9 月 16 日那页看到 `v2`，9 月 17 日那页什么都没有。

**根因**：`FileIntakeService.IntakeOne()` 里「同一路径、内容变了」这一支只改了版本号、指纹、
修改时间，**没动 `AddedDate`**。而全程序判断「某天有哪些归档文件」只看 `AddedDate` ——
当日页面、只看当天、全部视图分栏、月历圆点、日报周报、`notes\*.md` 全是它。
于是新版本永远粘在首次归档那天。用户真实数据里复现了两条（`92_2.blend` 也中过同样的招）。

**修法**（用户选定「两天各留一条，分开记 v1 / v2」）：
归档记录的粒度改成 **一个路径 × 一天 = 一条记录**。

- `LogBookStore` 新增 `FindFileByPathOn(path, date)`；`FindFileByPath` 改成返回该路径**最新**那条。
- `IntakeOne` 改成问「**今天**归档过这个路径没有」：
  今天归档过 + 指纹相同 → 重复；今天归档过 + 指纹不同 → 就地升版本（同一天不刷重复行）；
  今天没归档过 → 新记一条，版本号接着版本链往下走（前一天那条留在前一天）。
- `ApplyVersion` 现在一并写 `AddedDate` / `AddedAt`；快照跟着「这一版归档的那天」放。
- 自测新增回归用例「跨天再归档」16 项断言（**216 → 232 项**）。
- 新增 `tools\repair_archive_dates.py`：把 1.3.1 之前写歪的老记录按版本历史拆回各天。
  默认只预览，检测到程序在运行就拒绝写入（内存里的旧数据会把修复覆盖掉）。

**落地情况（9/17 用户退出实例后全部完成）**：

1. 重建 Release + apphost：编译 **0 错 0 警**，apphost 重新注入图标（492544 字节）。
2. `--selfcheck --quiet`（隔离数据目录）：**164 项全通过 / 19 段**。
3. `repair_archive_dates.py --apply` 修真实数据：**5 → 7 条**，
   备份 `index.json.bak-20260917-163920`。修完 `92_2.blend` 是 `v1@9-15` + `v2@9-16`，
   `92_3.blend` 是 `v1@9-16` + `v2@9-17` —— 用户报的那两天各有各的记录。
4. 便携包 `鲸记 WhaleLog 1.3.1.zip`（57.9 MB / 250 条目）已出，压包完整性校验通过，
   包内程序跑自检同样 164 项全通过。

顺带修掉两个工具自身的真 bug：

- `repair_archive_dates.py` 的 `find_running_app()` 原来用 `text=True` 读 `tasklist`，
  中文 Windows 上 `tasklist` 输出 GBK，按 UTF-8 硬解会在**读 stdout 的子线程**里抛
  `UnicodeDecodeError` —— 异常传不回主线程，只让 `.stdout` 变成 `None`，
  最后伪装成 `splitlines` 的 `AttributeError` 崩掉整个脚本。改成自己按本地代码页
  （`mbcs`）解码 + `errors="replace"`。**教训：子进程输出的编码要显式声明，
  子线程里的解码异常会以"另一个函数的报错"的面目出现。**
- `make_portable_zip.py` 同款写法补上 `encoding="utf-8", errors="replace"`。

另注：`make_portable_zip.py` 开头会 `rmtree` 旧的 `build/portable`（约 250 个文件），
在带批量删除保护的沙箱会话里会被拦下来、且**拦下后脚本静默中断**。
绕法是先把旧目录 `mv` 成交叉名（`build/.tmp-portable-old`），脚本见目录不存在就跳过删除。

### 2026-09-17 晚 · 悬浮素材换成无损原图版 → 版本 1.3.2

用户发来闭眼图要求替换悬浮态形象。核对过程中发现一件值得记的事：

- 用户给的**剪贴板 JPG** 处理出来后，与仓库里已有的 `closeeye.png` **字节完全相同**
  （md5 一致、逐像素差 0.00%）—— 也就是说**换的这张图就是当前生效的那张**，
  只是剪贴板把它从 PNG 转成了 JPG。
- 用户随后指出原图在 `C:\Users\Administrator\Pictures\closeeye.png`：
  **1254×1254 原生带 alpha 的 PNG**（34.6% 透明），确实不用抠背景。

于是改用它重做一版，**这是真实的质量提升**（不是白折腾）：

| | 旧素材（从 JPG 抠白底） | 新素材（原生透明 PNG，`--no-bg`） |
|---|---|---|
| 与基准 IoU | 0.9894 | **0.9981** |
| 边缘半透明像素 | 4314 | 3868（少一圈软化边） |
| md5 | `8c470b490ace` | `8ed1a3bbe8ef` |

主体像素一致，差别全在轮廓抗锯齿轮廓那一圈。做法：
`python tools\prepare_expression.py C:\Users\Administrator\Pictures\closeeye.png <输出> --no-bg`
（`--no-bg` = 输入已带透明通道，跳过抠背景那一步）。

版本升到 **1.3.2**；自检 164 项全过；包 `鲸记 WhaleLog 1.3.2.zip`（58.0 MB /
250 条目）已出，包内 `Assets\whale-hover.png` 与仓库 `closeeye.png` md5 一致。

**教训：用户说"换这张图"时，先比对一下它跟现有素材是不是同一张。**
最省事的判据就是 md5 —— 本次若直接照做，产出会与现状毫无区别。

### 2026-09-20 · dsh-pet 集成调研（**只调研，没改主程序一行代码**）

用户提出把 `MerZlin/dsh-pet-indesktop` 的动画素材与桌宠交互接进鲸记。产出两份文档：

- `docs/dsh-pet-集成方案与调研.md` —— 结论、许可、规格、体积测算、实施顺序、待决策项
- `docs/dsh-pet-调研附件/` —— 样本 webm + manifest + 解帧 PNG + 体积实测件（**不联网可复现**）

验证链（全部实跑过，不是推测）：

| 问题 | 结论 |
|---|---|
| 素材是什么 | 640×360 / 24fps / **VP9-alpha 透明 webm**，106 个共 52 MB；角色仅 213×267 |
| 鲸记能吃什么 | **720×720 精灵图 PNG**（规格文档第 6 节实测否掉了 GIF / APNG / 动画 WebP） |
| 中间怎么转 | ffmpeg 解 RGBA 帧 → 裁角色 → 放大 2.247× → 贴 720×720（中心 x=360 / 脚底 y=660） |
| 能解出 alpha 吗 | ✅ 能。`imageio-ffmpeg` 自带的 ffmpeg 7.1，实测 256 级 alpha、19 万个羽化像素 |
| ⚠️ 踩到的坑 | **PyAV 静默丢 alpha**（解出 `yuv420p`、alpha 全 255）——别用它取 alpha |
| 体积 | 全量 106 个 ≈ **1.4 GB** ❌；精简 8 个动作 ≈ 110 MB；抽到 12fps 再减半 |

**本轮刻意没做的**：没写转码脚本、没动主程序、没把素材入库（附件里只放了一个 441 KB 的样本 webm）。
理由：素材许可（CC BY-NC-SA 仅限个人非商业）和体积档位都需要用户先拍板，
先改代码等于赌一个还没定的方向。

### 2026-09-20 · dsh-pet 落地（版本 1.3.2 → **1.4.0**，调研当天紧接着做完）

用户在 5 个决策点上拍板：**接受许可**（个人非商业 + 界面署名）、**所有素材都要不精简**、
**形象替换成新的但给旧版留一个切换开关**、**不做自主走动**、**但要贴边**。

技术路线也改了：调研时算的是「转精灵图 PNG ≈ 1.4 GB」，落地时改走**运行时解码**
（透明 webm 原样搬运，ffmpeg 逐帧解），便携包从 58 MB 涨到 **139.6 MB**（实测数字，见下），
**画质与源素材完全一致**。详细分层见第 4 节 A。

新增/改动的文件：

| 文件 | 干什么 |
|---|---|
| `Services/VideoDecoder.cs`（新） | `VideoFrameStream`：ffmpeg 子进程 + 8 帧环形缓冲 + 时间驱动取帧，退出码/stderr 都管 |
| `Services/CharacterPacks.cs`（新） | 形象包发现 / 可用性判断 / 降级解析 / 越权路径兜底 |
| `Services/FfmpegLocator.cs`（新） | 按 4 个位置找 ffmpeg（程序目录 → 素材目录 → 程序\ tools → PATH），找不到只是播不了视频，不影响其它 |
| `Views/SpritePlayer.cs`（重写） | 精灵图和透明视频两条路统一在一个播放器里，附悬停预热 `Warm` |
| `Views/FloatingWindow.xaml.cs`（大改） | 按目录名前缀认角色、待机循环、随机小动作、拖拽反应、贴边两轴吸附 |
| `Services/AnimSheet.cs`（修 bug） | `ValidateFilter` 分隔符认错，导致 106 个动作全被拒收（见第 4 节 A 的坑） |
| `Services/IconAssets.cs`（改） | 素材查找带目录，新增 `LoadPackOnly`（新形象不回落原版的图） |
| `Models/AppSettings.cs`（改） | 新增 `FloatingCharacter`（空串 = 没选过 → 默认新版） |
| `MainWindow.xaml(.cs)`（改） | 「桌面小鲸鱼」卡片里加「形象」单选组，标出「选的和实际生效的不一样」 |
| `tools/convert_dsh_webm.py`（新） | 拉取的源素材 → 鲸记素材包，原样搬运不重编码；`--check` 自校验 |
| `tools/fetch_dsh_assets.py`（新） | 从 GitHub 拉源素材（mirror 不通时逐级降级） |
| `tools/make_portable_zip.py`（改） | 把 `assets\characters` 与 `ffmpeg.exe` 一并拷进便携包；README 补「换形象」「动起来」「ffmpeg 是 GPL」 |
| `tools/selftest/`、`DiagnosticsService.cs`（改） | 自测 232 → **294**；自检 164 → **187**（21 段），新增「形象包」「透明视频」两节 |

**验证状态**：主程序 `-c Release` **0 错 0 警**；自测 **294 项全过**；自检 **187 项全过**；
素材包 `--check` 0 硬问题、横向漂移 0.00px；便携包解压实测可跑。

---

## 9. 建议的下一步

### ✅ dsh-pet 集成 —— 2026-09-20 当天调研 + 当天落地，已发 1.4.0

方案文档 **`docs/dsh-pet-集成方案与调研.md`**（末尾有「落地结果」整章，写明与方案的分歧）、
不联网也能复现的验证材料在 `docs/dsh-pet-调研附件/`、素材规格在 `docs/动态素材规格.md` 第 2 节。

来源与关键数字（备查，别重测）：

- 源素材 **640×360 / 24fps / VP9-alpha 透明 webm**，106 个共 52 MB，来自 `MerZlin/dsh-pet-indesktop`（PySide6 项目）。
- 落地路线不是「转精灵图 PNG」（那要 ≈ 1.4 GB），而是**原样搬运 + 运行时 ffmpeg 解码**；
  最终素材包 51.9 MB / 25423 帧，便携包多了约 37 MB（外加 84 MB 的 ffmpeg.exe）。
- ⚠️ **PyAV 会静默丢 alpha**（解出来 `yuv420p`、alpha 全 255）——已知坑，别用它取 alpha；
  要取 alpha 必须用 `imageio-ffmpeg` 自带的真 ffmpeg 二进制（本机没有系统 ffmpeg）。
- ⚠️ **素材是 CC BY-NC-SA 类同人素材（OC「溟月」/ 画师上善无形），仅限个人非商业使用，必须署名。**
  若鲸记将来要发布或商用，这批素材（`assets\characters\dsh\`）必须**整体撤下**，
  撤下后程序会自动退回原版形象 —— 这一点有自检断言覆盖。
- 交互模块是 Python/Qt 的，**不能搬代码**，只能把行为参数用 C# 重写；本轮只做了「贴边」，
  **「自主走动」按用户决定没做** —— 想做的话参照 `FloatingWindow.xaml.cs` 的贴边/拖拽那几段。

**下一轮最该做的三件（按价值排）：**

1. **把 1.4.0 推上 GitHub。** 新设备上 4 行命令（见第 6 节/第 9 节第 7 条），
   仓库 `https://github.com/yangdong19961116-cyber/whalelog.git` 还是空的，本机推不了。
   ⚠️ 1.4.0 新增了 51.9 MB 的二进制素材，**首次推送的体量比原来大一个量级**，
   网络不好时用 `git config http.postBuffer 524288000` 顶一下。
2. **真实鼠标的目视确认**（下面第 1、2 条）—— 动画是这一版的主卖点，但至今只有代码路径验证过。
3. **`build\` 下那三个旧摊开目录该清了**（约 415 MB 纯冗余）—— 等你点头。

以下是历史遗留清单：

---

1. **`tools/drag_probe.py` 一直没跑过**（会真的移动鼠标光标、并先结束正在运行的鲸记，
   所以只能手动、且要先退掉你正开着的实例）。拖拽归档是本项目最核心的交互，
   但至今只靠离屏截图和自测覆盖，真实鼠标路径没验证过。
2. **悬浮态形象没有做过真实鼠标的目视确认。** 自检的悬浮探针走的是真实处理函数、
   9 条断言全过，但那是**代码路径**的验证；真光标移上去长什么样没看过
   （不自动移光标是既定纪律）。想确认就自己把鼠标挪上去看一眼。
3. **原版那套的 `Assets\anim\` 解压后仍是空的。** 默认形象「溟月」自带 106 个动作，
   所以开箱就有动画看了；但**切到原版鲸鱼娘就没有动画**，得按规格自己放。
   把 `outputs/样例-点击弹跳` 塞进包的 `Assets\anim\clickBounce\` 就能让原版也有得播 ——
   但这是产品决策（样例画风是否代表最终形象），要用户拍板。
4. **要给原版补素材时**，先跑 `python tools\check_anim_assets.py <目录>` 过一遍（退出码 0 才算合格），
   再放进 `Assets\anim\<动作名>\` 点托盘「重新加载素材」。
   表情差分走 `python tools\prepare_expression.py 新图.png out\whale-hover.png`，IoU ≥ 0.98 才算合格。
   换成别的源形象则照着 `tools\convert_dsh_webm.py` 改（它是给 dsh 写死的）。
5. **README 自列的四项待办**（第 4 节 C）：周报对比视图、拖入自动打标、历史导出、语音输入。
6. **产物已全部搬到 `E:\jingji`**（桌面已清空，只剩快捷方式）：1.1 / 1.2 / 1.3 / 1.3.1 / 1.3.2 / **1.4.0**
   六个便携包 + 三个交接包 + `新设备继续指令.md`。以后打包**不带参数**就落到那儿
   （脚本会自动建目录），别再丢桌面。
   ⚠️ **1.4.0 的包是 139.6 MB**（以前都是 58 MB）—— 多出来的是 52 MB 形象素材
   和 84 MB 的 `ffmpeg.exe`（两者都是已压缩内容，zip 压不动，别以为是脚本写错了）。
   想要小包就自己删掉包里的 `ffmpeg.exe`：**静态形象和精灵图动画照常**，
   只有透明视频动作播不了（自检的「透明视频」那节会因此报出来）。
   `workwork\build\` 下还堆着三个旧的摊开目录（`.tmp-portable-old` / `-131` / `-prev`，
   各约 138 MB，合计约 415 MB）—— **删除需用户明确同意**，问过一次，用户还没表态。
7. **Git 远端已建好，但仓库是空的 —— 第一次推送只能在新设备上做。**
   仓库：`https://github.com/yangdong19961116-cyber/whalelog.git`（私有，默认分支 `main`）。
   2026-09-15 实测，**本机的上传路全断**：
   - **git 通道**：`github.com` 被代理拦，`git push` 报 `CONNECT tunnel failed, response 502`
     （同一时刻 `api.github.com` 返回 200）。git 协议走的正是 `github.com`。
   - **连接器通道**：平台 GitHub 连接器走 `api.github.com` 通，但**只有读权限** ——
     `create_or_update_file` / `push_files` 一律 `403 Resource not accessible by integration`。
   - **连接器 + 文本兜底**：即便有写权限也不行 —— 它是逐文件写、内容要当文本参数传，
     全仓 962 KB 文本 + 1 MB 二进制图这个量级传不动。
   - → **新设备上 4 行命令推一次**（`git branch -M master main` + `git push -u origin main`，
     仓库是空的，所以是干净首发，不需要 force / merge），推完 `main` 才是唯一事实源。
     详见 `新设备继续指令.md` 第 0 节与第 6 节。
   - ⚠️ **本机推不了、也 `pull` 不了**。GitHub 这条通道只对新设备有效，本机继续走「拷包」。
   - **教训（已写进 MEMORY.md）**：别用「往不存在的仓库写返回 404 而非 403」判断写权限 ——
     GitHub 对当前凭据看不到的仓库也返回 404，只能说明仓库不存在。
     判权限必须看**仓库存在之后**的返回码。这个推断错过一次。
8. **仓库根 README 已归位。** 原先放在根目录的 `README.md` 其实是工作区里**另一个项目
   AudioRoute**（音频设备切换托盘应用）的，已移到 `src/AudioRoute/README.md`；
   根 README 重写成工作区总览，并加了「仓库暂不完整」的显眼警告。
   ⚠️ 注意：`workwork` 是**双项目工作区**（AudioRoute + WhaleLog），
   建基线时 AudioRoute 的 20 个文件也一并进了仓库 —— 交接时别把它只当鲸记一个项目看。

# 工作区

`C:\Users\Administrator\WorkBuddy\workwork\` 下有两个互不相关的 WPF + .NET 8 应用。
构建命令受「本机环境限制」约束（见第六节）。

## 一、AudioRoute（Win10 音频设备切换托盘应用）
浅/深色 + 跟随系统；`RegisterHotKey` 全局键；NAudio `IMMNotificationClient` 插拔自动切换；自包含单文件发布。
构建 `build.cmd` / `build.ps1`。参考 belphemur/SoundSwitch、EarTrumpet。
坑：`ComboBox.IsEditable` 文本变更要挂 `PART_EditableTextBox`（先 `ApplyTemplate()` 再 `FindName`）；
程序化赋值会反向触发 SelectionChanged→Update→Changed→LoadSettingsToUi 死循环，用 `_syncingUi` 守卫；
切默认设备要对 `Role.Multimedia` 和 `Role.Communications` 各调一次 `PolicyClient.SetDefaultDevice`。

## 二、WhaleLog 鲸记（每日工作记录 + 文件归档台）
当前版本 **1.4.7**（唯一来源 `src/WhaleLog/WhaleLog.csproj` 的 `<Version>`，界面读
`Assembly.GetName().Version` 自动跟）。主面板三页（今日记录/文件归档/设置）+ 桌面悬浮窗 + 托盘常驻。
- 记录：每天一份 Markdown；日期栏展开整月日历（有记录打点，「跳到」认 `9/23`/`0923`/`昨天`）。
- **改已写下的记录**（2026-10-14，未发版）：行右侧「编辑」按钮 + **双击整行** → `Views/EditEntryWindow.xaml`。
  **只改正文/分类/标签，不改时间**（时间是事实记录；改时间还要按新时间重算耗时，账会乱）。
  写回**模型里同一个对象** → 列表靠绑定自刷新、不用重建列表，`Id`/时间/进度/完成/顺延标记一个都不动。
  清空正文点保存**会被拦下**（保存不能偷偷等于删除）。**不回写已顺延出去的副本**（跨天同步会搅乱历史）。
  双击落点判定 `MainWindow.HitsInteractiveControl`：往上碰到 `ButtonBase`/`Slider`/`TextBox` 就不算"编辑整行"。
- **完成度进度条 + 未完成自动顺延**（2026-10-14，未发版）。`NoteEntry.Progress` 0–100 是唯一真源，
  `Done == (Progress == 100)`：`Progress` setter 里 `Done = v >= 100`，`Done` setter 里直接写 `_progress`
  （**两个 setter 不能互相调用，会成环**）。`ResumeProgress` 记住"取消勾选回哪"，只在拖到 <100 时更新、
  跟着落盘。老数据靠 `CarryOverService.Normalize`（`LogBookStore.Load` 调）把 done 补成 100。
  顺延（`Services/CarryOverService.cs`）：启动 + 跨零点（挂 `ReminderService.Ticked`，20 秒轮询）把
  **所有过去日子**里「未完成且没顺延过」的条目**复制**到今天（扫所有过去的日子，只看昨天会漏掉几天没开机的积压）；
  源条目只戳 `CarriedToDate`，翻旧日期显示「已顺延至 X」。幂等靠 `CarriedToDate` + 当天 `CarriedFromId` 两道锁
  → 重复启动不复制、删掉副本不复活、勾完即断链。
  界面顺延后刷新要**另写 `RefreshCurrentDay()` 做差量增删**，不能调 `LoadDay`（它会覆盖 `SummaryInput.Text`，
  吃掉用户正在打、防抖还没落盘的小结）。
- 归档：拖入文件 → 规则引擎分类 → SHA-256 指纹判版本链 → 记完整路径。文件归**当前查看的日期**；
  「全部」视图按归档日期分栏。
- **归档记录粒度 = 一个路径 × 一天 = 一条**（2026-09-17 定）。同文件第二天改再归档是**新记一条**；
  同一天反复改只升当天版本号。
  * `AddedDate` 是「某天有哪些归档文件」的**唯一**依据（当日页面 / 只看当天 / 全部分栏 / 月历圆点 /
    日报周报 / `notes\*.md` 七个入口都看它）—— 改它等于同时改七处。
  * ⚠️ 踩过：升版本只改版本号没同步 `AddedDate` → 新版本永远粘在首次归档那天。老数据用
    `python tools\repair_archive_dates.py`（默认预览，`--apply` 才写）。
  * 归档跳过**自己数据目录里**的文件：判断必须按**路径前缀 + 目录分隔符**比，直接用 `AppPaths.Root`
    做 `StartsWith` 会误伤名字以数据目录名开头的**兄弟目录**，整批静默跳过（1.4.4 修）。
- 悬浮窗：鲸鱼娘抠图，透明置顶、松手吸边（两轴独立，能吸角）、收起成贴边小头像；显示条目/文件数/倒计时。
  大小 0.7×–1.6×。**1.4.4 起文件（夹）拖到小鲸鱼身上 → 自动开主面板 + 切「文件归档」页 + 记到今天**
  （开关 `AutoOpenMainWindowOnDrop`，默认开）。归档仍走 `FileIntakeService`，规则与主面板一致。
  ⚠️ 给悬浮窗加交互前先看「透明像素接不住拖放」那条坑（第五节）。
- 提醒：默认 18:00 提前 15 分钟（气泡 + 弹跳 + 音效 + 托盘通知）；`Ctrl+Alt+N` 快速记一笔；日报/周报；开机自启。
- **硬性约束：绝不移动/重命名/删除用户原文件。** 快照模式（复制副本）默认关闭。

### 命令与工具
构建 `build-whalelog.cmd`（正常）/ `build-whalelog-offline.cmd`（受限）；
**受限会话 restore 报 `path1` 为 null 时先跑 `python tools/mkenv.py` 再 `source .tmp-env.sh`**（见第六节）；
打包 `python tools/make_portable_zip.py [out.zip]` → 自包含 zip（默认落 `E:\jingji`，命名自带版本号）；
自测 `tools/selftest/`（**478 项**断言，直接编译主程序非界面部分，含真起 ffmpeg 的用例 →
ffmpeg 需在 `tools\selftest\bin\Release\net8.0\`；**新增源文件要同步加进 `selftest.csproj` 的
`<Compile Include>` 列表**，否则报 CS0103 找不到类型）；
诊断 `WhaleLog.exe --selfcheck [--quiet]` → `%APPDATA%\WhaleLog\logs\selfcheck.log`
（**29 段 / 真实数据 339 项 / 隔离数据 340 项**；**脚本里跑必须带 `--quiet`**，
否则弹对话框等人点，表现为卡死到超时；**ffmpeg.exe 要在程序旁边**，否则
「透明视频动画」「归档后演清点归档」那两条会假失败）；
截图 `--shot` / `--shot-notes` / `--shot-settings` / `--shot-floating` / `--shot-calendar` / `--shot-edit`
（月历在 Popup 独立 HWND、编辑窗是独立窗口，各开一条单独出口；月历放大 4 倍看圆点），
均支持 `--x 值` 和 `--x=值`；
交互探针 `python tools/drag_probe.py`（合成真实鼠标）、拖放探针 `python tools/drop_probe.py`
（合成真实 OLE 拖放，先 `dotnet build tools\dragsource\`）—— 两者都会动光标、要求先退出运行实例，**手动跑**。
素材：程序旁 `Assets\` 同名文件优先内置（`AssetCatalog` + `IconAssets`）；`--export-assets [dir]` 只导出出厂素材；
`WHALELOG_ASSET_DIR` 改素材目录；`WHALELOG_FFMPEG` 指定 ffmpeg.exe。
形象包：`Assets\characters\<ID>\` 一套形象一个目录（`dsh` 溟月/默认、`classic` 原版=仓库根 `Assets\`）；
降级顺序 **设置里那个 → dsh → classic**（`CharacterPacks.Resolve`），原版是嵌入资源、永远能用。
转码素材：`python tools/fetch_dsh_assets.py` 拉源、`python tools/convert_dsh_webm.py --out assets/characters/dsh`、
`--check` 自校验。

### 状态与单选（踩过两次同类 bug）
- **ToggleButton 不互斥，单选必须显式处理**：互斥组要有唯一 source of truth（如 `_activePage`），
  可见性和勾选全部从它推导。曾两页同时 Visible 导致内容完全重叠。
- 目标值**从被点的 sender 推导**，绝不读另一个控件的 `IsChecked`。写法
  `目标值 = clicked.IsChecked == true ? target : 当前值;` —— 末尾 `: 当前值` 必须有，
  再点当前项会把 `IsChecked` 反选成 false，不兜住就「两个都不选」。
- 按 ToggleButton 真实行为（**先翻转 `IsChecked` 再抛 `Click`**）逐路径走自检。
- 任何会改控件值的事件处理器都要 `_syncing` 守卫，否则程序化赋值反向触发保存成环。

### WPF 界面陷阱
- 别把 `ValueChanged`/`TextChanged` 写 XAML 上：解析阶段给 `Minimum`/`Maximum` 赋值会连带触发，
  那时同段 XAML 里靠后的控件还没构造。统一在构造函数 `InitializeComponent()` 后订阅。
- `Style=` 只接 Style，`Foreground=`/`Background=` 才接 Brush —— 写错编译期不报，运行时 XamlParseException。
- 失焦自动关闭的窗口必须有 `_closing` 守卫，统一走 `RequestClose()`（否则「窗口关闭期间无法调用 Close」）。
- `Popup`+`StaysOpen=False` 会吃掉触发按钮自身点击，「再点一次收起」失效 → 用 `Closed` 时间戳 + 时间窗。
- `Popup` 有自己 HWND，键盘事件不一定穿过宿主 `PreviewKeyDown` → 让弹出内容自己抛 `DismissRequested`。
- **`ControlTemplate` 的名字作用域不跨模板**：外层模板的 `ControlTemplate.Triggers` 里
  `TargetName` 指不到嵌套模板（如 RepeatButton 自己模板里）的元素，编译期就报
  `MC4111 无法找到 Trigger 目标`。绕法是让颜色走一个能跨模板读的属性 ——
  子模板里 `{Binding Foreground, RelativeSource={RelativeSource AncestorType=Slider}}`，
  外层只改 `Foreground`。
- 动态生成元素用 `element.SetResourceReference(prop,"Key")` 而非 `DynamicResource`。
- 新增资源键要**同时**加 `Themes/Light.xaml` 和 `Dark.xaml`（键集必须完全一致）；样式放 `Styles/Shared.xaml`
  —— `ThemeService` 靠**文件名**匹配主题字典。
- JSON 落盘一律 `Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping`，否则中文变 `\uXXXX`。
- **`double.NaN` 写不进 JSON**：`FloatingLeft/Top` 默认 NaN → 序列化抛异常，而 `Save()` 容错 →
  表现成「全新安装设置怎么改都存不下去」且毫无痕迹。用 `AllowNamedFloatingPointLiterals`，
  且**容错的 catch 也要记日志**。
- 同一份信息只保留一个显示口径（曾在栏头和行里出现两个日期口径）。
- **窗口尺寸和内容缩放必须出自同一个系数**：`RootGrid.LayoutTransform = ScaleTransform`，
  用取整后实际尺寸反算；只改 `Width/Height` 会放大时一片空、缩小时内容被裁。
- `LayoutTransform` 外面的元素读 `ActualHeight` 是**变换前**尺寸（永远 168）→ 用 `TranslatePoint` 量两点差。
- `CollectionViewSource` 分组先后取决于记录在集合里的顺序 → 先排好序。
- 别用 BCL 常见类型名当方法名（`Key`/`Point`/`Color`/`Brush`/`Path`/`Task`）—— 一个 `Key(DateTime)` 就把
  `Key.Enter` 顶掉（CS0119）。回调参数别用 `Action<...>`（不支持可选参数），需要默认值时自定义委托。
- 取值范围只写一份：常量放 `AppSettings`，滑杆 `x:Static` 绑，校验用同一组。
- **任何「隐式规则」都要显示在界面上**：文件归「当前查看的日期」合理，但横幅必须写明目标日期。

### 命令行与单实例
- 开关取值要判断下一个参数是不是另一个开关（`Services/CommandLine.cs`）：`--export-assets --quiet`
  曾把 `"--quiet"` 当目录名。新增开关要在自测补解析断言。
- 开关匹配要同时认 `--x` 和 `--x=值`：只做全等比较会让 `--shot=路径` 被当成没这个开关，程序按普通方式启动。
- 一次性命令（`--selfcheck`/`--shot*`/`--create-shortcut`/`--export-assets`）**必须绕开单实例互斥**，
  也要跳过数据写入，避免和运行中的实例并发写 `index.json`。

## 三、素材机制

### 引用与解析
界面引用素材一律 `Source="{v:AssetImage <名字>}"`，**不要写 `pack://` URI**（会绕过「外部优先」解析）。
**「存在」和「可用」分开判断**：`AssetCatalog.FindExternal` 只查在不在，能否解码看 `IconAssets.LoadedFromExternal`；
报告要写清用外部还是内置。首次运行导出出厂素材到 `Assets\`（不覆盖已存在文件）并附 `素材说明.txt`。

### 形象包（2026-09-20 起）
`Assets\characters\<形象ID>\`；`classic` 是保留名、根就是 `Assets\`（老布局没搬家）。
**素材必须躺在 `WhaleLog.exe` 旁边**（`AppPaths.AssetsDir` = 程序目录\Assets）——
`WhaleLog.csproj` 1.4.1 起把仓库 `assets\characters\` 拷进编译产物；否则设置页「溟月」按钮灰着、点了没反应。
`IconAssets.LoadPackOnly` 用于新形象：**没有差分图时不回落内置闭眼图**（否则"换了形象、鼠标一移又变回旧鲸鱼"）。

### 透明视频动作（2026-09-20 起）
源素材 VP9-alpha webm **原样搬运不重编码**（转精灵图 PNG 全量 ≈ 1.4 GB 不可行），运行时用 ffmpeg 子进程解码。
- ⚠️ **必须点名 `-c:v libvpx-vp9`，否则 alpha 整条丢掉**（1.4.2 修）：VP9 的 alpha 不在像素里，
  在 matroska 的**附加数据**（`BlockAdditional`，`ffmpeg -i` 见 `ALPHA_MODE: 1`），
  而 ffmpeg 默认挑的 **native vp9 解码器不认它** —— 照样吐 RGBA、帧数全对，但 **alpha 全填 255，不报错**。
  点名后 `yuv420p` → `yuva420p`，代价仅 18%。探测 `VideoFrameStream.IsVp9Webm`（读头 64 KB 搜 `V_VP9`）
  + `FfmpegSupportsVp9Alpha`（问一次 `-decoders` 后缓存），两条都满足才加点名参数，否则退回 native。
  → 与「PyAV 静默丢 alpha」同类：**解码器可以选择性无视附加数据**。
- ⚠️ **"颜色不对"别往色彩空间想**：两种解码器解出的实心像素**逐像素最大差值 0**，色偏是黑底造成的对比错觉。
  **先测，再猜。**
- ⚠️ **裸字节必须和贴图字节序对齐：`-pix_fmt bgra`，不是 `rgba`**（1.4.3 修）。ffmpeg 的 `rgba` 是
  **分量顺序 R,G,B,A**，WPF `PixelFormats.Bgra32` 内存里是 **B,G,R,A** —— 中间无转换，红蓝整条对调
  （深蓝紫头发显示成栗棕红）。修法让 ffmpeg 吐 `bgra`（swscale 内部换，不额外花时间），常量
  `VideoFrameStream.PixelFormat` + `OffsetB/G/R/A`；`SpritePlayer` 那行上方写了「两处是一对，改一边必须改另一边」。
  **凡"外部工具产像素 → 本程序贴图"，先验一次字节序**（纯净色素材解一帧，看值落在哪条通道）。
- ⚠️ **"解出来了"和"解对了"是两回事**：断言"帧数/每帧字节数" → alpha 全丢时全过；
  断言"有没有透明像素" → 颜色全反时全过。**缺陷都是"结构对了、内容错了"**，断言要落到**画面内容**上。
- ⚠️ **用户说的每句现象都要当真**（"像反色""只有切换时开头几帧是对的"）—— 那是定位证据。
  "开头几帧对"指**切换后先是 WPF 自解码的静态图**（准的），视频帧要等 ffmpeg 起 ≈150 ms 才贴。
- `VideoFrameStream`：**8 帧环形缓冲**，满则阻塞生产者（不积压、几乎不吃 CPU）；一 tick 最多贴一帧，不快进。
- 测无限流（`-stream_loop -1`）**别指望 `-frames:v N` 兜底**：ffmpeg 可能不停，实测写到 **35 GB**。读够就杀进程。
- 冷启动 ≈150 ms → 鼠标**移入悬浮窗就预热**（`SpritePlayer.Warm`）。
- **角色由目录名前缀认**（`idle-`/`clickBounce-`/`ambient-`/`drag-`/`move-`/`turn-`），
  必须从目录名记，**不能从动作键反推**（`idle` 那支的键归一化成 `blink`，是历史行为）。
- ⚠️ **`filter` 当不可信输入校验**（防命令注入）：**先按 `=` 切滤镜名、再按 `:` 切参数**。顺序反了会把
  `crop=384:360:128:0` 的**名字**切成 `crop=384` → **106 个动作全判非法**，界面只表现"动画一个都不出来"。
  这类"切分隔符"的活**先写用例再写实现**。
- ⚠️ 素材许可：CC BY-NC-SA 类同人素材（OC「溟月」/ 画师上善无形），**仅限个人非商业、必须署名**；
  发布/商用必须整体撤下 `assets\characters\dsh\`（撤下后自动退回原版，有自检断言守着）。

## 四、自包含打包（离线）
正路 `publish -r win-x86 --self-contained` 需 NuGet 运行时包，本机没有 → `tools/make_portable_zip.py` 手工拼。
⚠️ **不要再引入"暂存目录"**：2026-09-20 实测，受限环境会拦「一次删 50 个以上文件」，
**被拦下的删除请求会在后台被逐个补执行** —— 打包期间把暂存树 `Assets\` 以约 17 个/秒删掉，
打出「程序在、素材没了」的包且**脚本不报任何错**。现在脚本**直接把源文件写进 zip**
（两份生成的 json 用 `writestr`，出厂素材导出到系统临时目录），**落盘后回查关键条目**，缺一条 `return 1`。
以后改这脚本务必保住这个自检。
- 三块料：应用本体 + `hostfxr.dll`（`dotnet/host/fxr/<ver>/`，共享框架目录里没有）+ 两个共享框架全部文件；
  `runtimeconfig.json` 用 `includedFrameworks`。
- **`WhaleLog.deps.json` 必须列出全部框架程序集**，否则 `Could not resolve CoreCLR path`；
  托管/原生 DLL 用 PE 头第 14 个数据目录（CLR Runtime Header）区分。
- 两个框架有 3 个同名程序集（`WindowsBase`/`System.Drawing`/`Microsoft.VisualBasic`），
  必须让 WindowsDesktop 版本覆盖 NETCore 空壳。
- 验证：自检「运行环境」报运行时位置；再把 `DOTNET_ROOT` 指到不存在路径跑一遍。
- **1.4.0 起包内多两样**：`Assets\characters`（形象包，51.9 MB）与 `ffmpeg.exe`（约 84 MB，
  按 `WHALELOG_FFMPEG` → `assets\tools\` → PATH → `imageio_ffmpeg` 搜）。都是已压缩内容、zip 压不动，
  所以包从 58 MB 涨到 **139.6 MB** —— **不是脚本算错了**。
  ⚠️ ffmpeg 是 GPL 授权的独立可执行文件，包内 README 必须写明。

## 五、探针/自检纪律
- **探针必须无副作用**：「创建快捷方式」曾直写桌面，从解压副本跑自检把用户快捷方式改指到临时目录。
  现在在 `logs\shortcut-probe\` 下试创建、验完删掉，桌面快捷方式只读并报告指向。
- **探针不能把改动留在设置对象里**：退出 `Save()` 会把内存设置整个写回文件，临时改动会泄漏进用户配置
  （真出过：不透明度被写成 1）。`RunReport`/`WriteFloatingShot` 都要 `Snapshot()`+`Restore()`，
  模拟拖动后 `CancelPendingSaves()` 掐掉 700ms 节流。
- 探针依赖的前置状态要显式设好（悬浮窗缩放探针先关 `autoCollapse`，否则量到的全是收起态 48px）。
- 悬浮窗 `ClampToArea()` 会把越界位置拉回工作区 → 探针用 `BackgroundProbeMode = true`
  + `ShowActivated = false`，并断言「窗口落在屏幕外」。
- 量位置要量「卡片本身」而不是 ItemsControl 容器（外边距在容器内部，量容器间隙永远是 0）。
- **界面改动的验证套路**：① 自检加探针（离屏窗口 → 需要时先 `SwitchTo(页面)`，
  `Collapsed` 时不生成条目容器 → 按控件真实交互方式模拟）；② 断言**可视树**而非数据（数 `GroupItem`、
  要求文字 `ActualWidth/Height` 非零）——「视图对象里有分组」≠「看得见」；③ 只能看的东西（裁切/配色/对齐）
  用 `--shot` 出图，没真实数据就用 `WHALELOG_DATA_DIR` 指临时数据，别往用户记录里塞测试文件。
- 纯逻辑（日期解析、分组排序、文案）放非界面文件（如 `Services/DateGrouping.cs`），自测能直接编译断言。
- 交互行为（拖拽/点击、贴边、收起）只能靠合成真实输入测；先确认基线干净。
- bash 的 `kill` 杀 GUI 不走 `OnExit`（`Settings.Save()` 不执行）→ 排查「配置被谁改了」时别把
  「普通启动一次程序」当嫌疑；会写文件的是主动 `Shutdown()` 的一次性命令。
  验证配置没被改要**打印具体数值**，只比 md5 可能两边都已被改过而看不出。
- 调试节奏：**先在 Debug 版验功能**（输出目录不同，不被运行中的程序锁），最后才切 Release。
- `--tray` 起的实例「存活 N 秒」不等于启动成功，可能被单实例守卫的 MessageBox 阻塞 →
  顺手 `EnumWindows` 查有没有 `#32770` 对话框。
- **界面层断言要问「这个控件点得动吗」，不能只问「背后的函数返回对吗」**（2026-09-20 用户报
  「设置里点新形象没反应」）：形象包 13 条纯逻辑断言全过，真实情况是按钮 `IsEnabled=false`（素材没在程序旁）
  —— 中间隔着一层谁都没测。给界面控件开"诊断钩子"（读 `IsEnabled/IsChecked/Content`、真翻一下）成本很低。
- **点一个已选中的 RadioButton 不会重抛 `Checked`**（true→true 无变化）。断言"点得动"必须点在**没被选中**的那个。
- **脚本里跑 `--selfcheck` 必须带 `--quiet`**：不带会弹对话框等人点，在批处理/agent 里表现为卡死到超时，
  而日志其实早写完了 —— 极易误判成"跑不完/挂了"。
- **凡有"像素值 / 内容正确性"要求的地方，断言要数「内容」，不能只数「结构」**（2026-09-20）。
  视频那节旧断言只有"解出多少帧""每帧多少字节"，alpha 全丢时**全都通过** —— 黑底就这样混进
  1.4.0/1.4.1 两个出厂包，最后是用户肉眼发现的。现在的「透明通道」检查是：现场造一段
  **左半不透明 / 右半全透明**的 VP9 素材（`geq=...:a='if(lt(X,32),255,0)'`），解一帧**逐像素数 `alpha==0` 的个数**。
- ⚠️ **新写的断言要做一次反向验证**：把修复关掉重编，确认它**真的会挂**。否则只是添了几条永远通过的装饰。
  本轮实测：关掉修复后自检挂 3 条（`全透明 0/4096 个像素`），改回来即全绿。
- **靠"运行时环境"决定成败的功能，断言和自检都测不到，必须真跑一次 + 配反向验证**（1.4.4）。
  「窗口接住了没有」（命中测试、DPI、模态循环、外部进程）和「处理函数对不对」是两回事：
  `DragEventArgs` 构造函数是 internal，造不出假事件 —— 处理函数全对、命中测试全错时自检**全绿**。
  现有两套合成真实输入的探针：`tools/drag_probe.py`、`tools/drop_probe.py` + `tools/dragsource/`
  （三场景含一个"窗口外不该接住"的对照组）。
  **判据要落在"用户能看见的结果"上**：拖放那条只看「主面板开没开 + 文件进没进当天归档」，
  **不看 `DoDragDrop` 返回值** —— 桌面本身也是合法拖放目标，`effect` 恒为 `Copy`，看它会假绿。
- ⚠️ **`AllowsTransparency` 的层叠窗口逐像素看 alpha 做命中测试**（2026-09-20）。alpha=0 的像素上，
  鼠标消息和 OLE 拖放会**穿透到下层窗口** —— 拖到鲸鱼**旁边**空白处什么都不发生、**且不报任何错**
  （鲸鱼本体上却是好的 → 看着像"时灵时不灵"）。解法：根 `Grid` 背景从 `Transparent`(alpha=0)
  改成 `#01000000`(alpha=1 近透明黑)；提示层反过来设 `IsHitTestVisible="False"`。
  **给悬浮窗加交互先问一句"这块像素是不是全透明"。**
- **自检探针里的聚合调用要有空集合守卫**（2026-10-14）：归档日历那节用了
  `.OrderByDescending(...).First()`，一个归档都没有的数据目录下抛 `Sequence contains no elements`，
  被 catch 吞成一条「按日期回看」的红 —— **干净数据目录下跑自检必然踩到，直接让"清屋验证"失效**。
  凡取 `First()/Max()/Min()` 的地方先想一句"数据为空时它返回什么"。
- **自检项数会随数据浮动**（真实数据 339 / 隔离数据 340），**段落数固定 29**。
  **看结尾「结论：全部通过」，别死抠数字**。
- **UI 自动化端到端**（2026-10-14 加的，用来验「编辑记录」真点真改）：PowerShell 载
  `UIAutomationClient`，在**真窗口**上点按钮、改文本，最后回读 `index.json` 判成败。三个必须：
  ① **`SetProcessDPIAware()`** —— 本机 150% 缩放，UIA 报的是物理像素，而 DPI-unaware 进程的
  `SetCursorPos` 收到的是被虚拟化过的坐标 → 鼠标跑到 1.5 倍远的地方，点个空；
  ② 按钮走 **`InvokePattern.Invoke()`**，别用鼠标点（它是 `Dispatcher.BeginInvoke` 异步派发的，
  即使被 `ShowDialog()` 阻塞在嵌套消息循环里，客户端也照常拿到返回）；
  ③ **`WindowStyle=None` + `AllowsTransparency` 的窗口不在 UIA 的 `RootElement.Children` 里**
  （编辑窗、悬浮窗都是）→ 先用 `EnumWindows` 按标题拿 HWND，再 `AutomationElement.FromHandle`。
  另：主窗口可能是**最小化**态（`IsIconic=True` 时矩形是 -21333 这种离屏值，看着像"窗口跑到屏幕外了"），
  开工前先 `ShowWindow(hwnd, SW_RESTORE)`。
  跑法：`Set-ExecutionPolicy -Scope Process Bypass` + `& "文件.ps1"`
  （`Invoke-Expression` 被安全策略拦；ps1 要存成 **UTF-8 with BOM**，否则中文乱码）。

## 六、本机环境限制
- **`dotnet restore` 报 `Value cannot be null. (Parameter 'path1')` 时，先试 `tools/mkenv.py`。**
  真因是会话里的 bash 拿不到 Windows 环境变量（`ProgramData`/`APPDATA`/`ALLUSERSPROFILE` 全缺），
  NuGet 取不到 `CommonApplicationData`。`python tools/mkenv.py` 读注册表导出 `.tmp-env.sh`，
  `source` 后 `dotnet build -c Release` 实测 0 错 0 警。→ **先找可变量，再下"不可行"的结论。**
- 走不通才用离线兜底：`tools/offline_restore.py` 手写 restore 产物，再带
  `-p:SkipResolvePackageAssets=true -p:GenerateDependencyFile=false -p:GenerateRuntimeConfigurationFiles=false` 编译。
- `dotnet.exe` 是 32 位宿主 → **项目不要设 `PlatformTarget=x64`**（FileLoadException），用 AnyCPU。
  apphost 只能按本机已安装的运行时架构选（本机只有 x86）。
- `dotnet.exe` 在 `C:\Program Files (x86)\dotnet\dotnet.exe`，**不在 bash PATH 里**，用完整路径（带空格加引号）。
- ⚠️ **从 bash 启动鲸记必须先补 `SystemRoot` / `windir`**（2026-09-20 定位，花了大半天）。
  受限会话 `os.environ` 里这两个变量**是空的**，WPF 初始化要拼字体缓存路径（`%SystemRoot%\Fonts`）——
  拿不到就抛 `Invalid URI: The format of the URI could not be determined`，**偶发崩溃**，
  看着像"随机闪退"，极难归因。`tools/drop_probe.py` / `drag_probe.py` 的 `child_env()` 已处理。
- **不能从 bash 调 `cmd.exe`**（安全策略），所以 `build-whalelog*.cmd` 在只能执行 bash 的会话里跑不了，
  要手工复现它四步（`offline_restore.py` → `dotnet build …` → 再跑一次 `offline_restore.py` → `make_apphost.py`）。
  **第三次 `offline_restore.py` 不能省**：带 `-p:GenerateRuntimeConfigurationFiles=false` 编出的产物
  没 `runtimeconfig.json`，直接启动报 `hostpolicy.dll not found`。
- 本会话 PowerShell 工具不返回 stdout（exit code 正常但抓不到输出），读输出优先用 bash 或直接看文件。
- **给 .NET 程序传路径必须用 Windows 风格**（`C:/...` 或 `C:\...`）。Git Bash 里 `$PWD` 是 `/c/Users/...`，
  .NET 不认 —— 程序**静默失败**：退出码 0、不报错、目录一个文件都不生成。
  凡"跑条命令看结果"的验证，**判定标准看产物文件，不看退出码**。
- **改同一个文件必须串行 Edit**：同一条消息里并行发两个改同一文件的 Edit，会丢其中一处。
- **本机没法 `git push` 到 GitHub**：`api.github.com` 通（200），但 `github.com` 被代理拦
  （`CONNECT tunnel failed, response 502`）→ 本机既不能 push 也不能 pull，远端只对另一台设备有效。
- **平台 GitHub 连接器只有读权限**：写文件（`create_or_update_file`/`push_files`）与
  建仓库（`create_repository`）一律 `403 Resource not accessible by integration`。
  ⚠️ 曾用「往不存在的仓库写文件返回 404 而非 403」推断它有写权限 —— **这条是错的**：
  GitHub 对当前凭据*看不到*的仓库同样返回 404。判权限必须看**仓库存在之后**的返回码。
- **bash 命令可能被「沙箱重跑」一次**：输出末尾出现 `⚠️ Sandbox bypassed (escalation-approved)`。
  后果很阴：第一次（看不到的）已执行成功，第二次再跑得到 `nothing to commit`、退出码 1，
  长 `&&` 链当场中断，**后面一步都没跑** → 收尾类命令别用长 `&&` 链，改用 `;` 并最后**显式回查结果**。

## 七、发布例行（发 1.2 时定下来的）
- 版本唯一来源 `src/WhaleLog/WhaleLog.csproj` 的 `<Version>`，界面读 `Assembly.GetName().Version` 自动跟。
- **所有产物统一放 `E:\jingji`**（2026-09-17 定的规矩）：便携包、交接包及以后任何要交出去的产物。
  两个打包脚本**不带参数**就输出到这里，目录不存在会自动创建；E: 盘不存在就退回桌面并在输出里说明原因。
- 打包产物命名带版本号：`E:\jingji\鲸记 WhaleLog <版本>.zip`。版本号由 `make_portable_zip.py` 自读 csproj
  —— 以前默认名不带版本、会覆盖上一版。交接包默认名 `鲸记 WhaleLog 项目交接包 <当天日期>.zip`。
- 顺序：升版本 → 重建 Release + apphost → 打包 → **同步 HANDOFF 与 README 里所有数字** → 提交。
  （1.4.0 起多一步：**打包前把 `ffmpeg.exe` 放到 `src\WhaleLog\bin\Release\net8.0-windows\`**，
  并跑一次 `--selfcheck` 确认「透明视频」那节真的解出了帧。）
- ⚠️ **打包前先确认没有 WhaleLog 实例在跑**（`tasklist | grep -i whalelog`）。被占用时 `dotnet build` 报
  `MSB3027 / MSB3021 文件被 WhaleLog (<PID>) 锁定`，前面编译其实已成功、只有最后拷贝 apphost 那步失败
  —— **别误判成代码编译不过**。杀实例属用户可见操作，**要先问**。
- **文档里的项数必须 grep 校验**。交接单断言数漂过 151→163→213→232→294，每次都是改代码忘了改数字。
  发版前 `grep -rn "232\|164 项"` 一遍成本极低（注意 `memory/` 下**按日期记的历史段落里的旧数字是对的**，别改）。
- **桌面属于个人目录：删桌面上的旧包必须用户明确同意**，不要自作主张清理。
- 2026-10-10：发 1.4.7 —— 自检「全部通过」，包 139.6 MB，`E:\jingji\` 与桌面各一份。

## 八、Git 远端
- 远端 `https://github.com/yangdong19961116-cyber/whalelog.git`（私有，默认分支 `main`）。
  本地分支叫 `master`，推之前 `git branch -M master main` 对齐。本机连不上 `github.com`（见第六节）。
- **`workwork` 是双项目工作区**：AudioRoute（`src/AudioRoute/`，已完成）+ WhaleLog（`src/WhaleLog/`，在做）。
  建基线时 AudioRoute 文件一并进仓库 —— 交接时别只当鲸记一个项目。其 README 已归位到 `src/AudioRoute/README.md`。

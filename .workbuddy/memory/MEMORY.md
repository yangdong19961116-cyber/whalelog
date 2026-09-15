# 工作区

`C:\Users\Administrator\WorkBuddy\workwork\` 下有两个互不相关的 WPF + .NET 8 应用。
构建命令的写法受文末「本机环境限制」约束。

## 一、AudioRoute（Win10 音频设备切换托盘应用）
浅/深色 + 跟随系统；`RegisterHotKey` 全局键；NAudio `IMMNotificationClient` 插拔自动切换；
自包含单文件发布。构建 `build.cmd` / `build.ps1`。参考 belphemur/SoundSwitch、EarTrumpet。
坑：`ComboBox.IsEditable` 的文本变更要挂 `PART_EditableTextBox`（先 `ApplyTemplate()` 再 `FindName`）；
程序化赋值会反向触发 SelectionChanged→Update→Changed→LoadSettingsToUi 死循环，用 `_syncingUi` 守卫；
切默认设备要对 `Role.Multimedia` 和 `Role.Communications` 各调一次 `PolicyClient.SetDefaultDevice`。

## 二、WhaleLog 鲸记（每日工作记录 + 文件归档台）
主面板三页（今日记录/文件归档/设置）+ 桌面悬浮窗 + 托盘常驻。
- 记录：每天一份 Markdown；日期栏展开整月日历（有记录的打点，「跳到」认 `9/23`/`0923`/`昨天`）。
- 归档：拖入文件 → 规则引擎分类（借鉴 DropIt「协议 = 条件 + 动作」，只保留条件）→ SHA-256 指纹判版本链
  → 记录完整路径。文件归**当前查看的日期**（可补录过去某天），横幅写明目标日期；
  「全部」视图按归档日期分栏，栏间从新到旧。
- 悬浮窗：鲸鱼娘抠图，透明置顶、松手吸边、收起成贴边小头像；显示条目数/文件数/倒计时。
- 提醒：默认 18:00 提前 15 分钟（气泡 + 弹跳 + 音效 + 托盘通知）；`Ctrl+Alt+N` 快速记一笔；
  日报/周报汇总；桌面快捷方式自建；开机自启。
- **硬性约束：绝不移动/重命名/删除用户原文件。** 快照模式（复制副本）默认关闭。

### 命令与工具
构建 `build-whalelog.cmd`（正常）/ `build-whalelog-offline.cmd`（受限）；
打包 `python tools/make_portable_zip.py [out.zip]` → 自包含 zip；
自测 `tools/selftest/`（163 项断言，直接编译主程序非界面部分）；
诊断 `WhaleLog.exe --selfcheck` → `logs\selfcheck.log`（真实数据 144 项）；
截图 `--shot`/`--shot-notes`/`--shot-floating`，均支持 `--x 值` 和 `--x=值`；
交互探针 `python tools/drag_probe.py`（合成真实鼠标，会动光标、要求先退出运行实例 → 手动跑）。
素材：程序旁 `Assets\` 同名文件优先于内置（清单 `AssetCatalog`，加载 `IconAssets`）；
`--export-assets [dir]` 只导出出厂素材；`WHALELOG_ASSET_DIR` 可改目录。

### 状态与单选（踩过两次同类 bug）
- **ToggleButton 不互斥，单选必须显式处理**：任何互斥组都要有唯一 source of truth（如 `_activePage`），
  可见性和勾选状态全部从它推导。曾两页同时 Visible 导致内容完全重叠。
- 目标值**从被点的 sender 推导**，绝不读另一个控件的 `IsChecked`
  （`_showAllFiles = FileFilterAll.IsChecked` 曾导致点「只看当天」不翻转）。
  写法 `目标值 = clicked.IsChecked == true ? target : 当前值;` —— 末尾的 `: 当前值` 必须有，
  再点当前项会把 `IsChecked` 反选成 false，不兜住就「两个都不选」。
- 改单选逻辑后按 ToggleButton 真实行为（**先翻转 `IsChecked` 再抛 `Click`**）逐路径走自检。
- 任何会改控件值的事件处理器都要 `_syncing` 守卫，否则程序化赋值反向触发保存成环。

### WPF 界面陷阱
- 别把 `ValueChanged`/`TextChanged` 写 XAML 上：解析阶段给 `Minimum`/`Maximum` 赋值会连带触发，
  那时同段 XAML 里靠后的控件还没构造。统一在构造函数 `InitializeComponent()` 后订阅。
- `Style=` 只接 Style，`Foreground=`/`Background=` 才接 Brush —— 写错编译期不报，运行时 XamlParseException。
- 失焦自动关闭的窗口必须有 `_closing` 守卫（`Close()` 中失活 → `OnDeactivated` 再 `Close()` →
  抛「窗口关闭期间无法调用 Close」）。统一走 `RequestClose()`。
- `Popup`+`StaysOpen=False` 会吃掉触发按钮自身的点击，「再点一次收起」失效 → 用 `Closed` 时间戳 + 时间窗。
- `Popup` 有自己的 HWND，键盘事件不一定穿过宿主的 `PreviewKeyDown` → 让弹出内容自己抛 `DismissRequested`。
- 动态生成的元素用 `element.SetResourceReference(prop,"Key")` 而非 `DynamicResource`。
- 新增资源键要**同时**加 `Themes/Light.xaml` 和 `Dark.xaml`（键集必须完全一致）；样式放 `Styles/Shared.xaml`
  —— `ThemeService` 靠**文件名**匹配主题字典，样式字典挪进 Themes 会被误换。
- JSON 落盘一律 `Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping`，否则中文变 `\uXXXX` 没法直接编辑。
- **`double.NaN` 写不进 JSON**：`FloatingLeft/Top` 默认 NaN，序列化抛异常，而 `Save()` 容错 →
  表现成「全新安装时设置怎么改都存不下去」且毫无痕迹。用 `AllowNamedFloatingPointLiterals`，
  并且**容错的 catch 也要记日志**
- 同一份信息只保留一个显示口径（曾在栏头和行里出现两个日期口径）。
- **窗口尺寸和内容缩放必须出自同一个系数**：`RootGrid.LayoutTransform = ScaleTransform`，
  用取整后实际尺寸反算系数；只改 `Width/Height` 会放大时一片空、缩小时内容被裁。
- `LayoutTransform` 外面的元素读 `ActualHeight` 是**变换前**的布局尺寸（永远 168）→
  用 `TranslatePoint` 量首尾两点之差。
- `CollectionViewSource` 的分组先后取决于记录在集合里的出现顺序 → 先排好序。
- 别用 BCL 常见类型名当方法名（`Key`/`Point`/`Color`/`Brush`/`Path`/`Task`）—— 一个 `Key(DateTime)` 就把
  `Key.Enter` 顶掉（CS0119）。回调参数别用 `Action<...>`（不支持可选参数），需要默认值时自定义委托。
- 取值范围只写一份：常量放 `AppSettings`，滑杆 `x:Static` 绑，校验用同一组。
- **任何「隐式规则」都要显示在界面上**：文件归「当前查看的日期」本身合理，但横幅必须写明目标日期。

### 命令行与单实例
- 开关取值要判断下一个参数是不是另一个开关（`Services/CommandLine.cs`）：
  `--export-assets --quiet` 曾把 `"--quiet"` 当目录名。新增开关要在自测补解析断言。
- 开关匹配要同时认 `--x` 和 `--x=值`：只做全等比较会让 `--shot=路径` 被当成没有这个开关，
  程序按普通方式启动不退出（命令行那边只看到「卡住」）。
- 一次性命令（`--selfcheck`/`--shot*`/`--create-shortcut`/`--export-assets`）**必须绕开单实例互斥**，
  也要跳过数据写入，避免和运行中的实例并发写 `index.json`。

### 探针/自检纪律
- **探针必须无副作用**：「创建快捷方式」曾直接写桌面，从解压副本跑自检就把用户的快捷方式改指到临时目录。
  现在在 `logs\shortcut-probe\` 下试创建、验完删掉，桌面快捷方式只读并报告指向。
- **探针不能把改动留在设置对象里**：退出时 `Save()` 会把内存设置整个写回文件，临时改动会泄漏进用户配置
  （真出过：不透明度被写成 1）。`RunReport`/`WriteFloatingShot` 都要 `Snapshot()`+`Restore()`，
  模拟拖动后 `CancelPendingSaves()` 掐掉 700ms 节流。
- 探针依赖的前置状态要显式设好（悬浮窗缩放探针要先关 `autoCollapse`，否则量到的全是收起态 48px）。
- 悬浮窗 `ClampToArea()` 会把越界位置拉回工作区（「摆到 -32000」实际停在桌面左上角还抢焦点）→
  探针用 `BackgroundProbeMode = true` + `ShowActivated = false`，并断言「窗口落在屏幕外」。
- 量位置要量「卡片本身」而不是 ItemsControl 容器（外边距落在容器内部，量容器间隙永远是 0）。
- **界面改动的验证套路**：① 自检加探针（离屏窗口 → 需要时先 `SwitchTo(页面)`，`Collapsed` 时不生成
  条目容器 → 按控件真实交互方式模拟）；② 断言**可视树**而非数据（数 `GroupItem`、要求文字
  `ActualWidth/Height` 非零）——「视图对象里有分组」不等于「看得见」；③ 只能看的东西（裁切/配色/对齐）
  用 `--shot` 出图，没有真实数据就用 `WHALELOG_DATA_DIR` 指临时数据，别往用户记录里塞测试文件。
- 纯逻辑（日期解析、分组排序、文案）放非界面文件（如 `Services/DateGrouping.cs`），自测能直接编译它断言。
- 交互行为（拖拽/点击、贴边、收起）只能靠合成真实输入测；先确认基线干净，否则会把残留窗口当结果。
- bash 的 `kill` 杀 GUI 不会走 `OnExit`（`Settings.Save()` 不执行）→ 排查「配置被谁改了」时
  别把「普通启动一次程序」当嫌疑；会写文件的是主动 `Shutdown()` 的一次性命令。
  验证配置没被改要**打印具体数值**，只比 md5 可能两边都已被改过而看不出。
- 调试节奏：**先在 Debug 版验功能**（输出目录不同，不会被运行中的程序锁住），最后才切 Release。
- `--tray` 起的实例「存活 N 秒」不等于启动成功，可能被单实例守卫的 MessageBox 阻塞 →
  顺手 `EnumWindows` 查有没有 `#32770` 对话框。

## 三、素材机制
界面引用素材一律 `Source="{v:AssetImage <名字>}"`，**不要写 `pack://` URI**（会绕过「外部优先」解析）。
**「存在」和「可用」分开判断**：`AssetCatalog.FindExternal` 只查文件在不在，能否解码看
`IconAssets.LoadedFromExternal`；报告要写清用的是外部还是内置。首次运行导出出厂素材到 `Assets\`
（已存在的文件一律不覆盖）并附 `素材说明.txt`。

## 四、自包含打包（离线）
正路 `publish -r win-x86 --self-contained` 需要 NuGet 运行时包，本机没有 → `tools/make_portable_zip.py` 手工拼。
- 三块料：应用本体 + `hostfxr.dll`（在 `dotnet/host/fxr/<ver>/`，共享框架目录里没有）+ 两个共享框架全部文件；
  `runtimeconfig.json` 用 `includedFrameworks`。
- **`WhaleLog.deps.json` 必须列出全部框架程序集**，否则报 `Could not resolve CoreCLR path`；
  托管/原生 DLL 用 PE 头第 14 个数据目录（CLR Runtime Header）区分。
- 两个框架有 3 个同名程序集（`WindowsBase`/`System.Drawing`/`Microsoft.VisualBasic`），
  必须让 WindowsDesktop 版本覆盖 NETCore 空壳，否则 WPF 起不来。
- 验证：自检「运行环境」报运行时位置；再把 `DOTNET_ROOT` 指到不存在路径跑一遍。

## 五、本机环境限制
- `dotnet restore` 在受限会话不可用，稳定报 `Value cannot be null. (Parameter 'path1')`
  —— NuGet 取不到 `CommonApplicationData`。用 `tools/offline_restore.py` 手写 restore 产物，
  再带 `-p:SkipResolvePackageAssets=true -p:GenerateDependencyFile=false
  -p:GenerateRuntimeConfigurationFiles=false` 编译。
- `dotnet.exe` 是 32 位宿主 → **项目不要设 `PlatformTarget=x64`**（FileLoadException），用 AnyCPU。
- apphost 只能按本机已安装的运行时架构选（本机只有 x86）。
- `dotnet.exe` 在 `C:\Program Files (x86)\dotnet\dotnet.exe`，**不在 bash 的 PATH 里**，
  要用完整路径（带空格，记得引号）。
- **不能从 bash 调 `cmd.exe`**（安全策略：`Invoking cmd.exe from Bash bypasses all command validation`），
  所以 `build-whalelog-offline.cmd` / `build-whalelog.cmd` 在只能执行 bash 的会话里跑不了，
  要手工复现它那四步（`offline_restore.py` → `dotnet build …` → 再跑一次
  `offline_restore.py` → `make_apphost.py`）。
  **第三次的 `offline_restore.py` 不能省**：带 `-p:GenerateRuntimeConfigurationFiles=false`
  编出来的产物没有 `runtimeconfig.json`，直接启动报 `hostpolicy.dll not found`。
- 本会话里 PowerShell 工具不返回 stdout（命令 exit code 正常但抓不到输出），
  要读输出优先用 bash 或直接看文件。
- **给 .NET 程序传路径必须用 Windows 风格**（`C:/...` 或 `C:\...`）。Git Bash 里 `$PWD`
  是 `/c/Users/...` 这种 POSIX 路径，.NET 不认 —— 程序会**静默失败**：退出码 0、不报错、
  `WHALELOG_DATA_DIR` 指向的目录一个文件都不生成。凡"跑条命令看结果"的验证，
  **判定标准看产物文件，不看退出码**。
- **改同一个文件必须串行 Edit**：同一条消息里并行发两个改同一文件的 Edit，会丢其中一处
  （本会话栽过两次）。
- **本机没法 `git push` 到 GitHub**：`api.github.com` 通（HTTP 200），但 `github.com`
  被代理拦（`CONNECT tunnel failed, response 502`）。所以「建仓库 + 推代码」这类任务，
  建仓库只能用户手动，首次 push 得在能正常访问 `github.com` 的机器上做。
- **平台 GitHub 连接器只有"写文件"权限，没有"建仓库"权限**：
  `create_repository` → `403 Resource not accessible by integration`。
  而往一个**不存在**的仓库写文件返回的是 `404` 而不是 `403` ——
  用这个差异可以区分「没权限」和「仓库还不存在」。

## 六、发布例行（发 1.2 时定下来的）
- 版本唯一来源是 `src/WhaleLog/WhaleLog.csproj` 的 `<Version>`，界面读 `Assembly.GetName().Version` 自动跟，改一处即可。
- 打包产物命名带版本号：`桌面\鲸记 WhaleLog <版本>.zip`
  （`make_portable_zip.py` 不带参数会输出成 `鲸记 WhaleLog.zip`，要显式传路径加上版本号）。
- 顺序：升版本 → 重建 Release + apphost → 打包 → **同步 HANDOFF 与 README 里所有数字** → 提交。
- **文档里的项数必须 grep 校验**。交接单的断言数漂过 151 → 163 → 213，
  每次都是改代码忘了改数字；发版前 `grep -n "163\|151\|144"` 一遍成本极低。
- **桌面属于个人目录：删桌面上的旧包必须用户明确同意**，不要自作主张清理。

## 七、Git 远端与「上传通道」的边界（2026-09-15 实测）
- 远端：`https://github.com/yangdong19961116-cyber/whalelog.git`（私有，默认分支 `main`）。
  本地分支叫 `master`，推之前记得 `git branch -M master main` 对齐。
- ⚠️ **本机连不上 `github.com`**：`api.github.com` 通（HTTP 200），`github.com` 被代理拦
  （`CONNECT tunnel failed, response 502`）。→ **本机既不能 push 也不能 pull**。
  远端这条通道只对另一台设备有效，本机仍走「拷交接包 zip」的老路。
- **平台 GitHub 连接器：只有读权限。** 写文件（`create_or_update_file` / `push_files`）
  一律 `403 Resource not accessible by integration`；建仓库（`create_repository`）也是 403。
  ⚠️ **别用「往不存在的仓库写返回 404 而非 403」判断写权限** —— GitHub 对当前凭据
  *看不到*的仓库同样返回 404，404 只能说明仓库不存在。判权限必须看**仓库存在之后**的返回码。
  这条推断错过一次，直接导致写了一批「文档已上传」的错误文档，又回头改。
- 即便有写权限也传不动源码：连接器是逐文件写、内容当工具参数传（等于先读进模型上下文），
  全仓 962 KB 文本 + 1 MB 二进制图这个量级不允许；PNG/ICO/ZIP 这类二进制本来也不适合当文本传。
- → **结论：GitHub 这条通道只对新设备有效**，本机继续走「拷交接包 zip」。
- **`workwork` 是双项目工作区**：AudioRoute（`src/AudioRoute/`，已完成）+ WhaleLog
  （`src/WhaleLog/`，在做）。建基线时 AudioRoute 的文件一并进了仓库 ——
  交接时别把它只当鲸记一个项目。它的 README 原先在仓库根目录，已归位到
  `src/AudioRoute/README.md`。

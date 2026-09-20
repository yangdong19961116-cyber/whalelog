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
- **归档记录的粒度 = 一个路径 × 一天 = 一条记录**（2026-09-17 定）。
  同一个文件第二天改了再归档是**新记一条**（昨天那条留在昨天，仍是 v1）；
  同一天里反复改只升当天那条的版本号，不刷重复行。
  * `AddedDate` 是「某天有哪些归档文件」的**唯一**依据：当日页面、「只看当天」、
    「全部」分栏、月历圆点、日报周报、`notes\*.md` 看的全是它 ——
    改这个字段等于同时改六个入口，动手前先把这段读一遍。
  * ⚠️ **踩过**：升版本时只改了版本号、没同步 `AddedDate`，新版本就永远粘在首次归档那天，
    产生它的那天反而查不到（用户 9/17 报的正是这个）。老数据用
    `python tools\repair_archive_dates.py`（默认只预览，`--apply` 才写）按版本历史拆回各天。
  * 归档会主动跳过**自己数据目录里**的文件（免得把 index.json 又拖回来）。
    判断必须按**路径前缀 + 目录分隔符**比 —— 直接用 `AppPaths.Root` 做 `StartsWith`
    会误伤名字以数据目录名开头的**兄弟目录**，整批静默跳过（1.4.4 修，现象是"接住了但归档 0 条"）。
- 悬浮窗：鲸鱼娘抠图，透明置顶、松手吸边、收起成贴边小头像；显示条目数/文件数/倒计时。
  **1.4.4 起：文件（或文件夹）拖到小鲸鱼身上就会自动打开主面板 + 切到「文件归档」页 +
  一律记到今天**（开关 `AutoOpenMainWindowOnDrop`，默认开；关掉只静默归档不弹面板）。
  悬浮窗只负责"支起主面板 + 弹气泡"，归档仍走 `FileIntakeService`，规则与主面板完全一致。
  ⚠️ 给悬浮窗加任何交互前先看那条「透明像素接不住拖放」的坑（见「探针/自检纪律」）。
- 提醒：默认 18:00 提前 15 分钟（气泡 + 弹跳 + 音效 + 托盘通知）；`Ctrl+Alt+N` 快速记一笔；
  日报/周报汇总；桌面快捷方式自建；开机自启。
- **硬性约束：绝不移动/重命名/删除用户原文件。** 快照模式（复制副本）默认关闭。

### 命令与工具
构建 `build-whalelog.cmd`（正常）/ `build-whalelog-offline.cmd`（受限）；
**受限会话里 restore 报 `path1` 为 null 时先跑 `python tools/mkenv.py` 再 `source .tmp-env.sh`**
（真因是 bash 拿不到 Windows 会话的环境变量，不是"离线做不到"）；
打包 `python tools/make_portable_zip.py [out.zip]` → 自包含 zip（默认落 `E:\jingji`）；
自测 `tools/selftest/`（**312 项**断言，直接编译主程序非界面部分；
含真起 ffmpeg 解视频的用例 → 先把 ffmpeg 拷到 `tools\selftest\bin\Release\net8.0\`）；
诊断 `WhaleLog.exe --selfcheck` → `logs\selfcheck.log`（**217 项 / 22 段**，隔离空数据更少几项、
有另一个实例在跑时再少 1；同样要 ffmpeg 在旁边；
**脚本里跑必须带 `--quiet`**，否则弹一个"自检完成"对话框等人点，表现为卡死到超时）；
截图 `--shot`/`--shot-notes`/`--shot-settings`/`--shot-floating`，均支持 `--x 值` 和 `--x=值`；
交互探针 `python tools/drag_probe.py`（合成真实鼠标，会动光标、要求先退出运行实例 → 手动跑）；
拖放探针 `python tools/drop_probe.py`（合成真实 OLE 拖放，先 `dotnet build tools\dragsource\`；同上要手动跑）。
素材：程序旁 `Assets\` 同名文件优先于内置（清单 `AssetCatalog`，加载 `IconAssets`）；
`--export-assets [dir]` 只导出出厂素材；`WHALELOG_ASSET_DIR` 可改目录；
`WHALELOG_FFMPEG` 指定 ffmpeg.exe（`FfmpegLocator` 找不到它时视频动作播不了，其余功能不受影响）。
形象包：`Assets\characters\<ID>\` 一套形象一个目录，`dsh`（溟月，默认）/ `classic`（原版，根就是 `Assets\`）；
降级顺序 **设置里那个 → dsh → classic**（`CharacterPacks.Resolve`），原版是嵌入资源、永远能用。
转码素材：`python tools/fetch_dsh_assets.py` 拉源、`python tools/convert_dsh_webm.py --out assets/characters/dsh` 生成、
`--check` 自校验。

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
- **界面层的断言要问「这个控件点得动吗」，不能只问「背后的函数返回对吗」**（2026-09-20 用户报
  「设置里点新形象没反应」）：形象包那 13 条纯逻辑断言全过，问题照旧发生 ——
  真实情况是按钮 `IsEnabled=false`（素材没在程序旁边），中间隔着一层谁都没测。
  给界面控件开"诊断钩子"（读它 `IsEnabled/IsChecked/Content`、真翻一下 `IsChecked`）成本很低。
- **点一个已经选中的 RadioButton 不会重抛 `Checked`**（`IsChecked` true→true 无变化）。
  想断言"点得动"，必须点在**没被选中**的那一个上，否则测出来的是假的失败。
- **脚本里跑 `--selfcheck` 必须带 `--quiet`**：不带会弹「自检完成」对话框等人点，
  在批处理/agent 里表现为卡死到超时，而日志其实早写完了 —— 极易误判成"跑不完/挂了"。
- **凡有"像素值 / 内容正确性"要求的地方，断言要数「内容」，不能只数「结构」**（2026-09-20）。
  视频那节的旧断言只有"解出多少帧""每帧多少字节"，而 alpha 全丢时这两条**全都通过** ——
  黑底就这样混进了 1.4.0 / 1.4.1 两个出厂包，最后是用户肉眼发现的。
  现在的「透明通道」检查是：现场造一段**左半不透明 / 右半全透明**的 VP9 素材
  （`geq=...:a='if(lt(X,32),255,0)'`），解一帧**逐像素数 `alpha==0` 的个数**。
- ⚠️ **新写的断言要做一次反向验证**：把修复关掉重编，确认它**真的会挂**。
  不然很可能只是又添了几条永远通过的装饰 —— 这正是上一个 bug 溜出去的原因。
  本轮实测：关掉修复后自检挂 3 条（`全透明 0/4096 个像素`），改回来即全绿。
- **靠"运行时环境"决定成败的功能，断言和自检都测不到，必须真跑一次 + 配反向验证**（2026-09-20，1.4.4）。
  「窗口接住了没有」（命中测试、DPI、模态循环、外部进程）和「处理函数对不对」是两回事：
  `DragEventArgs` 的构造函数是 internal，造不出假事件 —— 处理函数全对、命中测试全错时自检会**全绿**。
  现在有两套合成真实输入的探针：`tools/drag_probe.py`（鼠标拖拽 vs 点击）、
  `tools/drop_probe.py` + `tools/dragsource/`（真实 OLE 文件拖放，三场景含一个"窗口外不该接住"的对照组）。
  **探针的判据要落在"用户能看见的结果"上**：拖放那条只看「主面板开没开 + 文件进没进当天归档」，
  **不看 `DoDragDrop` 的返回值** —— 桌面本身也是合法拖放目标，`effect` 恒为 `Copy`，看它会假绿。
- ⚠️ **`AllowsTransparency` 的层叠窗口是逐像素看 alpha 做命中测试的**（2026-09-20）。
  alpha=0 的像素上，鼠标消息和 OLE 拖放会**穿透到下层窗口** —— 拖到鲸鱼**旁边**的空白处
  什么都不发生、**且一个错都不报**（鲸鱼本体上却是好的 → 看着像"时灵时不灵"）。
  解法是让整块矩形可命中：根 `Grid` 背景从 `Transparent`(alpha=0) 改成 `#01000000`(alpha=1 的近透明黑)；
  提示层反过来设 `IsHitTestVisible="False"`。**给悬浮窗加交互先问一句"这块像素是不是全透明"。**
- **自检项数会随数据浮动**（真实数据 217 / 隔离空数据更少几项），**段落数固定 22**。
  **看结尾「结论：全部通过」，别死抠数字**。

## 三、素材机制
界面引用素材一律 `Source="{v:AssetImage <名字>}"`，**不要写 `pack://` URI**（会绕过「外部优先」解析）。
**「存在」和「可用」分开判断**：`AssetCatalog.FindExternal` 只查文件在不在，能否解码看
`IconAssets.LoadedFromExternal`；报告要写清用的是外部还是内置。首次运行导出出厂素材到 `Assets\`
（已存在的文件一律不覆盖）并附 `素材说明.txt`。

### 形象包（2026-09-20 起）
`Assets\characters\<形象ID>\` 一套形象一个目录；`classic` 是保留名、根目录就是 `Assets\` 本身（老布局没搬家）。
**素材必须躺在 `WhaleLog.exe` 旁边**（`AppPaths.AssetsDir` = 程序目录\Assets）——
`WhaleLog.csproj` 1.4.1 起把仓库 `assets\characters\` 拷进编译产物；在这之前
从 `bin\Release\...` 直接跑是**没有形象素材**的，表现就是设置页「溟月」按钮灰着、点了没反应。
`CharacterPacks.Resolve` 的降级顺序 **设置里那个（可用就用）→ `dsh` → `classic`** ——
原版是嵌入资源、永远能用，所以这个函数**永远不返回用不了的形象**。
`IconAssets.LoadPackOnly` 用于新形象：**没有差分图时不回落内置那张原版的闭眼图**
（否则会出现"换了形象、鼠标一移上去变回旧鲸鱼"）。

### 透明视频动作（2026-09-20 起）
源素材 VP9-alpha webm **原样搬运不重编码**（转精灵图 PNG 全量 ≈ 1.4 GB，不可行），
运行时用 ffmpeg 子进程解码（WPF 解不了 VP9 的 alpha，且 APNG 那条老结论同样适用）。
- ⚠️ **必须点名 `-c:v libvpx-vp9`，否则 alpha 整条丢掉**（2026-09-20 用户报「颜色不对、
  锯齿严重、还有黑底」，1.4.2 修）：VP9 的 alpha **不在像素里**，在 matroska 的
  **附加数据**（`BlockAdditional`，`ffmpeg -i` 能看到 `ALPHA_MODE: 1`）里，
  而 ffmpeg 默认挑的 **native vp9 解码器不认它** —— 照样一帧帧吐 RGBA、帧数全对，
  但 **alpha 位全填 255**，且**不报任何错**。表现为角色顶着一块黑方块、边缘生硬。
  点名之后输出像素格式从 `yuv420p` 变 `yuva420p`，**代价只有 18%**（241 帧 0.155s → 0.183s）。
  探测：`VideoFrameStream.IsVp9Webm`（读文件头 64 KB 搜明文 `V_VP9`，不起进程）+
  `FfmpegSupportsVp9Alpha`（问一次 `-decoders` 后缓存），两条都满足才加点名参数
  （否则 mpeg4 之类素材会因"解码器不匹配"直接解不开）；有没有 libvpx-vp9 兜底退回 native。
  这与调研期记的「PyAV 静默丢 alpha」是**同一类**问题：**解码器可以选择性无视附加数据**。
- ⚠️ **"颜色不对"别急着往色彩空间上想**：实测两种解码器解出的实心像素**逐像素最大差值 0**，
  颜色一个字节都没错 —— 1.4.2 那次观感上的"色偏"是黑底造成的对比错觉。**先测，再猜。**
- ⚠️ **裸字节必须和贴图的字节序对齐：`-pix_fmt bgra`，不是 `rgba`**（2026-09-20 用户报
  「颜色像反色了、本来应该是蓝色」，1.4.3 修）。ffmpeg 的 `rgba` 是 **R,G,B,A** 分量顺序，
  而 WPF `PixelFormats.Bgra32`（`SpritePlayer` 贴图用的）内存里是 **B,G,R,A** ——
  中间没有转换代码，**红蓝两条通道整个对调**：深蓝紫头发显示成栗棕红。
  两个名字看着都在说"红绿蓝"，但一个说**分量顺序**、一个说**内存顺序**，差一格就翻车。
  修法是让 ffmpeg 吐 `bgra`（swscale 内部换，**不额外花时间**），常量 `VideoFrameStream.PixelFormat`
  + `OffsetB/G/R/A`；`SpritePlayer` 那行 `WriteableBitmap(..., PixelFormats.Bgra32, ...)` 上方写了
  「两处是一对，改一边必须改另一边」。**以后凡"外部工具产像素 → 本程序贴图"，先验一次字节序**
  （拿纯净色素材解一帧，看值落在哪条通道）。
- ⚠️ **"解出来了"和"解对了"是两回事。** 上面两个 bug 是同一条教训的两半：
  断言"帧数/每帧字节数"→ alpha 全丢时全过；断言"有没有透明像素"→ 颜色全反时全过。
  **缺陷都是"结构对了、内容错了"。** 断言要落到**画面内容**上（透明值多少、红值落在哪条通道）。
- ⚠️ **用户说的每句现象都要当真**（"像反色""只有切换时开头几帧是对的"）—— 那是定位证据。
  "开头几帧是对的"这里指的是**切换后先是 WPF 自己解码的静态图**（那张是准的），
  视频帧要等 ffmpeg 起来 ≈150 ms 才贴上，所以它和"逐帧一致的通道错误"**并不矛盾**。
- `VideoFrameStream`：**8 帧环形缓冲**，满则阻塞生产者（天然不积压、几乎不吃 CPU）；
  一个 tick 最多贴一帧，落后逐帧追但**不快进**。
- 测无限流（`-stream_loop -1`）时**别指望 `-frames:v N` 兜底**：ffmpeg 可能不停，
  实测一路把临时文件写到了 **35 GB**。读够自己要的帧数就主动杀进程。
- 冷启动 ≈150 ms → 鼠标**移入悬浮窗就预热**（`SpritePlayer.Warm`）。
- **角色由目录名前缀认**（`idle-` / `clickBounce-` / `ambient-` / `drag-` / `move-` / `turn-`），
  必须从目录名记，**不能从动作键反推**（`idle` 那支的键归一化成了 `blink`，是历史行为）。
- ⚠️ **`filter` 是当不可信输入校验的**（防命令注入）：**先按 `=` 切滤镜名、再按 `:` 切参数**。
  顺序反了会把 `crop=384:360:128:0` 的**名字**切成 `crop=384` → **106 个动作全被判非法**，
  而界面表现只是"动画一个都不出来"，极难归因。这类"切分隔符"的活**先写用例再写实现**。
- ⚠️ 素材许可：CC BY-NC-SA 类同人素材（OC「溟月」/ 画师上善无形），**仅限个人非商业、必须署名**；
  要发布/商用必须整体撤下 `assets\characters\dsh\`（撤下后自动退回原版，有自检断言守着）。

## 四、自包含打包（离线）
正路 `publish -r win-x86 --self-contained` 需要 NuGet 运行时包，本机没有 → `tools/make_portable_zip.py` 手工拼。
⚠️ **不要再引入"暂存目录"**：2026-09-20 实测，受限环境会拦「一次删 50 个以上文件」，
**被拦下的删除请求会在后台被逐个补执行** —— 打包期间把暂存树里的 `Assets\`
以约 17 个/秒匀速删掉，打出来的包「程序在、素材没了」且**脚本不报任何错**。
现在脚本**直接把源文件写进 zip**（生成的两份 json 用 `writestr`，出厂素材导出到系统临时目录），
**落盘后回查关键条目**，缺一条就 `return 1`。以后改这个脚本务必保住这个自检。
- 三块料：应用本体 + `hostfxr.dll`（在 `dotnet/host/fxr/<ver>/`，共享框架目录里没有）+ 两个共享框架全部文件；
  `runtimeconfig.json` 用 `includedFrameworks`。
- **`WhaleLog.deps.json` 必须列出全部框架程序集**，否则报 `Could not resolve CoreCLR path`；
  托管/原生 DLL 用 PE 头第 14 个数据目录（CLR Runtime Header）区分。
- 两个框架有 3 个同名程序集（`WindowsBase`/`System.Drawing`/`Microsoft.VisualBasic`），
  必须让 WindowsDesktop 版本覆盖 NETCore 空壳，否则 WPF 起不来。
- 验证：自检「运行环境」报运行时位置；再把 `DOTNET_ROOT` 指到不存在路径跑一遍。
- **1.4.0 起包内还多两样**：`Assets\characters`（形象包，51.9 MB）与 `ffmpeg.exe`（约 84 MB，
  找不到时按 `WHALELOG_FFMPEG` → `assets\tools\` → PATH → `imageio_ffmpeg` 的顺序搜）。
  两者都是已压缩内容，zip 压不太动，所以包从 58 MB 涨到 **139.6 MB** —— **不是脚本算错了**。
  ⚠️ ffmpeg 是 GPL 授权的独立可执行文件，包内 README 里必须写明这一点。

## 五、本机环境限制
- **`dotnet restore` 报 `Value cannot be null. (Parameter 'path1')` 时，先试 `tools/mkenv.py`。**
  这个错一度被当成"受限会话做不到 restore"，**真因是会话里的 bash 拿不到 Windows 的环境变量**
  （`ProgramData` / `APPDATA` / `ALLUSERSPROFILE` 全缺），NuGet 取不到 `CommonApplicationData`。
  `python tools/mkenv.py` 读注册表（HKLM 机器级 + HKCU 用户级）导出 `.tmp-env.sh`，
  `source` 之后 `dotnet build -c Release` 实测 **0 错 0 警**。
  → **先找可变量，再下"不可行"的结论。**
- 上面这条走不通时才用离线兜底：`tools/offline_restore.py` 手写 restore 产物，
  再带 `-p:SkipResolvePackageAssets=true -p:GenerateDependencyFile=false
  -p:GenerateRuntimeConfigurationFiles=false` 编译。
- `dotnet.exe` 是 32 位宿主 → **项目不要设 `PlatformTarget=x64`**（FileLoadException），用 AnyCPU。
- apphost 只能按本机已安装的运行时架构选（本机只有 x86）。
- `dotnet.exe` 在 `C:\Program Files (x86)\dotnet\dotnet.exe`，**不在 bash 的 PATH 里**，
  要用完整路径（带空格，记得引号）。
- ⚠️ **从 bash 启动鲸记必须先补 `SystemRoot` / `windir`**（2026-09-20 定位到，花了大半天）。
  受限会话的 `os.environ` 里这两个变量**是空的**，而 WPF 初始化会去拼字体缓存路径
  （`%SystemRoot%\Fonts` 那一套）—— 拿不到就抛
  `Invalid URI: The format of the URI could not be determined`，**偶发崩溃**：
  有的跑次没事、有的直接死在启动，看着像"随机闪退"，极难归因。
  ```bash
  cd src/WhaleLog/bin/Release/net8.0-windows
  export SystemRoot="C:\\WINDOWS" windir="C:\\WINDOWS"
  ./WhaleLog.exe --selfcheck --quiet
  ```
  `tools/drop_probe.py` / `tools/drag_probe.py` 里的 `child_env()` 已经处理了；
  自己写临时脚本验程序时别忘了。（诊断脚本 `tools/_diag_launch.py` 已完成使命，已删。）
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
- **平台 GitHub 连接器只有读权限**：写文件（`create_or_update_file` / `push_files`）与
  建仓库（`create_repository`）一律 `403 Resource not accessible by integration`。
  ⚠️ 曾用「往不存在的仓库写文件返回 404 而非 403」推断它有写权限 —— **这条是错的**：
  GitHub 对当前凭据*看不到*的仓库同样返回 404，404 只能说明仓库不存在。
  判权限必须看**仓库存在之后**的返回码（详见第七节）。
- **bash 命令可能被「沙箱重跑」一次**：输出末尾会出现 `⚠️ Sandbox bypassed (escalation-approved)`。
  后果很阴：第一次（看不到的）那次其实**已经执行成功**，第二次再跑就得到
  `nothing to commit`、退出码 1，长 `&&` 链当场中断，**后面的步骤一步都没跑**。
  → 收尾类命令别用长 `&&` 链，改用 `;` 并在最后**显式回查结果**（文件时间戳、`diff`、`git status`）。

## 六、发布例行（发 1.2 时定下来的）
- 版本唯一来源是 `src/WhaleLog/WhaleLog.csproj` 的 `<Version>`，界面读 `Assembly.GetName().Version` 自动跟，改一处即可。
- **所有产物统一放 `E:\jingji`**（2026-09-17 用户定的规矩）：便携包、交接包，以及以后任何
  要交出去的产物都在那儿，**别再丢桌面** —— 桌面堆几个版本之后看不出当前发的是哪版。
  两个打包脚本**不带参数**就输出到这里，目录不存在会自动创建；
  万一 E: 盘不存在就退回桌面并在输出里说明原因（不能为守约定把打包卡死）。
- 打包产物命名带版本号：`E:\jingji\鲸记 WhaleLog <版本>.zip`。
  版本号由 `make_portable_zip.py` 自己从 csproj 读 —— 以前默认名不带版本、会覆盖上一版，
  只能靠时间戳猜"上一版发的是哪个"，所以改成默认就带版本。
- 交接包默认名 `鲸记 WhaleLog 项目交接包 <当天日期>.zip`，同样落在 `E:\jingji`
  （源目录默认取仓库根，`make_transfer_zip.py` 不带参数即可）。
- 顺序：升版本 → 重建 Release + apphost → 打包 → **同步 HANDOFF 与 README 里所有数字** → 提交。
  （1.4.0 起多一步：**打包前把 `ffmpeg.exe` 放到 `src\WhaleLog\bin\Release\net8.0-windows\`**，
  并跑一次 `--selfcheck` 确认「透明视频」那节真的解出了帧。）
- ⚠️ **打包前先确认没有 WhaleLog 实例在跑**（`tasklist /FI "IMAGENAME eq WhaleLog.exe"`）。
  被占用时 `dotnet build` 报 `MSB3027 / MSB3021 文件被 WhaleLog (<PID>) 锁定`，
  前面编译其实已经成功、只有最后拷贝 apphost 那步失败 —— **别误判成代码编译不过**。
  杀实例属用户可见操作，**要先问**（用户可能正开着在记东西）。
- **文档里的项数必须 grep 校验**。交接单的断言数漂过 151 → 163 → 213 → 232 → 294，
  每次都是改代码忘了改数字；发版前 `grep -rn "232\|164 项"` 一遍成本极低
  （注意：`memory/` 下**按日期记的历史段落里的旧数字是对的**，别一起改掉）。
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

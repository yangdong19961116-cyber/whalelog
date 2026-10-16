# WhaleLog / AudioRoute 详细档（MEMORY 的详细版，2026-10-14 拆分）

> `MEMORY.md` 是高频要点索引；**本文件是被拆出来的全文**（原 MEMORY.md，按一~八分节）。
> 需要查具体坑、命令、历史结论时来这里。日常起手看 `MEMORY.md` 即可。

# 工作区
`C:\Users\Administrator\WorkBuddy\workwork\`：两个独立的 WPF + .NET 8 应用（AudioRoute / WhaleLog）。构建受「本机环境限制」（六）约束。同一文件**串行 Edit**（并行会丢改动）。

## 一、AudioRoute（Win10 音频设备切换托盘）
浅/深色+跟随系统 · `RegisterHotKey` · NAudio `IMMNotificationClient` 插拔自动切换 · 自包含单文件 · 构建 `build.cmd`。参考 SoundSwitch、EarTrumpet。
坑：`ComboBox.IsEditable` 文本变更挂 `PART_EditableTextBox`（先 `ApplyTemplate()`）；程序化赋值反向触发 SelectionChanged 死循环 → `_syncingUi` 守卫；切默认设备要对 `Multimedia` 与 `Communications` 各调一次 `SetDefaultDevice`。

## 二、WhaleLog 鲸记（每日工作记录 + 文件归档台）
版本唯一来源 `src/WhaleLog/WhaleLog.csproj` 的 `<Version>`（**当前 1.4.7；「当天完成度」已完成未发版**；界面读 `Assembly.GetName().Version` 自动跟）。三页（记录/归档/设置）+ 悬浮窗 + 托盘。每天一份 md；日期栏展开整月日历（有记录打点，「跳到」认 `9/23`/`0923`/`昨天`）。

### A. 进度条 + 未完成自动顺延（10-14）
- `NoteEntry.Progress`(0–100) 唯一真源，`Done == (Progress == 100)`：**两个 setter 不能互调**（`Progress` 写 `Done=v>=100`，`Done` 直写 `_progress`，互调会成环）。`ResumeProgress` 记「取消勾选回哪」（仅拖到 <100 时更新）。老数据由 `CarryOverService.Normalize`（`LogBookStore.Load` 调）补 done=100。
- `CarryOverService`：启动 + 跨零点（挂 `ReminderService.Ticked`，20 秒轮询）把**所有过去日子**里「未完成且没顺延过」的条目**复制**到今天（只看昨天会漏掉几天没开机的积压）；源条目只戳 `CarriedToDate`。幂等靠 `CarriedToDate` + 当天 `CarriedFromId` 两把锁（重复启动不复制／删副本不复活／勾完断链）。
- 顺延后刷新必须用 `RefreshCurrentDay()` **差量增删**，不能 `LoadDay`（会覆盖 `SummaryInput.Text`，吃掉正在打、防抖未落盘的小结）。

### B. 当天完成度（10-14）
列表底部一行：「当天完成度 · 徽标 · N%」。**10-14 用户第二次改口径：去掉那根汇总条、文字左对齐**
（一个左对齐的 StackPanel；原来那套 8 列 Grid + 隐身占位 + `DayProgressBar` 已删）。
- **算法只有一份**：`Models/Notes.cs` 的 `DayCompletion.Average(entries)`（全条目进度平均、勾选=100、四舍五入；**无条目 = null**）+ `Display(live, frozen, isToday)`。**别退化成"勾了几条/共几条"**（95+5 与 50+50 都该是 50）。界面那行／日报周报／`index.json`／`notes\*.md` 四处共用；各自再算必然出四个数。
- 定格 `CompletionService.FreezePastDays`（启动+跨零点，扫过去所有日子）→ `DayNote.Freeze(at)`：`CompletionSettled` 管"结没结账"，**幂等靠它 + 值相等**（只看时间戳会把"今天已存过值"当"已结账"而永不记时间）；`CompletionFrozenAt` **只在 `at.Date <= 那天+1天` 时写**（装新版首次跑会补算一两个月前，那时编"今天"的时刻是假的 → 留空并对外说"没有记下收工时刻"）。
- **今天的值每次落盘前刷**：`LogBookStore.Save` 里的 `SyncTodayCompletion`（internal，自测直调）——"次日 0 点前保存"最靠得住的是"改一次存一次"（700ms 防抖）。**过去的日子被改动 → 重新定格**（`RefreshDayCompletion` 见 `Completion != live` 就 `Freeze`），否则上面条变了底下数字不动。
- 没有条目的一天**整行收起**，不显示 0%（"还没记"≠"做到 0%"）。
- 落点：`index.json` 的 completion/completionSettled/completionFrozenAt；`notes\*.md` 头字段 + 正文 `> 当天完成度 **N%**`；日报概览+分日明细；周报「平均每日完成度」。

### B2. 回看历史把一条改回未完成 → 当场同步到今天（10-14）
自动顺延只在**启动 / 跨零点**跑，可"昨天那条其实没干完"往往是第二天翻记录才发现的 —— 所以
`MainWindow.TrySyncUnfinishedPastEntryToToday`（挂在 `OnEntryPropertyChanged` 的 Progress 分支，
取消勾选也走这条：`Done` setter 会把 Progress 退回 `ResumeProgress` 并抛通知）→
`CarryOverService.CarryEntry(book, sourceDay, entry, today?)`（**判定唯一权威**）。
- 判定：只管过去的日子 / 完成的永不顺延 / 已顺延过的不再搬 / **今天已有它副本的不重复搬**
  —— 今天的进展优先，绝不因为回看历史把今天已经做完的事退回去；搬的是副本，源条目一个字不改。
- 源条目要**当场**亮出「已顺延至 X」，所以 `NoteEntry.CarriedToDate` 从自动属性改成会发通知的属性
  （`IsRolledForward` / `RolledToText`）。
- `_probeMode` 下只走到判定就返回（探针手上是假数据、背后是用户真记录，不能真写）。
- 想改成"今天那份也跟着改"的话，动 `CarryEntry` 的第四个判定。

### C. 改已写下的记录（10-14）
行右侧「编辑」按钮 + **双击整行** → `Views/EditEntryWindow.xaml`。只改正文/分类/标签**不改时间**（时间是事实，改它还得重算耗时）；写回**模型里同一个对象**（绑定自刷新，Id/时间/进度/完成/顺延标记不动）。清空正文点保存**要拦**（保存≠删除）；**改动顺着顺延链双向传下去**（见 C2）。双击落点判定 `MainWindow.HitsInteractiveControl`：往上碰到 `ButtonBase`/`Slider`/`TextBox` 就不算"编辑整行"。

### C2. 编辑时同步到跨天顺延的链（10-14 第三轮）
`CarryOverService.ChainOf(book, entry)` 认链 + `SyncContent(book, edited)` 同步。**双向**（用户选的口径：编最早那份和编今天那份一样），**只同步正文/分类/标签**。
- **链的两个方向**：往上是 `CarriedFromId`；往下是 `CarriedToDate` + 拿那天查 `CarriedFromId == 本条.Id`（**条目上没有"我复制给了谁"的 Id，只有日期**，所以下游必须两步查）。⚠️ **向上追溯与向下遍历必须各用一个 visited 集合**：共用一个时从中间那份出发会先把源头和自己都标上 → 第二段刚走到自己就当成"回到原地"而截断（自测第一次跑就抓到 `ChainOf(mid).Count == 1`）。脏数据互指也不会死循环；链里找不到自己时退回"只有我一条"= 空操作。
- ⚠️ **进度/勾选/时间/Id/顺延标记一个都不动**。链上各天各自推进：今天干到 70% 不能被昨天那份的 20% 顶回去（用户 10-13 定的"今天那边的进展优先"）。名字可以统一，进度不能。反向验证：加一行 `other.Progress = edited.Progress` → 自测立刻挂。
- 正文为空时**不覆盖**（编辑窗本来就拦空正文，服务层也自己站得住）。
- 界面接线抽成 `MainWindow.ApplyEntryEdit(entry, persist)`（模态窗探针没法"点了保存再往下走"，只能把这段拎出来让自检真跑）；`persist:false` 只给探针 —— 自检也会拿**真实数据目录**跑，那时写假数据就是动用户记录。状态栏文案走 `EditStatusText`（` · 同步到 10月14日 的顺延记录`，**用中点不套括号**：正文自己常带括号）。

### D. 往期清单的「未收尾 / 已顺延」（10-15，用户要求「往期记录里的未收尾改成已顺延，当天存在的未收尾的除外」）
`Views/RecentDaysPanel.xaml.cs` 每行右侧原本凡是"这天有没勾完的"就一律挂橙色「· N 未收尾」。可现在那些东西
**早就顺延到后面去了**（`CarriedToDate` 有值），橙色在这界面的意思是"我还能回去补一下"（待办），天天催一件
早就不欠在这一天的事是误导。
- **判据只有一条：条目自己身上有没有 `CarriedToDate`**（**不按"这天是不是过去"猜** —— 回看旧日期时补记一条
  时它一条都没滚过，硬说"已顺延"是假话）。有 → 「· N 已顺延」走 `TextMutedBrush`（**和事项行尾「已顺延至 X」
  同色**）；没有 → 「· N 未收尾」走 `WarningBrush`。两拨都有就都写出来（「· 1 未收尾 · 1 已顺延」）。
- **今天（及将来）一律算未收尾**：顺延只从过去往今天搬，今天的还没滚走 —— 这就是用户那句"当天存在的未收尾
  的除外"。实现在 `DayIndex.OpenSplit(day, today)` 里那道 `d.Date >= today` 守卫（读不出日期的也按未收尾）。
- 落点：`DayDigest.Carried`（`Open` 的子集，Build 里顺手统计）+ `Pending = Open − Carried`（顺手删掉已无人用
  的 `HasOpen`，留着下次又会有人拿它去决定那段橙色）；文案 `DayIndex.OpenLabel(count, carried)`；
  `RecentDaysPanel.AddBadge` 一处给颜色键并记进 `Row.Badges`。`--shot-recent` 会把每行的标记和颜色念到 stdout。
- ⚠️ **脱离可视树的控件断言不了颜色**：探针里 `new RecentDaysPanel()` 后不挂窗口，`SetResourceReference`
  那一刻上游没资源字典，`Foreground` 读出来是 null → `Row.Badges` 存的是 **BrushKey**（`RowBadgeBrushes`）。
- 反向验证（都被抓到）：`OpenSplit` 里把 `Math.Clamp(day.Carried,…)` 换成 `0` → 自测挂 3 + 自检挂 4；
  `d.Date >= today` 挪到 99 年后 → 自测挂「今天一律算未收尾」「将来也算」；「已顺延」刷成 `WarningBrush`
  → 自检挂「已顺延走灰色」「混合那天两段颜色分开」。真机核对：真实数据 `10-15` 未收尾 / `10-14`、`10-13`
  已顺延 / `10-12` 无标记，与用户截图逐行一致。

### 归档
拖入 → 规则引擎分类 → SHA-256 指纹判版本链 → 记完整路径。文件归**当前查看的日期**；「全部」按日期分栏。
- **粒度 = 一个路径 × 一天 = 一条**（09-17 定）；次日再归档是**新记一条**，同一天反复改只升当天版本号。⚠️ `AddedDate` 是「某天有哪些归档」的**唯一**依据（当日页/只看当天/全部分栏/月历圆点/日报周报/`notes\*.md` 七个入口），改它等于同时改七处。踩过：升版本没同步 `AddedDate` → 新版本永远粘在首次归档那天。修：`tools\repair_archive_dates.py`（默认预览，`--apply` 才写）。
- 跳过**自己数据目录里**的文件要按**路径前缀 + 目录分隔符**比；用 `AppPaths.Root` 直接 `StartsWith` 会误伤名字以数据目录名开头的**兄弟目录**，整批静默跳过（1.4.4 修）。
- 悬浮窗：透明置顶、松手吸边（两轴独立、能吸角）、收起成贴边小头像；显条目/文件数/倒计时；0.7×–1.6×。1.4.4 起文件（夹）拖到小鲸鱼 → 自动开主面板+切归档页+记到今天（`AutoOpenMainWindowOnDrop`）。⚠️ 加交互前先看「透明像素接不住拖放」。
- 提醒默认 18:00 提前 15 分钟（气泡+弹跳+音效+托盘）；`Ctrl+Alt+N` 快记；日报/周报；开机自启。
- **硬性约束：绝不移动/重命名/删除用户原文件**。快照模式默认关。

### 命令与工具
- 构建 `build-whalelog.cmd`（正常）/`-offline.cmd`（受限）；受限会话 restore 报 `path1` 为 null → `python tools/mkenv.py` 后 `source .tmp-env.sh`（六）。打包 `python tools/make_portable_zip.py [out.zip]`（默认 `E:\jingji`，名字带版本号）。
- 自测 `tools/selftest/`（**602 项**，Debug/Release 都跑；**新增源文件必须同步加进 `selftest.csproj` 的 `<Compile Include>`**，否则 CS0103；真起 ffmpeg 的用例要求 ffmpeg 在 `tools\selftest\bin\<cfg>\net8.0\` —— **没放就少 20 项**，别以为少了断言是好事）。
- 诊断 `WhaleLog.exe --selfcheck [--quiet]` → `%APPDATA%\WhaleLog\logs\selfcheck.log`（**31 段 / 隔离数据 374 项，项数随数据浮动，看结尾「结论：全部通过」**；**脚本里跑必须带 `--quiet`**，否则弹框等人点=卡死超时；**ffmpeg.exe 要在程序旁**，否则「透明视频动画」假失败）。
- 截图 `--shot`/`-notes`/`-settings`/`-floating`/`-calendar`/`-edit`（月历在 Popup、编辑窗是独立窗口，各单独出口；月历放大 4 倍看圆点）；支持 `--x 值` 与 `--x=值`。交互/拖放探针 `python tools/drag_probe.py`、`drop_probe.py`（先 `dotnet build tools\dragsource\`）会动光标、要求先退实例，**手动跑**。
- 环境变量 `WHALELOG_ASSET_DIR`（素材目录）/`WHALELOG_FFMPEG`/`WHALELOG_DATA_DIR`（**换数据目录，出图试跑指临时目录，别动用户记录**）；`--export-assets [dir]` 只导出出厂素材。
- 形象包 `Assets\characters\<ID>\`（`dsh` 溟月/默认、`classic` 原版=仓库根 `Assets\`）；降级 **设置里那个 → dsh → classic**（`CharacterPacks.Resolve`），原版是嵌入资源、永远能用。转码 `tools/fetch_dsh_assets.py`、`convert_dsh_webm.py --out assets/characters/dsh`。
- **查源仓库素材有没有更新**：`python tools/check_dsh_updates.py`（10-15 新增；**只比清单、不下载一个字节**，退出码 `0` 没变 / `1` 有变，`--quiet` 只在有变时说话、`--json` 给程序吃）。源仓库 `MerZlin/dsh-pet-indesktop`，**角色目录现在叫 `shenshen`**（本地叫 `dsh` 是我们自己起的名，两边目录结构不同 —— 本地是转码产物，没有源仓库那层 `videos/<分类>/`）。判据两条一起用：① 本地每个 `anim/<动作>/anim.json` 的 **`source` 字段**就是源仓库的原始文件名；② 素材**原样搬运不重编码**，所以 `video.webm` 字节数必须和源 tree 里 blob 的 `size` **逐字节相等** —— 数量对得上但字节变了就是「同一个动作被重画」，这条最容易漏。清单走 `git/trees/main?recursive=1`（一次请求拿全量；本机只有 `api.github.com` 通），缓存落 `.tmp-dsh-tree.json`（`.gitignore` 的 `.tmp-*` 覆盖）。遍历本地时**跳过 `_`/`.` 开头的目录**（手工备份 `idle-xxx.bak`/`_old` 不该算成一个动作，否则报一堆假「消失」）。
  **2026-10-15 查的结果：零更新**（106/106 同名同字节）。元数据里只有 `videos/manifest.json` 多了一行 `"head_box": [185,60,436,195]`（9-22 提交，dsh-pet 做「鼠标到屏幕边缘探头」用）；**`body_box` 没变**，而 `convert_dsh_webm.py` 的 `load_body_box` 只读 `body_box` → **转码结果不受影响、不用重转**（鲸记全仓 grep 无 `head_box` 引用）。源仓库 10-14 发了 v4.3.0（拖拽物理/Markdown/CI 一堆修复）—— 那是 dsh-pet 自己的程序，与素材无关。

### 状态与单选（同类 bug 踩过两次）
- **ToggleButton 不互斥，单选必须显式处理**：互斥组要有唯一 source of truth（如 `_activePage`），可见性与勾选全从它推导（曾两页同时 Visible、内容重叠）。
- 目标值**从被点的 sender 推导**，绝不读另一个控件的 `IsChecked`：`目标值 = clicked.IsChecked == true ? target : 当前值;` —— 末尾 `: 当前值` 必须有（再点当前项会把 `IsChecked` 反选成 false，不兜住就「两个都不选」）。按 ToggleButton 真实行为（**先翻转 `IsChecked` 再抛 `Click`**）逐路径自检。任何改控件值的事件处理器都要 `_syncing` 守卫。

### WPF 界面陷阱
- `ValueChanged`/`TextChanged` **别写 XAML 上**（解析期给 `Minimum`/`Maximum` 赋值会连带触发，那时同段 XAML 靠后的控件还没构造）→ 统一在 `InitializeComponent()` 后订阅。`Style=` 只接 Style，`Foreground=`/`Background=` 才接 Brush（写错编译期不报，运行时 XamlParseException）。
- **`TextBlock.Foreground` 与 `Control.Foreground` 是两个 DP**：在 TextBlock 上 `SetResourceReference(ForegroundProperty,…)`（解析到 Control 那个）**不报错也不变色** —— 最安静的坏法。给哪个控件设资源就点名它自己的 DP。
- 失焦自动关闭的窗口要 `_closing` 守卫 + 统一走 `RequestClose()`（否则「窗口关闭期间无法调用 Close」）。`Popup`+`StaysOpen=False` 会吃掉触发按钮自身点击 → 用 `Closed` 时间戳+时间窗兜；`Popup` 有独立 HWND，键盘事件不一定穿过宿主 `PreviewKeyDown` → 让弹出内容自己抛 `DismissRequested`。
- **`ControlTemplate` 名字作用域不跨模板**：外层 `ControlTemplate.Triggers` 的 `TargetName` 指不到嵌套模板（如 RepeatButton 自己模板）里的元素，编译期报 `MC4111`。绕法：颜色走能跨模板读的属性 —— 子模板里 `{Binding Foreground, RelativeSource={RelativeSource AncestorType=Slider}}`，外层只改 `Foreground`。同一套画法**抽成共享 `ControlTemplate`**（`ProgressBarTemplate` 给可拖的 `ProgressSlider` 与不可拖的 `DayProgressBar` 共用），差别留在各自 Style。
- **两处要对齐 → 列宽必须逐列一致**：固定宽列（百分比 42、耗时 76）两边写同一个数；靠内容撑宽的 `Auto` 列两边不同就错开（当年底部汇总条右移 2.7px 就是这么来的）。放不了内容的列塞**隐身同款占位**（`Visibility="Hidden"` 的同内容控件，宽度自动跟），别硬编码像素。*（10-14 起汇总条已经不画了，这条规则对"记录行内部各行互相对齐"仍然成立。）*
- **`ScrollViewer` 滚动条占一整列**：钉在外面的元素宽「窗口−滚动条」、里面的行「窗口−滚动条−4」→ 要和列表行对齐的东西得放进**滚动内容里**。
- 动态生成元素用 `element.SetResourceReference(prop,"Key")` 而非 `DynamicResource`。新增资源键要**同时**加 `Themes/Light.xaml` 与 `Dark.xaml`（键集完全一致）；样式放 `Styles/Shared.xaml`（`ThemeService` 靠**文件名**匹配）。
- JSON 落盘一律 `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`（否则中文变 `\uXXXX`）。**`double.NaN` 写不进 JSON**：`FloatingLeft/Top` 默认 NaN → 序列化抛异常而 `Save()` 容错 → 「新装设置怎么改都存不下去」毫无痕迹。用 `AllowNamedFloatingPointLiterals`，**容错的 catch 也要记日志**。
- **窗口尺寸与内容缩放必须出自同一系数**：`RootGrid.LayoutTransform = ScaleTransform` + 取整后实际尺寸反算；只改 `Width/Height` 则放大一片空、缩小时内容被裁。`LayoutTransform` 外的元素读 `ActualHeight` 是**变换前**值（永远 168）→ 用 `TranslatePoint` 量两点差。
- 别用 BCL 常见类型名当方法名（`Key`/`Point`/`Color`/`Brush`/`Path`/`Task`）—— `Key(DateTime)` 会把 `Key.Enter` 顶掉（CS0119）。回调参数别用 `Action<...>`（不支持可选参数）。`CollectionViewSource` 分组先后取决于集合里的顺序 → 先排好序。取值范围常量只写一份（放 `AppSettings`，滑杆 `x:Static` 绑）。
- **任何「隐式规则」都要显示在界面上**：归档横幅写明目标日期；完成度的"今天实时/过去定格"靠徽标+ToolTip 讲清（ToolTip 还要说"改了会重定格"）。

### 命令行与单实例
- 开关取值要判断下一个参数是不是另一个开关（`Services/CommandLine.cs`）：`--export-assets --quiet` 曾把 `"--quiet"` 当目录名。开关匹配要**同时认 `--x` 与 `--x=值`**（只做全等会让 `--shot=路径` 被当成没这开关，程序按普通方式启动）。新增开关要在自测补解析断言。
- 一次性命令（`--selfcheck`/`--shot*`/`--create-shortcut`/`--export-assets`）**必须绕开单实例互斥**，也要跳过数据写入：`App.OnExit` 的 `Store.Save()` 被 `_oneShot` 挡住（否则"跑趟自检"就把 `index.json` 整个重写+留新 backup）。⚠️ 例外：悬浮窗拖放探针会**真的**写一条归档再撤回，所以跑完自检索引 mtime 仍变、条数不变。

## 三、素材机制
- 界面引用素材一律 `Source="{v:AssetImage <名字>}"`，**不写 `pack://` URI**（会绕过「外部优先」解析）。**「存在」与「可用」分开判断**：`AssetCatalog.FindExternal` 只查在不在，能否解码看 `IconAssets.LoadedFromExternal`；报告要写清用外部还是内置。首次运行导出出厂素材到 `Assets\`（不覆盖已存在）+ `素材说明.txt`。
- 形象包（09-20 起）：`Assets\characters\<ID>\`；`classic` 是保留名、根就是 `Assets\`。**素材必须躺在 `WhaleLog.exe` 旁边**（`AppPaths.AssetsDir` = 程序目录\Assets），csproj 1.4.1 起把仓库 `assets\characters\` 拷进编译产物；否则设置页「溟月」按钮灰着、点了没反应。`IconAssets.LoadPackOnly`：**没有差分图时不回落内置闭眼图**（否则"换了形象、鼠标一移又变回旧鲸鱼"）。
- 透明视频动作（09-20 起）：VP9-alpha webm **原样搬运不重编码**（转精灵图 PNG ≈1.4 GB 不可行），运行时 ffmpeg 子进程解码。
  * ⚠️ **必须点名 `-c:v libvpx-vp9`，否则 alpha 整条丢**（1.4.2 修）：VP9 的 alpha 不在像素里而在 matroska **附加数据**（`BlockAdditional`，`ffmpeg -i` 见 `ALPHA_MODE: 1`），ffmpeg 默认挑的 **native vp9 解码器不认它** —— 照样吐 RGBA、帧数全对、**alpha 全填 255 且不报错**。点名后 `yuv420p`→`yuva420p`，代价 18%。探测 `IsVp9Webm`（读头 64 KB 搜 `V_VP9`）+ `FfmpegSupportsVp9Alpha`（问一次 `-decoders` 后缓存），两条都满足才加点名参数。→ 与「PyAV 静默丢 alpha」同类：**解码器可以选择性无视附加数据**。
  * ⚠️ **"颜色不对"别往色彩空间想**：两种解码器解的实心像素**逐像素最大差值 0**，色偏是黑底造成的对比错觉。**先测，再猜。**
  * ⚠️ **裸字节必须和贴图字节序对齐：`-pix_fmt bgra` 不是 `rgba`**（1.4.3 修）：`rgba` 是 R,G,B,A，WPF `Bgra32` 内存是 B,G,R,A → 红蓝整条对调（深蓝紫头发变栗棕红）。常量 `VideoFrameStream.PixelFormat` + `OffsetB/G/R/A`；`SpritePlayer` 那行上方写着「两处是一对，改一边必须改另一边」。**凡"外部工具产像素→本程序贴图"，先验一次字节序。**
  * ⚠️ **"解出来了"≠"解对了"**：断言帧数/每帧字节数 → alpha 全丢时全过；断言"有没有透明像素" → 颜色全反时全过。**缺陷都是"结构对了、内容错了"**，断言要落到**画面内容**上。⚠️ **用户说的每句现象都要当真**（"像反色""只有切换时开头几帧是对的"）—— 那是定位证据（"开头几帧对"= 切换后先是 WPF 自解码静态图，视频帧要等 ffmpeg 起 ≈150 ms）。
  * `VideoFrameStream` 8 帧环形缓冲，满则阻塞生产者；一 tick 最多贴一帧、不快进。冷启动 ≈150 ms → 鼠标移入悬浮窗就预热（`SpritePlayer.Warm`）。测无限流（`-stream_loop -1`）**别指望 `-frames:v N` 兜底**（ffmpeg 可能不停，实测写到 **35 GB**），读够就杀进程。
  * **角色由目录名前缀认**（`idle-`/`clickBounce-`/`ambient-`/`drag-`/`move-`/`turn-`），必须从目录名记，**不能从动作键反推**（`idle` 那支的键归一化成 `blink`）。
  * ⚠️ **`filter` 当不可信输入校验**（防注入）：**先按 `=` 切滤镜名、再按 `:` 切参数**；顺序反了会把 `crop=384:360:128:0` 的**名字**切成 `crop=384` → **106 个动作全判非法**，界面只表现"动画一个都不出来"。切分隔符的活**先写用例再写实现**。
  * ⚠️ 许可：CC BY-NC-SA 同人素材（OC「溟月」/画师上善无形）**仅限个人非商业、必须署名**；发布/商用必须整体撤下 `assets\characters\dsh\`（撤下后自动退回原版，有自检断言守着）。

## 四、自包含打包（离线）
正路 `publish -r win-x86 --self-contained` 需 NuGet 运行时包，本机没有 → `tools/make_portable_zip.py` 手工拼。
⚠️ **不要再引入"暂存目录"**：受限环境会拦「一次删 50 个以上文件」，**被拦下的删除请求会在后台被逐个补执行** —— 曾把暂存树 `Assets\` 以约 17 个/秒删掉，打出「程序在、素材没了」的包且**脚本不报任何错**。现在脚本**直接把源文件写进 zip**（生成的 json 用 `writestr`，出厂素材导出到系统临时目录），**落盘后回查关键条目**，缺一条 `return 1` —— 改这脚本务必保住这个自检。
- 三块料：应用本体 + `hostfxr.dll`（`dotnet/host/fxr/<ver>/`，共享框架目录里没有）+ 两个共享框架全部文件；`runtimeconfig.json` 用 `includedFrameworks`。**`WhaleLog.deps.json` 必须列出全部框架程序集**，否则 `Could not resolve CoreCLR path`（托管/原生 DLL 用 PE 头第 14 个数据目录区分）。两个框架有 3 个同名程序集（`WindowsBase`/`System.Drawing`/`Microsoft.VisualBasic`），必须让 WindowsDesktop 版本覆盖 NETCore 空壳。验证：自检「运行环境」报运行时位置；再把 `DOTNET_ROOT` 指到不存在路径跑一遍。
- **1.4.0 起包内多两样**：`Assets\characters`（51.9 MB）与 `ffmpeg.exe`（约 84 MB，按 `WHALELOG_FFMPEG` → `assets\tools\` → PATH → `imageio_ffmpeg` 搜）。都是已压缩内容、zip 压不动，所以包从 58 MB 涨到 **139.6 MB** —— 不是脚本算错了。⚠️ ffmpeg 是 GPL 授权的独立可执行文件，包内 README 必须写明。

## 五、探针/自检纪律
- **探针必须无副作用**：①「创建快捷方式」曾直写桌面 → 现在在 `logs\shortcut-probe\` 下试创建、验完删掉，桌面快捷方式只读并报告指向。②探针不能把改动留在设置对象里（退出 `Save()` 会把内存设置整个写回文件，真出过不透明度被写成 1）→ `RunReport`/`WriteFloatingShot` 都要 `Snapshot()`+`Restore()`，模拟拖动后 `CancelPendingSaves()` 掐掉 700ms 节流。③**探针的数据改动也要**：灌假数据走 `LoadEntriesForProbe`（`_probeMode` 期间关掉一切写入），不要往用户记录里塞测试条目。
- 探针依赖的前置状态要显式设好（悬浮窗缩放探针先关 `autoCollapse`，否则量到的全是收起态 48px）。悬浮窗 `ClampToArea()` 会把越界位置拉回工作区 → 用 `BackgroundProbeMode=true` + `ShowActivated=false` 并断言「窗口落在屏幕外」。量位置要量「卡片本身」而非 ItemsControl 容器（外边距在容器内部）。
- **界面改动的验证套路**：① 自检加探针（离屏窗口 → 需要时先 `SwitchTo(页面)`，`Collapsed` 时不生成条目容器 → 按控件真实交互方式模拟）；② 断言**可视树**而非数据（数 `GroupItem`、要求文字 `ActualWidth/Height` 非零、读控件自己的值/颜色/位置）——「视图对象里有分组」≠「看得见」；③ 只能看的东西（裁切/配色/对齐）用 `--shot` 出图，没真实数据就用 `WHALELOG_DATA_DIR` 指临时数据。**位置类断言要量两个控件的相对差**（`TranslatePoint` 到窗口，差 ≈ 0 才算对齐），别只看绝对值。
- ⚠️ **对齐类断言的基准要选"外部控件"，别拿控件自己的属性**（10-14）：`DayCompletionRow` 是 `BorderThickness=1`，内容实际从「行 x + 边框 1 + Padding 12 = +13」开始，断言写成 `tx - x == pad` 差 1 像素就挂。改成拿**上面每一行勾选框的左边界**当基准（既不自证、也正好是"最左边"的意思）。**自检第一次跑就抓到了它** —— 这正是探针该干的活。
- 纯逻辑（日期解析、分组排序、完成度算法、文案）放非界面文件（`Services/DateGrouping.cs`、`Models/Notes.cs`），自测直接编译断言；界面那一半在 `--selfcheck` 探针里，两半分工写清楚。交互行为（拖拽/点击、贴边、收起）只能靠合成真实输入测。
- **界面层断言要问「这个控件点得动吗」，不能只问「背后的函数返回对吗」**（09-20 用户报"设置里点新形象没反应"：13 条纯逻辑断言全过，真实情况是按钮 `IsEnabled=false`）。**点一个已选中的 RadioButton 不会重抛 `Checked`**（true→true 无变化）→ 必须点在**没被选中**的那个。**凡有"像素/内容正确性"要求，断言要数「内容」不能只数「结构」**（视频那节旧断言只有"解出多少帧/每帧多少字节"，alpha 全丢时全过，黑底就这样混进 1.4.0/1.4.1 两个出厂包）。
- **自检探针里的聚合调用要有空集合守卫**（10-14）：归档日历那节 `.First()` 在零归档数据目录下抛 `Sequence contains no elements`，被 catch 吞成一条红 —— 干净数据目录下必然踩到，直接让"清屋验证"失效。凡 `First()/Max()/Min()` 先想一句"数据为空时返回什么"。
- ⚠️ **新写的断言要做一次反向验证**：把修复关掉重编，确认它**真的会挂**（否则只是永远通过的装饰）。实测：改坏"过去用定格值"→ 自测挂 1 + 自检挂 2；汇总条列宽 42→24 → 自检挂「同列」1 条（差 17.7px）；去掉定格幂等 + "补算不记时刻" → 自测挂 9 条。
- **靠"运行时环境"决定成败的功能，断言和自检都测不到，必须真跑一次 + 配反向验证**（1.4.4）：`DragEventArgs` 构造函数是 internal、造不出假事件 → 处理函数全对、命中测试全错时自检**全绿**。靠 `tools/drag_probe.py`/`drop_probe.py` + `tools/dragsource/`（三场景含"窗口外不该接住"的对照组）。**判据落在"用户能看见的结果"上**：拖放只看「主面板开没开 + 文件进没进当天归档」，**不看 `DoDragDrop` 返回值**（桌面也是合法拖放目标，`effect` 恒为 `Copy`，看它会假绿）。
- ⚠️ **`AllowsTransparency` 的层叠窗口逐像素看 alpha 做命中测试**：alpha=0 的像素上鼠标消息和 OLE 拖放会**穿透到下层窗口** —— 拖到鲸鱼旁边空白处什么都不发生且不报错（鲸鱼本体上却是好的 → 看着像"时灵时不灵"）。解法：根 `Grid` 背景从 `Transparent`(alpha=0) 改成 `#01000000`(alpha=1)；提示层反过来设 `IsHitTestVisible="False"`。**给悬浮窗加交互先问"这块像素是不是全透明"。**
- **UI 自动化端到端**（验"真点真改"，细节见 `2026-10-14.md`）：PowerShell 载 `UIAutomationClient` 在**真窗口**上点按钮改文本，最后回读 `index.json` 判成败。三个必须：`SetProcessDPIAware()`（本机 150% 缩放，DPI-unaware 的 `SetCursorPos` 收到虚拟化坐标 → 点到空处）；按钮走 `InvokePattern.Invoke()`；`WindowStyle=None`+`AllowsTransparency` 的窗口不在 UIA 的 `RootElement.Children` 里 → 先 `EnumWindows` 拿 HWND 再 `AutomationElement.FromHandle`（最小化态 `IsIconic=True` 时矩形是 -21333 → 先 `ShowWindow(hwnd, SW_RESTORE)`）。跑法 `Set-ExecutionPolicy -Scope Process Bypass` + `& "文件.ps1"`（`Invoke-Expression` 被拦；ps1 存 **UTF-8 with BOM**）。
- bash 的 `kill` 杀 GUI 不走 `OnExit`（`Settings.Save()` 不执行）；会写文件的是主动 `Shutdown()` 的一次性命令。验证配置没被改要**打印具体数值**，只比 md5 可能两边都已被改过而看不出。调试节奏：**先在 Debug 版验功能**（输出目录不同，不被运行中的程序锁），最后才切 Release。

## 六、本机环境限制
- **`dotnet restore` 报 `Value cannot be null. (Parameter 'path1')` 时先试 `tools/mkenv.py`。** 真因是会话里的 bash 拿不到 Windows 环境变量（`ProgramData`/`APPDATA`/`ALLUSERSPROFILE` 全缺），NuGet 取不到 `CommonApplicationData`。`python tools/mkenv.py` 读注册表导出 `.tmp-env.sh`，`source` 后 0 错 0 警。→ **先找可变量，再下"不可行"的结论。** 走不通才用离线兜底 `tools/offline_restore.py` + `-p:SkipResolvePackageAssets=true -p:GenerateDependencyFile=false -p:GenerateRuntimeConfigurationFiles=false`；**第三次 `offline_restore.py` 不能省**（否则没 `runtimeconfig.json`，启动报 `hostpolicy.dll not found`）。
- `dotnet.exe` 是 32 位宿主（`C:\Program Files (x86)\dotnet\dotnet.exe`，**不在 bash PATH 里**，用完整路径加引号）→ **项目不要设 `PlatformTarget=x64`**（FileLoadException），用 AnyCPU。
- ⚠️ **从 bash 启动鲸记必须先补 `SystemRoot`/`windir`**（受限会话里为空）：WPF 初始化要拼字体缓存路径，拿不到就抛 `Invalid URI: ...`，**偶发崩溃**，看着像"随机闪退"。`tool\*.py` 的 `child_env()` 已处理。**不能从 bash 调 `cmd.exe`**（安全策略）→ `build-whalelog*.cmd` 在只能执行 bash 的会话里跑不了，要手工复现它四步。本会话 PowerShell 工具不返回 stdout，读输出优先用 bash 或直接看文件。
- **给 .NET 程序传路径必须用 Windows 风格**（`C:/...`）：Git Bash 里 `$PWD` 是 `/c/Users/...`，.NET 不认 → **静默失败**（退出码 0、不报错、不生成文件）。凡"跑条命令看结果"，**判定标准看产物文件，不看退出码**。
- **本机没法 `git push`**：`api.github.com` 通但 `github.com` 被代理拦（`CONNECT tunnel failed 502`）。**平台 GitHub 连接器只有读权限**（写文件/建仓库一律 403）—— ⚠️ 别用"往不存在的仓库写返回 404"推断有写权限（看不到的仓库同样 404），判权限必须看**仓库存在之后**的返回码。
- **bash 命令可能被「沙箱重跑」一次**（输出末尾 `⚠️ Sandbox bypassed`）：第一次已执行成功，第二次得到 `nothing to commit` 退出码 1 → 长 `&&` 链当场中断、后面一步都没跑 → 收尾类命令别用长 `&&`，改用 `;` 并最后**显式回查结果**。

## 七、发布例行
- 版本唯一来源 csproj 的 `<Version>`。**所有产物统一放 `E:\jingji`**；两个打包脚本不带参数就输出到这里，目录不存在自动创建，E: 盘不存在则退回桌面并在输出里说明。命名带版本号 `E:\jingji\鲸记 WhaleLog <版本>.zip`；交接包 `鲸记 WhaleLog 项目交接包 <当天日期>.zip`。
- 顺序：升版本 → 重建 Release + apphost → 打包 → **同步 HANDOFF 与 README 里所有数字** → 提交。1.4.0 起多一步：打包前把 `ffmpeg.exe` 放进 `src\WhaleLog\bin\Release\net8.0-windows\` 并跑一次 `--selfcheck` 确认「透明视频」那节真解出了帧。
- ⚠️ **打包前先确认没有 WhaleLog 实例在跑**（`tasklist | grep -i whalelog`）：被占用时 `dotnet build` 报 `MSB3027/MSB3021 文件被锁定`，前面编译其实已成功、只有最后拷 apphost 那步失败 —— 别误判成代码编译不过。杀实例属用户可见操作，**要先问**。
- **文档里的项数必须 grep 校验**（交接单断言数漂过 151→163→213→232→294，每次都是改代码忘了改数字）。**桌面属于个人目录：删桌面上的旧包必须用户明确同意**。
- 2026-10-10 已发 1.4.7（包 139.6 MB，`E:\jingji\` 与桌面各一份）。⚠️ 桌面那个 1.4.7 包**不含**「当天完成度」，要含新功能必须重打。

## 八、Git 远端
- 远端 `https://github.com/yangdong19961116-cyber/whalelog.git`，**公开空仓（零 ref，默认分支还没建）**。
  ⚠️ 09 月记的「私有」是**错的**（私有仓匿名访问不会返 200）；本地分支已 `git branch -M main`。
- **`github.com:443` 是「时间性」间歇封锁**（2026-10-16 反复实测）：
  * 同一个 IP 在几秒内能在 **0/12 ↔ 12/12** 之间跳变；换 IP（`140.82.112.3` / `140.82.113.3` / `20.27.177.113`）
    同样会撞坏窗口。**不是「DNS 指错 IP」，改 hosts 没用** —— 那条「DNS 指到被封 IP、换成别的 IP 就通」
    是正好落在一次 12/12 好窗口里得出的，**已被推翻**。
  * `hosts` 本会话**无管理员权限写不进**（`-w` 测试骗人：能判断可写，实际 append 报 PermissionError，
    且不能在 `etc\` 下新建备份文件）。所以**没改 hosts**（md5 `3688374325b992de…` 前后一致）。
  * ICMP 能 ping 通、TCP:443 被丢；同一时刻 `api.github.com` / `baidu.com` / `example.com` / `microsoft.com`
    全部正常 → 就是针对 `github.com:443`。
  * **重试不是可靠办法**：裸连 `git ls-remote` 连续 9 次全败（约 90 秒）。
  * 其它域名：`api.github.com`(0.4 s)、`codeload.github.com`(301)、`objects.githubusercontent.com`(404) **通**；
    `raw.githubusercontent.com` **不通**。注入的 `HTTPS_PROXY=127.0.0.1:5574` 对 github 回 **502** → 跑 git 前剥掉。
  * 试过又放弃的：本地 CONNECT 隧道把 github.com 钉到 `.112.3`（`tools/github_tunnel.py`，**已删**）——
    接通一次后照样在坏窗口里超时，因为封锁与 IP 无关。hosts 备份文件也一并删了。
- ✅ **正路：`tools/push_via_api.py`（2026-10-16 新增）** —— 走 `api.github.com` 的 Git Data API
  把本地提交原样搬上去（blob → tree → commit → ref），**支持完整历史重放**（本仓 66 个提交 / 833 个 blob；
  `--squash` 压成 1 个提交则约 400 次请求）。每个 commit 都带**原始的 author / committer / 日期 / 提交信息**，
  而 GitHub 的 blob/tree/commit 用同一套 SHA-1，所以远端 sha 应与本地**逐个相等**，脚本结尾会核对并报出来。
  `--dry-run` 只读本地、不碰网络。本机 Python **没有 `requests`**，脚本用 stdlib `urllib`。
- **第二道关是凭据**：本机无 `~/.ssh`、无 `.git-credentials`；`credential.helper=helper-selector` 非交互
  弹不出选择框 → `could not read Username` 挂到超时。走 API 要 PAT（经典 PAT 勾 `repo`，
  或细粒度 PAT 的 `Contents: Read and write`）。
- **2026-10-16 下午实测：镜像可以推送，"镜像只读"这个假设是错的**（这条推翻了本文件早先的默认认知）。
  在**代理开着**的环境（当时 `https_proxy=127.0.0.1:6354`，端口每次会话都可能不同）实测：

  | 端点 | 结果 | 含义 |
  |---|---|---|
  | `api.github.com` | **200** | 通，方案 B 可用 |
  | `github.com`（`info/refs?service=git-upload-pack`） | **502 CONNECT tunnel failed** | 代理层直接拒了 CONNECT |
  | `ghproxy.net/https://github.com` 读（upload-pack） | 200 | 可读 |
  | `ghproxy.net/...` 写（**receive-pack**） | **401** | ⭐ **push 端点在，只是要认证** |
  | `ghfast.top/https://github.com` 读写 | 200 / **401** | 同上 |
  | `hub.gitmirror.com` / `github.kkgithub.com` | 502 | 代理连不上 |
  | `gitclone.com` | 502 | 不可用 |

  **判据是关键**：`401 Unauthorized` ≠ `403/404/405`。401 说明端点存在、只差身份；
  而且响应头里带着 **`www-authenticate: Basic realm="GitHub"`** —— 这个挑战是
  **GitHub 自己发的、代理原样透传**，证明请求真的被转发到了 GitHub。
  `git push --dry-run` 走镜像的报错也是 `could not read Username for 'https://ghproxy.net'`
  （**认证层**失败），而不是超时/502（网络层失败）。→ **镜像路径通，瓶颈从来不是网络。**
- ⭐ **另一条不用 PAT 的路：`ssh.github.com:443` 实测握手成功**（不只是 TCP 通）：
  `ssh -T -p 443 git@ssh.github.com` 返回 `Permission denied (publickey)` ——
  这是 **SSH 协议层**的应答（ED25519 host key 都换到了），说明整条隧道可用，
  只差一对本机密钥 + 把公钥加到 GitHub 账号。`~/.ssh` 目前不存在（可以生成）。

  ⚠️ **安全取舍（重要）**：走镜像 push 等于**把 PAT 交给第三方代理**，它理论上能看到
  `Authorization` 头。方案 B（`api.github.com`）和方案 C（SSH）的凭据**只发给官方域名**，
  不经过第三方 → **能选就别走镜像**。而且用细粒度 PAT（只给这一个仓库 `Contents: Read and write`）
  比经典 `repo` PAT 权限小得多。
- 2026-10-16 之后本地积累了若干未推提交（当时 69 个，最新看 `git log --oneline`），
  三条路都只差凭据，等用户给一个就能一次推上去。
- `workwork` 是双项目工作区：AudioRoute（`src/AudioRoute/`，已完成）+ WhaleLog（在做）。建基线时 AudioRoute 一并进仓库；其 README 在 `src/AudioRoute/README.md`。

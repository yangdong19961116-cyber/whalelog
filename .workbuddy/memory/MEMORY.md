# 工作区 MEMORY（索引 + 高频要点）

`C:\Users\Administrator\WorkBuddy\workwork\`：两个独立的 WPF + .NET 8 应用（AudioRoute / WhaleLog）。
**细节全在 `MEMORY-detail.md`（同目录，按主题分节）；本文件只放高频要点。** 每日过程记录 `YYYY-MM-DD.md`。

---

## 一、当前状态

**WhaleLog 鲸记**（每日工作记录 + 文件归档台）：版本唯一来源 `src/WhaleLog/WhaleLog.csproj` 的 `<Version>`
（**当前 1.4.8**）。三页（今日记录/文件归档/设置）+ 桌面悬浮窗 + 托盘常驻；记录每天一份 md。
**1.4.8 已发**（2026-10-15；`E:\jingji\鲸记 WhaleLog 1.4.8.zip` + 桌面各一份，139.6 MB / 465 条目）：
① 进度条 + 未完成自动顺延 ② 改已写下的记录 ③ 当天完成度（**汇总条已去掉，改一行左对齐的字**）
④ 回看过去改回未完成 → **当场**同步到今天 ⑤ 编辑时**顺顺延链双向同步**（只同步正文/分类/标签，进度不动）
⑥ 往期清单：顺延过的灰色「· N 已顺延」，今天/将来的照旧橙色「· N 未收尾」。
- ⚠️ 重建 Release 前先**问用户**能否关掉在跑的实例（否则 MSB3021/MSB3027）。
- ⚠️ 改了 `<Version>` 必须 `--no-incremental` 重编（增量不重编 XAML，BAML 会留旧版本号）。
- ⚠️ 桌面 / `E:\jingji` 上各版本包**并存**，不删旧的。
- **硬性约束：绝不移动/重命名/删除用户原文件**。快照模式默认关闭。

**AudioRoute**（Win10 音频设备切换托盘，`src/AudioRoute/`，完成态）。

---

## 二、高频要点（详版见 detail）

### 起手必看
- 构建 `build-whalelog.cmd` / `-offline.cmd`；受限会话 restore 报 `path1` null → `python tools/mkenv.py` + `source .tmp-env.sh`。
- 自测 `tools/selftest/`（**602 项**，Debug/Release 都跑；**新增源文件要同步加进 `selftest.csproj`
  的 `<Compile Include>`**，否则 CS0103）。
- 自检 `WhaleLog.exe --selfcheck [--quiet]` → `%APPDATA%\WhaleLog\logs\selfcheck.log`（**31 段 / 隔离数据
  374 项**，看「结论：全部通过」；**脚本里必须带 `--quiet`**；ffmpeg.exe 要在程序旁）。
- 出图/试跑用 `WHALELOG_DATA_DIR` 指临时目录，别动用户记录。
- 素材更新 `python tools/check_dsh_updates.py`（只比清单；码 0 没变 / 1 有变）。**10-15 查过 106/106 零更新。**
- 打包 `python tools/make_portable_zip.py`（默认 `E:\jingji`，名带版本号）。
- **先在 Debug 版验功能**，最后才切 Release；**同一文件多处 Edit 必须串行**。

### 纪律速查（每条背后都有踩过的坑，细节在 detail）
- 会改控件值的事件处理器都要 `_syncing` 守卫；**两个 setter 不能互调**（`Progress` ↔ `Done` 成环）。
- **ToggleButton 不互斥**：单选取唯一 source of truth，目标值从 sender 推导
  （`clicked.IsChecked == true ? target : 当前值`，末尾 `: 当前值` 必须有）。
- `TextBlock.Foreground` 与 `Control.Foreground` 是两个 DP；**`double.NaN` 写不进 JSON**
  （用 `AllowNamedFloatingPointLiterals`，容错的 catch 也要记日志）。
- JSON 一律 `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`；给 .NET 传路径必须 `C:/...` 风格
  （`/c/...` 静默失败）→ **判定看产物，不看退出码**。
- **只写一份真相**：范围常量在 `AppSettings`；完成度算法只在 `DayCompletion.Average`；`AddedDate` 是
  「某天有哪些归档」的唯一依据（改它=同时改七处）。**隐式规则都要显示在界面上。**
- **新断言必须反向验证**（关掉修复重编，确认真会挂）；改坏/还原用**带上下文的唯一片段**，改完 `git diff` 复核。
- 对齐断言基准选**外部控件**；位置断言量**相对差**；断言要数「内容」不只「结构」；界面断言要问「控件点得动吗」。
- ⚠️ 脱离可视树的控件 `SetResourceReference` 解析不出画刷 → 探针只能比**资源键**。
- **探针必须无副作用**；`AllowsTransparency` 窗口按 alpha 命中测试 → 全透明像素会**穿透**，
  根 Grid 背景用 `#01000000`。

### 归档粒度（最容易改坏的一条）
**一个路径 × 一天 = 一条**：次日再归档是**新记一条**，同一天反复改只升当天版本号。
踩过：升版本没同步 `AddedDate` → 新版本永远粘在首次归档那天
（修：`tools/repair_archive_dates.py`，默认预览、`--apply` 才写）。

---

## 三、本机环境限制
- `dotnet.exe` 不在 bash PATH：`"C:/Program Files (x86)/dotnet/dotnet.exe"`；项目用 **AnyCPU**（x64 会 FileLoadException）。
- 🔴 从 bash 启动鲸记必须先 `source <repo>/.tmp-env.sh`（缺 `SystemRoot`/`windir` → WPF 起不来）。
- 不能从 bash 调 `cmd.exe`（安全策略）→ `build-whalelog*.cmd` 跑不了，手工复现那四步。
- 🟢 **GitHub 网络本身是通的**（10-15 实测：直连 `github.com` 1.2 s 返 200，`git ls-remote` 成功）。
  推不上去的真因是**没有凭据**（`credential.helper=helper-selector` 非交互弹不出框 →
  `could not read Username` 卡到超时），且远端 `yangdong19961116-cyber/whalelog` 是**公开空仓**（零 ref）。
  另：环境注入的 `HTTPS_PROXY=127.0.0.1:5574` 对 github 会**传一半断流** → 跑 git 前先剥掉 `HTTPS_PROXY/HTTP_PROXY`。
- bash 命令可能被「沙箱重跑」一次（末尾 `⚠️ Sandbox bypassed`）→ 收尾别用长 `&&`，用 `;` 并回查结果。
- 本会话 PowerShell 工具不返回 stdout → 用 bash 或直接看文件。

---

## 四、发布例行
升 `<Version>` → 重建 Release + apphost（打包前把 `ffmpeg.exe` 放进 `bin\Release\net8.0-windows\`
并跑一次 `--selfcheck` 确认「透明视频」真解出帧）→ 打包到 `E:\jingji` + 桌面 → **grep 校验 HANDOFF/README
所有数字** → 提交。
- ⚠️ 打包前 `tasklist | grep -i whalelog`（被占用报 MSB3027/MSB3021，别误判成编译不过）；**杀实例先问用户**。
- ⚠️ 复核 `grep -c "<旧号>" bin/Release/net8.0-windows/WhaleLog.dll` 应为 0；`--no-incremental` 前
  备份同目录 `ffmpeg.exe`（84 MB，Clean 会删）。
- **核销**：包内 `WhaleLog.dll` md5 == `bin/Release/.../WhaleLog.dll`；再解压到 temp 跑包内 exe 的
  `--selfcheck --quiet`（带 `WHALELOG_DATA_DIR`），看「结论：全部通过」。
- **桌面属个人目录：删旧包必须用户明确同意。**

## 五、Git
- 远端 `https://github.com/yangdong19961116-cyber/whalelog.git`（**公开空仓，默认分支还没建**）；
  本地 `master`，推之前 `git branch -M master main`。**推送要先配 PAT**。
- `workwork` 双项目：AudioRoute + WhaleLog 同仓。

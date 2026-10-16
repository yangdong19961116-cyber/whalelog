# 工作区 MEMORY（索引 + 高频要点）

`C:\Users\Administrator\WorkBuddy\workwork\`：两个独立 WPF + .NET 8 应用（AudioRoute / WhaleLog）。
**细节全在 `MEMORY-detail.md`（同目录）；本文件只放高频要点。** 过程记录 `YYYY-MM-DD.md`。

## 一、当前状态

**WhaleLog 鲸记**（每日工作记录 + 文件归档台）：版本唯一来源 `src/WhaleLog/WhaleLog.csproj` 的 `<Version>`
（**当前 1.4.9**）。三页（今日记录/文件归档/设置）+ 桌面悬浮窗 + 托盘常驻；记录每天一份 md。
**1.4.8 已发**（10-15）：进度条 + 未完成自动顺延、改已写记录、当天完成度、回看改回未完成同步今天、
编辑时顺延链双向同步、往期清单「已顺延/未收尾」。
**1.4.9 已发**（10-16，`E:\jingji\` + 桌面，139.7 MB / 465 条目）：托盘菜单「播放动画…」二级页
（一级只多一项，二级 = 搜索框 + 按角色分组，选中会自动打开桌面小鲸鱼）。
行清单是纯函数 `Services/AnimMenu.cs`；出图 `--shot-anim-menu`。**暂无未发版功能。**
- ⚠️ 重建 Release 前先**问用户**能否关掉在跑的实例；改了 `<Version>` 必须 `--no-incremental` 重编
  （增量不重编 XAML，BAML 会留旧版本号）；桌面 / `E:\jingji` 上各版本包**并存**，不删旧的。
- 核版本真进了产物：读 dll 里 `1.4.9.0` 的字节数、上一个版本号必须是 0（**别只看文件时间**）。
- **硬性约束：绝不移动/重命名/删除用户原文件**；快照模式默认关闭。

**AudioRoute**（Win10 音频托盘，完成态）。

## 二、起手必看

- 构建 `build-whalelog.cmd` / `-offline.cmd`；受限会话 restore 报 `path1` null → `python tools/mkenv.py` + `source .tmp-env.sh`。
- 自测 `tools/selftest/`（**632 项**）；**新增源文件要同步加进 `selftest.csproj`**，否则 CS0103。
- 自检 `WhaleLog.exe --selfcheck --quiet` → 日志在**数据目录**下 `logs\selfcheck.log`（不在 %APPDATA%）；
  **脚本里必须带 `--quiet`**；`ffmpeg.exe` 要在程序旁。
- 出图/试跑用 `WHALELOG_DATA_DIR` 指临时目录，别动用户记录。素材更新 `tools/check_dsh_updates.py`；
  打包 `tools/make_portable_zip.py`。
- **先在 Debug 版验功能**，最后才切 Release；**同一文件多处 Edit 必须串行**。

## 三、纪律速查（细节见 detail）

- 会改控件值的事件处理器都要 `_syncing` 守卫；**两个 setter 不能互调**（`Progress` ↔ `Done` 成环）。
- **ToggleButton 不互斥**：单选取唯一 source of truth，目标值从 sender 推导
  （`clicked.IsChecked == true ? target : 当前值`，末尾 `: 当前值` 必须有）。
- `TextBlock.Foreground` 与 `Control.Foreground` 是两个 DP；**`double.NaN` 写不进 JSON**；
  JSON 一律 `UnsafeRelaxedJsonEscaping`；给 .NET 传路径要 `C:/...` 风格
  → **判定看产物，不看退出码**。
- **只写一份真相**（范围常量在 `AppSettings`、完成度在 `DayCompletion.Average`、
  `AddedDate` 是「某天有哪些归档」的唯一依据）；**隐式规则都要显示在界面上**。
- **新断言必须反向验证**；改坏/还原用**带上下文的唯一片段**，改完 `git diff` 复核。
- 对齐断言基准选**外部控件**；位置断言量**相对差**；断言要数「内容」不只「结构」；
  **出的图要亲眼看** —— 探针只查「有没有」，抓不到「排到列表底下」这类位置 bug。
- 探针**必须无副作用**；脱离可视树的控件 `SetResourceReference` 解析不出画刷（只能比**资源键**）；
  全透明像素会**穿透**（根 Grid 背景用 `#01000000`）。
- 归档粒度「**一个路径 × 一天 = 一条**」是最容易改坏的一条。

## 四、本机环境限制

- `dotnet.exe` 不在 bash PATH：`"C:/Program Files (x86)/dotnet/dotnet.exe"`；项目用 **AnyCPU**。
- 🔴 从 bash 启动鲸记必须先 `source <repo>/.tmp-env.sh`（缺 `SystemRoot`/`windir` → WPF 起不来）。
- 不能从 bash 调 `cmd.exe` → `build-whalelog*.cmd` 跑不了，手工复现那四步。
- ⚠️ **`github.com:443` 是时间性间歇封锁**（同一 IP 几秒内 0/12 ↔ 12/12，换 IP 同样撞坏窗口）→
  **改 hosts 没用**，且本会话无管理员权限写 `hosts`。
  ✅ 推代码走 **`tools/push_via_api.py`**（`api.github.com` 一直稳）；仍要 PAT（本机无任何凭据）。详见 detail「Git 远端」。
- ⭐ **镜像能推送（2026-10-16 实测，推翻了"镜像只读"的想当然）**：`ghproxy.net` / `ghfast.top`
  的 **receive-pack 端点回 401 且带 `www-authenticate: Basic realm="GitHub"`** —— 挑战是 GitHub
  自己发的、代理透传，即 push 请求真被转发到了 GitHub，只差凭据。
  ⚠️ 但**别优先走镜像**：等于把 PAT 交给第三方。凭据只发官方的两条路都实测可用：
  ① `push_via_api.py`（`api.github.com` 200）② **`ssh.github.com:443` SSH 握手成功**
  （返回 `Permission denied (publickey)` = 协议层通了，只差本机密钥 + 账号配公钥）。
- bash 命令可能被「沙箱重跑」一次 → 收尾别用长 `&&`，用 `;` 并回查结果。
- 本会话 PowerShell 工具不返回 stdout → 用 bash 或直接看文件。

## 五、发布例行 / Git

升 `<Version>` → `--no-incremental` 重建 Release + apphost（**先备份同目录 `ffmpeg.exe`**，Clean 会删）
→ 打包到 `E:\jingji` + 桌面 → 核销（包内 dll md5 == bin 产物；解压跑 `--selfcheck --quiet`）
→ grep 校验 HANDOFF/README 所有数字 → 提交。
- 远端 `yangdong19961116-cyber/whalelog`（**公开**，默认分支 `main`）。
  ✅ 2026-10-16 用 **SSH over 443** 把 70 个提交推完、远端 sha 与本地逐提交一致；
  **不再需要 PAT**（`~/.ssh/config` 把 `github.com` 路由到 `ssh.github.com:443`，细节见 MEMORY-detail 第八节）。
  兜底脚本 `tools/push_via_api.py` 留着备用。`workwork` 双项目同仓。
- **桌面属个人目录：删旧包必须用户明确同意。**

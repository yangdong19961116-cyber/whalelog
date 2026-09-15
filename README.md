# 工作区 · workwork

两个 Windows 桌面项目放在一起：

| 项目 | 状态 | 说明 |
|---|---|---|
| **鲸记 WhaleLog** | **在做** | 桌面工作记录台。小鲸鱼常驻桌面，拖文件进去归档，记录 / 复盘 / 周报在一个窗口里完成。`src/WhaleLog/` |
| AudioRoute | 已完成 | 托盘音频设备切换器：全局热键切默认输出设备，插上 2.4G 接收器自动切过去。`src/AudioRoute/`，说明见 `src/AudioRoute/README.md` |

---

## ⚠️ 这个仓库现在是「不完整」的 —— 先读这段

**仓库里目前只有文档，没有源码。** 这是有意为之，不是传错了。

| 情况 | 说明 |
|---|---|
| 现在有什么 | `README.md`、`HANDOFF-鲸记WhaleLog.md`、`新设备继续指令.md`、`.workbuddy/memory/MEMORY.md`、`LICENSE`、`.gitignore` |
| 为什么没有源码 | 上传这批文档的那台机器**出网受限**：`api.github.com` 通，但 `github.com` 被挡 —— `git push` 报 `CONNECT tunnel failed, response 502`（git 协议走的正是后者）。退而用连接器逐文件写也不行：它是把内容当文本参数传的，全仓 962 KB 文本 + 1 MB 图片这个量级传不动 |
| 怎么补全 | **在新设备上推一次全量**，一条命令的事，见 [`新设备继续指令.md`](新设备继续指令.md) 第 6 节 |

**所以现在别 `git clone` 它当源码用**（会是个空工程）。正确开工方式：解压
`鲸记 WhaleLog 项目交接包 2026-09-15.zip`（含全量源码 + 完整提交历史），
按 `新设备继续指令.md` 推上来。推完之后这个仓库就是唯一事实源。

---

## 从哪读起

| 想干什么 | 读哪个 |
|---|---|
| **在新设备上接着干** | [`新设备继续指令.md`](新设备继续指令.md) —— 操作手册，照着做 |
| 了解鲸记做到哪一步、下一步做什么 | [`HANDOFF-鲸记WhaleLog.md`](HANDOFF-鲸记WhaleLog.md) —— 交接单 |
| 技术决策与踩过的坑（两个项目合在一起） | [`.workbuddy/memory/MEMORY.md`](.workbuddy/memory/MEMORY.md) |
| 鲸记的功能全貌 / 构建 / 打包 / 自测 | `src/WhaleLog/README.md`（补全源码后可见） |
| AudioRoute 的用法 | `src/AudioRoute/README.md` |
| 动态素材规格（发给做素材的人） | `docs/动态素材规格.md`（补全源码后可见） |

## 鲸记 WhaleLog 速览

- **形态**：无边框、透明、置顶的桌面悬浮窗，贴着屏幕边缘，闲置收成小头像。
- **核心交互**：把文件拖到小鲸鱼身上 → 按日期自动归档。
- **辅助功能**：记录与复盘编辑、周报视图、全局快捷键、托盘菜单、浅色/深色主题、开机自启。
- **形象可换**：程序旁边的 `Assets\` 里放同名文件即可，不用重新编译。
- **动态素材**：精灵图 + 帧表（PNG + JSON）。已接点击弹跳 / 闲置眨眼 / 提醒三个动作，
  以及鼠标悬浮时的闭眼差分形象。

## 新设备上要注意的技术约束

- `net8.0-windows`，**AnyCPU**（不能设 x64）。
- 要装 **.NET 8 SDK**（只有 Runtime 不够）；`tools/make_apphost.py` 还需要本机的
  `Microsoft.WindowsDesktop.App` 运行时。
- 编译完要跑一次 `python tools/make_apphost.py` 生成 GUI 子系统的 exe，
  否则启动会挂一个黑控制台窗口。
- 诊断探针（`--selfcheck`）**绝不能写用户的真实配置**，必须用 `WHALELOG_DATA_DIR`
  隔离到临时目录，且路径要给 Windows 风格（`C:/...`）。

---

*本文件由交接流程维护。补全源码后，上面那段「不完整」的警告可以删掉。*
*2026-09-15：原先放在根目录的 README 是 AudioRoute 的，已归位到 `src/AudioRoute/README.md`。*

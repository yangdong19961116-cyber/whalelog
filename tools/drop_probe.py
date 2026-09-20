"""把文件真的拖到悬浮窗上 —— 端到端实测。

为什么必须有这个探针：文件拖放走的是 OLE，只有真的发起一次 DoDragDrop
才知道"窗口到底有没有接住"。这件事对悬浮窗尤其要紧 —— 它是
AllowsTransparency 的层叠窗口，Windows 对这类窗口的命中测试是**逐像素看 alpha**
的，全透明的地方拖放会直接穿到下面的窗口上去。断言测不出这个，
它只会在用户拖上去"什么都没发生"的时候才暴露。

判据是三个场景，其中两个是互为对照的：

    拖到鲸鱼**旁边**（窗口内的透明像素）→ 该接住   ← 这一条是关键
    拖到鲸鱼**身上**（不透明像素）      → 该接住
    拖到窗口**矩形外**                  → 不该接住  ← 对照组

"接住"的判据取三层，缺一不可：DoDragDrop 的返回值、主面板有没有被打开、
文件有没有真的进到当天的归档里。

用法：
    python tools/drop_probe.py            # 三个场景都测
    python tools/drop_probe.py --only 1   # 只跑某一个场景（排查时用）
    python tools/drop_probe.py --keep     # 保留临时数据目录，便于翻看
"""

from __future__ import annotations

import ctypes
import json
import os
import pathlib
import shutil
import subprocess
import sys
import tempfile
import time
from ctypes import wintypes

ROOT = pathlib.Path(__file__).resolve().parents[1]
EXE = ROOT / "src" / "WhaleLog" / "bin" / "Release" / "net8.0-windows" / "WhaleLog.exe"
DRAGSOURCE = ROOT / "tools" / "dragsource" / "bin" / "Release" / "net8.0-windows" / "dragsource.exe"
DATA = pathlib.Path(tempfile.gettempdir()) / "whalelog-dropprobe"

# 待拖的文件放在数据目录**外面**，而且名字不能和数据目录同前缀：
# 归档会主动跳过自己在数据目录里的文件（免得把 index.json 又拖回来归档），
# 判断是"路径前缀"，所以叫 whalelog-dropprobe-files 反而会撞上 whalelog-dropprobe。
FILES = pathlib.Path(tempfile.gettempdir()) / "whalelog-drop-src"

WIN_LEFT, WIN_TOP = 420, 300
SCALE = 1.0
EXPANDED_W, EXPANDED_H = 236, 296

MAIN_TITLE = "鲸记 WhaleLog"
FLOAT_TITLE = "鲸记小鲸鱼"

user32 = ctypes.WinDLL("user32")
user32.GetWindowTextLengthW.restype = ctypes.c_int
ENUMPROC = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)


def whale_windows() -> list[tuple[str, int, tuple[int, int, int, int]]]:
    """当前屏幕上所有鲸记窗口：(标题, 进程号, 位置)。"""
    found: list[tuple[str, int, tuple[int, int, int, int]]] = []

    def cb(hwnd, _):
        n = user32.GetWindowTextLengthW(hwnd)
        if n == 0:
            return True
        buf = ctypes.create_unicode_buffer(n + 1)
        user32.GetWindowTextW(hwnd, buf, n + 1)
        if MAIN_TITLE in buf.value or FLOAT_TITLE in buf.value:
            pid = wintypes.DWORD()
            user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
            r = wintypes.RECT()
            user32.GetWindowRect(hwnd, ctypes.byref(r))
            found.append((buf.value, pid.value, (r.left, r.top, r.right, r.bottom)))
        return True

    user32.EnumWindows(ENUMPROC(cb), 0)
    return found


def child_env() -> dict:
    """
    给被测程序准备一份环境变量。

    要紧的是补上 SystemRoot / windir：WPF 的字体缓存初始化
    （MS.Internal.FontCache.Util 的静态构造）要拿它去定位系统字体目录，
    取不到就在 System.Windows.Window 的静态构造里抛
    "Invalid URI: The format of the URI could not be determined"，
    窗口一个都建不出来。而从 agent 的 bash 环境继承下来的 os.environ
    里恰好没有这两个变量 —— 症状是"偶发启动崩溃"，极难归因。
    """
    env = dict(os.environ)
    env.setdefault("SystemRoot", r"C:\Windows")
    env.setdefault("windir", r"C:\Windows")
    return env


def kill_stale() -> None:
    subprocess.run(["taskkill", "/IM", "WhaleLog.exe", "/F"],
                   capture_output=True, encoding="utf-8", errors="replace")
    subprocess.run(["taskkill", "/IM", "dragsource.exe", "/F"],
                   capture_output=True, encoding="utf-8", errors="replace")
    time.sleep(1.5)


def launch() -> subprocess.Popen:
    shutil.rmtree(DATA, ignore_errors=True)
    (DATA / "notes").mkdir(parents=True)
    (DATA / "logs").mkdir(parents=True)
    FILES.mkdir(parents=True, exist_ok=True)

    (DATA / "settings.json").write_text(json.dumps({
        "floatingEnabled": True,
        "floatingScale": SCALE,
        "floatingOpacity": 1.0,
        "floatingSnapToEdge": False,     # 不吸边，位置完全可控
        "floatingAutoCollapse": False,   # 不自动收起，测的始终是展开态
        "floatingAnimations": False,     # 关掉动画：少一个变量（命中测试与动画无关）
        "floatingLeft": float(WIN_LEFT),
        "floatingTop": float(WIN_TOP),
        "workEndHour": 18, "workEndMinute": 0,
        "reminderEnabled": False,
        "themeOverride": 2,
    }, ensure_ascii=False, indent=2), encoding="utf-8")

    env = child_env()
    env["WHALELOG_DATA_DIR"] = str(DATA)
    proc = subprocess.Popen([str(EXE), "--tray"], env=env)
    time.sleep(5.5)
    return proc


def make_files(count: int = 2) -> list[str]:
    """造几个真文件。名字用中文，顺便验一遍路径里的非 ASCII 不会出问题。"""
    shutil.rmtree(FILES, ignore_errors=True)
    FILES.mkdir(parents=True)

    made = []
    for i in range(1, count + 1):
        p = FILES / f"季度报告 {i}.txt"
        p.write_text(f"这是第 {i} 个测试文件\n", encoding="utf-8")
        made.append(str(p))
    return made


def float_rect() -> tuple[int, int, int, int]:
    """
    等悬浮窗出现**并且已经是展开态**再返回。

    只等位置不够：刚启动那一小会儿窗口可能还没展开（收起态只有 60×60），
    这时候按展开态的坐标去拖，落点就跑到窗口外面了 —— 表现为"偶发接不住"。
    """
    seen: list[tuple[str, int, tuple[int, int, int, int]]] = []
    for _ in range(40):
        seen = whale_windows()
        for title, _, rect in seen:
            if FLOAT_TITLE in title:
                left, top, right, bottom = rect
                if (abs(left - WIN_LEFT) <= 30 and abs(top - WIN_TOP) <= 30
                        and right - left > 150 and bottom - top > 150):
                    return rect
        time.sleep(0.25)

    print(f"     期望悬浮窗展开在 ({WIN_LEFT}, {WIN_TOP})，实际看到的鲸记窗口：{seen}")
    logs = sorted((DATA / "logs").glob("*")) if (DATA / "logs").exists() else []
    if logs:
        print(f"     数据目录 logs/：{[p.name for p in logs]}")
        for p in logs:
            try:
                print(f"     --- {p.name} ---")
                print("     " + p.read_text(encoding="utf-8", errors="replace")[:800].replace("\n", "\n     "))
            except Exception as e:
                print(f"     读不了：{e}")
    raise SystemExit("悬浮窗没出现在预期位置，无法继续")


def drag_to(x: int, y: int, files: list[str], hold: int = 800) -> str:
    """真拖一次，返回 DoDragDrop 的 effect（Copy / None）。"""
    args = [str(DRAGSOURCE), "--to", str(x), str(y), "--hold", str(hold)]
    for f in files:
        args += ["--file", f]

    out = subprocess.run(args, capture_output=True, encoding="utf-8", errors="replace",
                         env=child_env())
    line = next((l for l in out.stdout.splitlines() if l.startswith("effect=")), "")
    effect = line.split("=", 1)[1].strip() if line else "(没输出)"

    # 拖放源的输出全打出来：它自己会报"光标实际落在哪、effect 是什么"，
    # 出问题时这几行就是唯一的线索
    for l in out.stdout.splitlines():
        print(f"     | {l}")
    for l in out.stderr.splitlines():
        print(f"     ! {l}")

    if out.returncode != 0:
        print(f"     ↳ 拖放源退出码 {out.returncode}"
              + ("（3 = 看门狗超时，DoDragDrop 挂住了）" if out.returncode == 3 else ""))

    time.sleep(2.5)          # 留给归档落盘和主面板创建
    return effect


def archived_files() -> list[dict]:
    """当天已经归档的记录。"""
    index = DATA / "index.json"
    if not index.exists():
        names = sorted(p.name for p in DATA.iterdir()) if DATA.exists() else "(数据目录都不在)"
        print(f"     index.json 不存在！数据目录里：{names}")
        err = DATA / "logs" / "error.log"
        if err.exists():
            print("     --- error.log ---")
            for line in err.read_text(encoding="utf-8", errors="replace").splitlines()[:8]:
                print("     " + line)
        return []

    try:
        book = json.loads(index.read_text(encoding="utf-8"))
    except Exception as e:
        print(f"     index.json 读不动：{e}")
        return []

    today = time.strftime("%Y-%m-%d")

    def add_date(f: dict) -> str:
        # index.json 用 camelCase 落盘（见 LogBookStore.JsonOpts 的命名策略）
        return f.get("addedDate") or f.get("AddedDate") or ""

    all_files = book.get("files", [])
    hits = [f for f in all_files if add_date(f) == today]
    if all_files and not hits:
        print(f"     index 里有 {len(all_files)} 条，但归属日期都不是今天（{today}）："
              f"{sorted({add_date(f) for f in all_files})}")
    return hits


def main_window_open() -> bool:
    return any(MAIN_TITLE in t for t, _, _ in whale_windows())


def finish(checks: list[tuple[str, bool, str]], keep: bool) -> int:
    kill_stale()
    print("\n" + "=" * 62)
    failed = [c for c in checks if not c[1]]
    for name, ok, detail in checks:
        print(f"{'✓' if ok else '✗'} {name}\n    实测：{detail}")
    print("=" * 62)
    print(f"结论：{'全部符合预期' if not failed else f'{len(failed)} 项不符合预期'}")

    if not keep:
        shutil.rmtree(DATA, ignore_errors=True)
        shutil.rmtree(FILES, ignore_errors=True)
    return 1 if failed else 0


def main() -> int:
    print(f"待测程序：{EXE}")
    for path, what in ((EXE, "主程序"), (DRAGSOURCE, "拖拽源")):
        if not path.exists():
            print(f"找不到{what}，先 build 一次：{path}", file=sys.stderr)
            return 1

    keep = "--keep" in sys.argv
    only = None
    if "--only" in sys.argv:
        idx = sys.argv.index("--only")
        if idx + 1 < len(sys.argv):
            only = int(sys.argv[idx + 1])

    checks: list[tuple[str, bool, str]] = []

    # ---------- 场景 1：拖到鲸鱼旁边的透明区域 ----------
    # 这是整个功能的技术风险所在：透明像素能不能接住拖放。
    kill_stale()
    proc = launch()
    try:
        assert not main_window_open(), "基线不干净：拖之前主面板就存在"
        left, top, right, bottom = float_rect()
        files = make_files(2)

        tx, ty = left + 18, top + 18     # 左上角内缩 18px：这一带是纯透明像素
        print(f"\n[场景 1] 拖到鲸鱼旁边的透明处 ({tx}, {ty})")
        effect = drag_to(tx, ty, files)

        recs = archived_files()
        names = {r.get("fileName") for r in recs}

        # effect 不作为判据：光标底下是桌面，桌面**也会**接住文件拖放
        # （效果是"复制到桌面"），所以哪怕鲸记完全没参与，返回的同样是 Copy。
        # 真正的判据只有两条：主面板有没有被打开、文件有没有进当天的归档 ——
        # 这两件事只有鲸记会做。（实测过：把窗口背景改回全透明再跑，
        # effect 照样是 Copy，而后面两条全挂。）
        checks.append(("拖到透明处之后，主面板被打开", main_window_open(),
                       "主面板出现了" if main_window_open() else "主面板没出现"))
        checks.append(("两个文件都真的进了当天的归档", len(recs) == 2,
                       f"当天 {len(recs)} 条：{sorted(names)}"))
    finally:
        proc.terminate(); time.sleep(1.0); proc.kill()

    if only == 1:
        return finish(checks, keep)

    # ---------- 场景 2：拖到鲸鱼身上（不透明像素，对照场景 1）----------
    kill_stale()
    proc = launch()
    try:
        assert not main_window_open(), "基线不干净：拖之前主面板就存在"
        left, top, right, bottom = float_rect()
        files = make_files(2)

        tx, ty = (left + right) // 2, (top + bottom) // 2
        print(f"\n[场景 2] 拖到鲸鱼身上 ({tx}, {ty})")
        effect = drag_to(tx, ty, files)

        recs = archived_files()

        checks.append(("拖到鲸鱼身上之后，主面板被打开", main_window_open(),
                       "主面板出现了" if main_window_open() else "主面板没出现"))
        checks.append(("两个文件都真的进了当天的归档", len(recs) == 2, f"当天 {len(recs)} 条"))
    finally:
        proc.terminate(); time.sleep(1.0); proc.kill()

    if only == 2:
        return finish(checks, keep)

    # ---------- 场景 3：拖到窗口矩形外（对照组）----------
    # 没有这一条，上面两条就没意义 —— 万一"随便拖到哪都算接住"（比如拖放源自己有 bug），
    # 前两条照样会过。
    kill_stale()
    proc = launch()
    try:
        left, top, right, bottom = float_rect()
        files = make_files(2)

        tx, ty = left - 60, (top + bottom) // 2
        print(f"\n[场景 3] 拖到窗口外 ({tx}, {ty})")
        effect = drag_to(tx, ty, files)

        recs = archived_files()

        # 不拿 effect 当判据：光标底下是桌面，而桌面**是会**接住文件拖放的
        # （效果就是"复制到桌面"），所以 effect 必然是 Copy ——
        # 它既不能证明我们的窗口参与了，也不能证明没参与。
        # 真正的判据是"有没有归档"和"主面板有没有冒出来"，那两件事只有鲸记会做。
        checks.append(("拖到窗口外不会产生归档记录", len(recs) == 0, f"当天 {len(recs)} 条"))
        checks.append(("拖到窗口外不会打开主面板", not main_window_open(),
                       "主面板没出现" if not main_window_open() else "主面板出现了"))
    finally:
        proc.terminate(); time.sleep(1.0); proc.kill()

    return finish(checks, keep)


if __name__ == "__main__":
    raise SystemExit(main())

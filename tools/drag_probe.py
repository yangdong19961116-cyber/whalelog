"""悬浮窗「拖拽 vs 点击」判定的实测探针。

悬浮窗的行为没法靠断言测：`DragMove()` 是阻塞式的、会进入系统的模态移动循环，
只有真实的鼠标按下-移动-松开才能走到那条路径。所以这里用 Win32 合成真实输入
（SetCursorPos + mouse_event），然后看松手后主面板有没有冒出来 —— 那是最直接的判据。

用法：
    python tools/drag_probe.py            # 拖拽 + 单击两种情形都测
    python tools/drag_probe.py --keep     # 保留临时数据目录，便于翻看

判定标准：
    拖拽后主面板**不该**出现；单击后主面板**应该**出现。
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
DATA = pathlib.Path(tempfile.gettempdir()) / "whalelog-dragprobe"

# 悬浮窗摆在这个位置（临时设置里指定），远离任务栏和其他窗口
WIN_LEFT, WIN_TOP = 360, 260
SCALE = 1.0
EXPANDED_W, EXPANDED_H = 236, 296

user32 = ctypes.WinDLL("user32", use_last_error=True)
user32.SetCursorPos.argtypes = [ctypes.c_int, ctypes.c_int]
user32.GetWindowTextLengthW.restype = ctypes.c_int

ENUMPROC = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

MOUSEEVENTF_LEFTDOWN = 0x0002
MOUSEEVENTF_LEFTUP = 0x0004

MAIN_TITLE = "鲸记 WhaleLog"
FLOAT_TITLE = "鲸记小鲸鱼"


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


def kill_stale() -> None:
    subprocess.run(["taskkill", "/IM", "WhaleLog.exe", "/F"],
                   capture_output=True, encoding="utf-8", errors="replace")
    time.sleep(1.5)


def launch() -> subprocess.Popen:
    shutil.rmtree(DATA, ignore_errors=True)
    (DATA / "notes").mkdir(parents=True)
    (DATA / "logs").mkdir(parents=True)
    (DATA / "settings.json").write_text(json.dumps({
        "floatingEnabled": True,
        "floatingScale": SCALE,
        "floatingOpacity": 1.0,
        "floatingSnapToEdge": False,      # 不吸边，位置完全可控
        "floatingAutoCollapse": False,
        "floatingLeft": float(WIN_LEFT),
        "floatingTop": float(WIN_TOP),
        "workEndHour": 18, "workEndMinute": 0,
        "reminderEnabled": False,
        "themeOverride": 2,
    }, ensure_ascii=False, indent=2), encoding="utf-8")

    env = dict(os.environ, WHALELOG_DATA_DIR=str(DATA))
    proc = subprocess.Popen([str(EXE), "--tray"], env=env)
    time.sleep(4.5)
    return proc


def drag(from_pt: tuple[int, int], to_pt: tuple[int, int]) -> None:
    """真实鼠标：按下 → 分步移动 → 松开。"""
    user32.SetCursorPos(*from_pt)
    time.sleep(0.35)
    user32.mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0)
    time.sleep(0.30)
    steps = 24
    for i in range(1, steps + 1):
        x = from_pt[0] + round((to_pt[0] - from_pt[0]) * i / steps)
        y = from_pt[1] + round((to_pt[1] - from_pt[1]) * i / steps)
        user32.SetCursorPos(x, y)
        time.sleep(0.025)
    time.sleep(0.30)
    user32.mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0)
    time.sleep(1.6)


def press_release(pt: tuple[int, int]) -> None:
    """真实鼠标：原地按下再松开，不移动。"""
    user32.SetCursorPos(*pt)
    time.sleep(0.35)
    user32.mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0)
    time.sleep(0.25)
    user32.mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0)
    time.sleep(1.6)


def float_rect() -> tuple[int, int, int, int]:
    """先确认悬浮窗真的出现在预期位置，再拿它的实际矩形。"""
    for _ in range(20):
        for title, _, rect in whale_windows():
            if FLOAT_TITLE in title:
                left, top, right, bottom = rect
                if abs(left - WIN_LEFT) <= 30 and abs(top - WIN_TOP) <= 30:
                    return rect
        time.sleep(0.25)
    raise SystemExit("悬浮窗没出现在预期位置，无法继续")


def float_center() -> tuple[int, int]:
    left, top, right, bottom = float_rect()
    return ((left + right) // 2, top + (bottom - top) // 2)


def main() -> int:
    print(f"待测程序：{EXE}")
    if not EXE.exists():
        print("找不到编译产物，先 build 一次", file=sys.stderr)
        return 1

    # 每项：(检查项, 是否通过, 实测说明)
    checks: list[tuple[str, bool, str]] = []

    # ---------- 场景 1：拖拽 ----------
    kill_stale()
    proc = launch()
    try:
        before = whale_windows()
        print("\n[拖拽前] 屏幕上的窗口：")
        for t, pid, r in before:
            print(f"   {t}  pid={pid}  {r}")
        assert not any(MAIN_TITLE in t for t, _, _ in before), \
            "基线不干净：拖拽前主面板就存在，测试无效"

        start = float_center()
        print(f"   从 {start} 拖到 {(start[0] + 240, start[1] + 160)}")
        drag(start, (start[0] + 240, start[1] + 160))

        after = whale_windows()
        print("[拖拽后] 屏幕上的窗口：")
        for t, pid, r in after:
            print(f"   {t}  pid={pid}  {r}")
        opened = any(MAIN_TITLE in t for t, _, _ in after)
        checks.append(("拖拽 240×160 像素后，主面板不出现", not opened,
                       f"主面板{'出现了' if opened else '没出现'}"))
    finally:
        proc.terminate(); time.sleep(1.0); proc.kill()

    # ---------- 场景 2：单击（对照，应该打开主面板）----------
    kill_stale()
    proc = launch()
    try:
        assert not any(MAIN_TITLE in t for t, _, _ in whale_windows()), \
            "基线不干净：单击前主面板就存在，测试无效"
        press_release(float_center())
        opened = any(MAIN_TITLE in t for t, _, _ in whale_windows())
        checks.append(("原地单击（不移动）后，主面板出现", opened,
                       f"主面板{'出现了' if opened else '没出现'}"))
    finally:
        proc.terminate(); time.sleep(1.0); proc.kill()

    # ---------- 场景 3：从状态条区域拖拽 ----------
    # 状态条上的"单击"是手动收起/展开，所以拖动它时必须仍然是拖动：
    # 既不该打开主面板，也不该被判定成点击而把窗口收起来。
    kill_stale()
    proc = launch()
    try:
        assert not any(MAIN_TITLE in t for t, _, _ in whale_windows()), \
            "基线不干净：拖状态条前主面板就存在，测试无效"
        left, top, right, bottom = float_rect()
        start = ((left + right) // 2, bottom - 24)        # 状态条在窗口底部
        print(f"\n   从状态条 {start} 拖到 {(start[0] + 40, start[1] - 200)}")
        drag(start, (start[0] + 40, start[1] - 200))

        after = whale_windows()
        opened = any(MAIN_TITLE in t for t, _, _ in after)
        checks.append(("拖状态条后，主面板不出现", not opened,
                       f"主面板{'出现了' if opened else '没出现'}"))

        float_w = next((r[2] - r[0] for t, _, r in after if FLOAT_TITLE in t), 0)
        expected_w = round(EXPANDED_W * SCALE)
        checks.append(("拖状态条后，窗口仍是展开态（没被误当成点击收起）",
                       float_w > expected_w * 0.6,
                       f"宽 {float_w}px，展开态约 {expected_w}px、收起态约 60px"))
    finally:
        proc.terminate(); time.sleep(1.0); proc.kill()

    kill_stale()

    print("\n" + "=" * 62)
    failed = [c for c in checks if not c[1]]
    for name, ok, detail in checks:
        print(f"{'✓' if ok else '✗'} {name}\n    实测：{detail}")
    print("=" * 62)
    print(f"结论：{'全部符合预期' if not failed else f'{len(failed)} 项不符合预期'}")

    if "--keep" not in sys.argv:
        shutil.rmtree(DATA, ignore_errors=True)
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())

# -*- coding: utf-8 -*-
"""
鼠标防锁屏小工具（原地来回抖动版）
- 启动后每隔 interval 秒，让鼠标在当前位置左右来回移动 step 像素
- 同时调用 SetThreadExecutionState 阻止系统休眠 / 锁屏
- 仅使用 Python 标准库（ctypes），无需安装任何第三方包
- 按 Ctrl+C 退出

用法示例：
  python mouse_jiggler.py                 # 默认：每 2 秒抖 5 像素
  python mouse_jiggler.py --step 10 --interval 3
  python mouse_jiggler.py --once          # 只抖一次就退出（用于测试）
  python mouse_jiggler.py --duration 30   # 运行 30 秒后自动退出
  python mouse_jiggler.py --no-keep-awake # 只动鼠标，不阻止休眠
"""
import argparse
import ctypes
import signal
import sys
import time

# ---------- Windows API ----------
try:
    user32 = ctypes.windll.user32
    kernel32 = ctypes.windll.kernel32
except AttributeError:
    sys.exit("本工具仅支持 Windows 系统（需要 user32 / kernel32）。")

ES_CONTINUOUS = 0x80000000
ES_SYSTEM_REQUIRED = 0x00000001
ES_DISPLAY_REQUIRED = 0x00000002


def keep_awake():
    """告诉系统：本线程仍活跃，不要休眠 / 关闭显示器 / 锁屏。"""
    kernel32.SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED)


def get_pos():
    class POINT(ctypes.Structure):
        _fields_ = [("x", ctypes.c_long), ("y", ctypes.c_long)]

    pt = POINT()
    user32.GetCursorPos(ctypes.byref(pt))
    return pt.x, pt.y


def set_pos(x, y):
    user32.SetCursorPos(int(x), int(y))


# ---------- 主循环 ----------
running = True


def _stop(signum, frame):
    global running
    running = False
    print("\n接收到退出信号，正在停止...")


def main():
    ap = argparse.ArgumentParser(description="鼠标防锁屏小工具（原地来回抖动）")
    ap.add_argument("--step", type=int, default=5, help="每次抖动的像素数（默认 5）")
    ap.add_argument("--interval", type=float, default=2.0, help="抖动间隔秒数（默认 2.0）")
    ap.add_argument("--duration", type=float, default=0, help="运行秒数，0 表示一直运行（默认 0）")
    ap.add_argument("--once", action="store_true", help="只抖动一次就退出（测试用）")
    ap.add_argument("--no-keep-awake", action="store_true", help="不调用 SetThreadExecutionState")
    ap.add_argument("--quiet", action="store_true", help="不打印心跳信息")
    args = ap.parse_args()

    signal.signal(signal.SIGINT, _stop)

    if not args.no_keep_awake:
        keep_awake()

    print("鼠标防锁屏工具已启动（原地来回抖动）。按 Ctrl+C 退出。")
    print(f"  抖动幅度 = {args.step} 像素，间隔 = {args.interval} 秒"
          + ("" if args.no_keep_awake else "，已阻止系统休眠/锁屏"))

    direction = 1
    start = time.time()
    ticks = 0

    try:
        while running:
            x, y = get_pos()
            target_x = x + direction * args.step
            set_pos(target_x, y)
            nx, ny = get_pos()  # 读回真实位置，确认确实移动了
            direction *= -1
            ticks += 1

            if not args.quiet:
                print(f"  [{ticks:04d}] 鼠标移动到 ({nx}, {ny})  （基准 {x},{y}，偏移 {nx - x:+d} 像素）")

            if args.once:
                break
            if args.duration and (time.time() - start) >= args.duration:
                break

            # 晃动后把基准设为新位置，保证始终"原地"来回
            time.sleep(args.interval)
    except KeyboardInterrupt:
        pass
    finally:
        if not args.no_keep_awake:
            # 恢复系统默认电源策略
            kernel32.SetThreadExecutionState(ES_CONTINUOUS)
        print("已停止，系统电源策略已恢复。")


if __name__ == "__main__":
    main()

# -*- coding: utf-8 -*-
"""
鼠标防锁屏工具（带可视化界面，启动/停止按钮）—— Python 版
- 仅使用 Python 标准库：tkinter（界面）+ ctypes（Windows API），无需安装任何包
- 启动后鼠标在原地左右来回小幅抖动，并阻止系统休眠/锁屏
- 点击「启动」开始，点击「停止」结束；关闭窗口也会自动停止并恢复电源策略

运行：双击「启动鼠标防锁屏-Python版.bat」即可
"""

import ctypes
import os
import sys
import traceback


# ---------- 全局错误上报（不依赖 tkinter，任何早期错误都能弹窗+写日志） ----------
def _report_error(title, msg):
    log_path = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                            "mouse_jiggler_error.log")
    try:
        with open(log_path, "a", encoding="utf-8") as f:
            f.write("=" * 60 + "\n")
            f.write(title + "\n")
            f.write(msg + "\n")
    except Exception:
        pass
    try:
        ctypes.windll.user32.MessageBoxW(
            0, msg, "鼠标防锁屏工具 - " + title, 0x10  # MB_ICONERROR
        )
    except Exception:
        pass


# ---------- Windows API 绑定 ----------
try:
    user32 = ctypes.windll.user32
    kernel32 = ctypes.windll.kernel32
except AttributeError:
    _report_error("不支持的系统",
                  "本工具仅支持 Windows 系统（需要 user32 / kernel32）。\n"
                  "请在 Windows 上运行。")
    sys.exit(1)

ES_CONTINUOUS = 0x80000000
ES_SYSTEM_REQUIRED = 0x00000001
ES_DISPLAY_REQUIRED = 0x00000002


def get_pos():
    class POINT(ctypes.Structure):
        _fields_ = [("x", ctypes.c_long), ("y", ctypes.c_long)]

    pt = POINT()
    user32.GetCursorPos(ctypes.byref(pt))
    return pt.x, pt.y


def set_pos(x, y):
    user32.SetCursorPos(int(x), int(y))


class Jiggler:
    """核心抖动逻辑（不依赖 GUI，可在后台线程运行）。"""

    def __init__(self):
        self.running = False
        self.step = 5
        self.interval = 2.0
        self.keep_awake = True
        self.tick = 0
        self.dir = 1
        self.last_pos = "--"
        self._thread = None

    def start(self):
        if self.running:
            return
        self.running = True
        self.tick = 0
        self.dir = 1
        if self.keep_awake:
            kernel32.SetThreadExecutionState(
                ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED
            )
        self._thread = threading.Thread(target=self._loop, daemon=True)
        self._thread.start()

    def stop(self):
        if not self.running:
            return
        self.running = False
        if self.keep_awake:
            kernel32.SetThreadExecutionState(ES_CONTINUOUS)
        # 后台线程会在下一轮循环检查 self.running 时自行退出

    def _loop(self):
        while self.running:
            x, y = get_pos()
            set_pos(x + self.dir * self.step, y)
            self.last_pos = f"({x + self.dir * self.step}, {y})"
            self.dir *= -1
            self.tick += 1
            if self.keep_awake:
                kernel32.SetThreadExecutionState(
                    ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED
                )
            time.sleep(self.interval)


# 注意：threading / time 在 Windows 上必然可用，这里放在 Jiggler 之后导入也无妨，
# 为稳妥直接在顶部统一导入。
import threading
import time


class App:
    def __init__(self, root):
        self.root = root
        self.j = Jiggler()
        self.root.title("鼠标防锁屏工具")
        self.root.geometry("380x320")
        self.root.resizable(False, False)
        self.root.protocol("WM_DELETE_WINDOW", self.on_close)

        self._build_widgets()
        self._update_buttons()
        self._poll()

    def _build_widgets(self):
        pad = {"padx": 12, "pady": 6}

        try:
            font_title = ("Microsoft YaHei", 14, "bold")
            font_tip = ("Microsoft YaHei", 9)
            font_status = ("Microsoft YaHei", 11, "bold")
        except Exception:
            font_title = ("TkDefaultFont", 14, "bold")
            font_tip = ("TkDefaultFont", 9)
            font_status = ("TkDefaultFont", 11, "bold")

        ttk.Label(self.root, text="鼠标防锁屏工具", font=font_title).pack(pady=(12, 2))
        ttk.Label(self.root, text="启动后鼠标在原地左右轻微抖动，阻止锁屏/休眠",
                  font=font_tip, foreground="#666666").pack(pady=(0, 8))

        # 参数区
        frm = ttk.Frame(self.root)
        frm.pack(fill="x", **pad)

        ttk.Label(frm, text="抖动幅度(像素):").grid(row=0, column=0, sticky="w", pady=3)
        self.step_var = tk.StringVar(value="5")
        ttk.Entry(frm, textvariable=self.step_var, width=10).grid(row=0, column=1, sticky="w", padx=6)

        ttk.Label(frm, text="间隔(秒):").grid(row=1, column=0, sticky="w", pady=3)
        self.interval_var = tk.StringVar(value="2.0")
        ttk.Entry(frm, textvariable=self.interval_var, width=10).grid(row=1, column=1, sticky="w", padx=6)

        self.awake_var = tk.BooleanVar(value=True)
        ttk.Checkbutton(frm, text="同时阻止系统休眠/锁屏", variable=self.awake_var).grid(
            row=2, column=0, columnspan=2, sticky="w", pady=3)

        # 状态
        self.status_var = tk.StringVar(value="○ 已停止")
        self.status_label = ttk.Label(self.root, textvariable=self.status_var,
                                       font=font_status, foreground="#888888")
        self.status_label.pack(pady=(4, 0))

        self.pos_var = tk.StringVar(value="已抖动 0 次")
        ttk.Label(self.root, textvariable=self.pos_var, foreground="#555555").pack(pady=(2, 8))

        # 按钮
        btn_frm = ttk.Frame(self.root)
        btn_frm.pack(pady=(0, 10))
        self.start_btn = ttk.Button(btn_frm, text="▶ 启动", width=12, command=self.on_start)
        self.start_btn.grid(row=0, column=0, padx=8)
        self.stop_btn = ttk.Button(btn_frm, text="■ 停止", width=12, command=self.on_stop)
        self.stop_btn.grid(row=0, column=1, padx=8)

        ttk.Label(self.root, text="点击「启动」开始，点击「停止」或关闭窗口结束",
                  font=font_tip, foreground="#999999").pack(pady=(4, 0))

    def on_start(self):
        try:
            self.j.step = max(1, int(self.step_var.get()))
        except ValueError:
            self.j.step = 5
        try:
            self.j.interval = max(0.1, float(self.interval_var.get()))
        except ValueError:
            self.j.interval = 2.0
        self.j.keep_awake = self.awake_var.get()
        self.j.start()
        self._update_buttons()

    def on_stop(self):
        self.j.stop()
        self._update_buttons()

    def on_close(self):
        self.j.stop()
        self.root.destroy()

    def _update_buttons(self):
        if self.j.running:
            self.start_btn.config(state="disabled")
            self.stop_btn.config(state="normal")
            self.status_var.set("● 运行中")
            self.status_label.config(foreground="#2E8B57")
        else:
            self.start_btn.config(state="normal")
            self.stop_btn.config(state="disabled")
            self.status_var.set("○ 已停止")
            self.status_label.config(foreground="#888888")

    def _poll(self):
        # 由后台线程更新 j.tick / j.last_pos，这里定时刷新界面（tkinter 非线程安全，故用 after 轮询）
        if self.j.running:
            self.pos_var.set(f"已抖动 {self.j.tick} 次   当前位置 {self.j.last_pos}")
        self.root.after(150, self._poll)


def main():
    root = tk.Tk()
    App(root)
    root.mainloop()


if __name__ == "__main__":
    try:
        import tkinter as tk
        from tkinter import ttk
    except Exception as e:
        _report_error("缺少 tkinter",
                      "无法导入 tkinter（Python 图形界面库）。\n"
                      "请安装带 tkinter 的 Python（官方安装包默认包含），\n"
                      "或在安装时勾选 \"tcl/tk and IDLE\"。\n\n错误详情：\n" + repr(e))
        sys.exit(1)

    try:
        main()
    except Exception:
        _report_error("运行出错", traceback.format_exc())
        sys.exit(1)

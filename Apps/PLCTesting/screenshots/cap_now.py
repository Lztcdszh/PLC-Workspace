import win32gui
import win32con
import time
from PIL import ImageGrab

def find_window():
    result = []
    def enum(hwnd, _):
        if win32gui.IsWindowVisible(hwnd):
            title = win32gui.GetWindowText(hwnd)
            if "PLC监控" in title:
                result.append(hwnd)
    win32gui.EnumWindows(enum, None)
    return result

wins = find_window()
print("Found windows:", wins)
if not wins:
    print("NO WINDOW")
    raise SystemExit(1)

for idx, hwnd in enumerate(wins):
    win32gui.ShowWindow(hwnd, win32con.SW_RESTORE)
    try:
        win32gui.SetForegroundWindow(hwnd)
    except Exception as e:
        print("SetForegroundWindow skipped:", e)
    time.sleep(0.5)
    left, top, right, bottom = win32gui.GetWindowRect(hwnd)
    print("Rect", idx, left, top, right, bottom)
    out = r"C:\Users\Administrator\source\repos\Apps\PLCTesting\screenshots\shot_canvas_%d.png" % idx
    img = ImageGrab.grab(bbox=(left, top, right, bottom))
    img.save(out)
    print("Saved", out, img.size)

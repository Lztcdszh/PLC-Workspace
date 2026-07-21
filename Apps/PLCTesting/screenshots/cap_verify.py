import win32gui
import win32con
import time
from PIL import Image

OUT = r"C:\Users\Administrator\source\repos\Apps\PLCTesting\screenshots\live_verify.png"

def find_window():
    result = []
    def enum(hwnd, _):
        if win32gui.IsWindowVisible(hwnd):
            title = win32gui.GetWindowText(hwnd)
            if "PLC监控" in title:
                result.append((hwnd, title))
    win32gui.EnumWindows(enum, None)
    return result

wins = find_window()
print("Found windows:", wins)
if not wins:
    print("NO WINDOW")
    raise SystemExit(1)

hwnd, title = wins[0]
win32gui.ShowWindow(hwnd, win32con.SW_RESTORE)
win32gui.SetForegroundWindow(hwnd)
time.sleep(1.0)

left, top, right, bottom = win32gui.GetWindowRect(hwnd)
print("Rect:", left, top, right, bottom)

from PIL import ImageGrab
img = ImageGrab.grab(bbox=(left, top, right, bottom))
img.save(OUT)
print("Saved", OUT, img.size)

# ---- 橙色像素分析 (#FF8C00 = (255,140,0)) ----
def is_orange(px):
    r, g, b = px[:3]
    return r > 190 and 100 <= g <= 175 and b < 70

px = img.load()
W, H = img.size
orange_rows = {}
total = 0
for y in range(H):
    cnt = 0
    for x in range(0, W, 2):  # 隔列采样加速
        if is_orange(px[x, y]):
            cnt += 1
            total += 1
    if cnt > 0:
        orange_rows[y] = cnt

print("Total orange samples:", total)
print("Rows with orange (y:count):")
for y in sorted(orange_rows):
    print(f"  y={y}: {orange_rows[y]}")

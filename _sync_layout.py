import re

main = r"C:/Users/Administrator/source/repos/Apps/PLCTesting/Views/MainWindow.xaml"
meas = r"C:/Users/Administrator/source/repos/Apps/LayoutCheck/MeasureWindow.xaml"

sm = open(main, encoding='utf-8').read()
# 提取位置指示器 Canvas（Width=1960）内部
m = re.search(r'<Canvas Width="1960" Height="350">(.*?)</Canvas>', sm, re.S)
if not m:
    raise SystemExit("未在 MainWindow 找到 Width=1960 的位置指示器 Canvas")
block = m.group(1)

pm = open(meas, encoding='utf-8').read()
# 替换 MeasureWindow 中旧的 Canvas 整块（兼容历史宽度 1864 与当前 1960）
pm = re.sub(r'<Canvas Width="(?:1864|1960)" Height="350">.*?</Canvas>',
            f'<Canvas Width="1960" Height="350">{block}</Canvas>', pm, flags=re.S)
# 校验窗口加宽以容纳更宽的 Canvas
pm = pm.replace('Width="1900" Height="640"', 'Width="2000" Height="640"')
open(meas, 'w', encoding='utf-8').write(pm)
print("MeasureWindow.xaml 已同步 MainWindow 位置指示器（Canvas 加宽至 1960）")
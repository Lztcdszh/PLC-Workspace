# -*- coding: utf-8 -*-
import io

PATH = r"C:\Users\Administrator\source\repos\Apps\PLCTesting\Views\MainWindow.xaml"

with io.open(PATH, "r", encoding="utf-8") as f:
    content = f.read()

CONN = 'Connection="{Binding RelativeSource={RelativeSource AncestorType=myplc:PlcPanel}, Path=Connection}"'

Y_TOP = 95
Y_BOT = 225
X0 = 90
STEP = 154
XT = lambda i: X0 + i * STEP          # top node i  (0..8)
XB = lambda i: X0 + (8 - i) * STEP     # bottom node i (0..8) —— 与上排右端对齐，保证竖向连接与左右端严格对称

# (name, addr_or_M, pos, time_addr, is_limit)
top = [
    ("负极限位", "M4.1", None, None, True),
    ("充电位", "DB1.DBW240", 26, "DB1.DBW300", False),
    ("1号位", "DB1.DBW240", 28, "DB1.DBW302", False),
    ("2号位", "DB1.DBW240", 30, "DB1.DBW304", False),
    ("3号位", "DB1.DBW240", 32, "DB1.DBW306", False),
    ("4号位", "DB1.DBW240", 34, "DB1.DBW308", False),
    ("5号位", "DB1.DBW240", 36, "DB1.DBW310", False),
    ("6号位", "DB1.DBW240", 38, "DB1.DBW312", False),
    ("7号位", "DB1.DBW240", 40, "DB1.DBW314", False),
]
bot = [
    ("8号位", "DB1.DBW240", 42, "DB1.DBW316", False),
    ("9号位", "DB1.DBW240", 44, "DB1.DBW318", False),
    ("10号位", "DB1.DBW240", 46, "DB1.DBW320", False),
    ("11号位", "DB1.DBW240", 48, "DB1.DBW322", False),
    ("12号位", "DB1.DBW240", 50, "DB1.DBW324", False),
    ("13号位", "DB1.DBW240", 52, "DB1.DBW326", False),
    ("14号位", "DB1.DBW240", 54, "DB1.DBW328", False),
    ("15号位", "DB1.DBW240", 56, "DB1.DBW330", False),
    ("正极限位", "M4.2", None, None, True),
]

parts = []
parts.append('<!-- ===== 位置指示器：Canvas 蛇形连续轨道（控件贴站点） ===== -->')
parts.append('<Grid>')
parts.append('    <Viewbox Stretch="Uniform" HorizontalAlignment="Stretch" VerticalAlignment="Stretch">')
parts.append('        <Canvas Width="1450" Height="340">')

# track polyline (top -> right down -> bottom)
parts.append('            <Polyline Points="%d,%d %d,%d %d,%d %d,%d" Stroke="#7A8FA3" StrokeThickness="6" StrokeLineJoin="Round" StrokeStartLineCap="Round" StrokeEndLineCap="Round" />' % (XT(0), Y_TOP, XT(8), Y_TOP, XT(8), Y_BOT, XB(8), Y_BOT))

def node_ellipse(x, y):
    return '            <Ellipse Canvas.Left="%d" Canvas.Top="%d" Width="32" Height="32" Fill="#16324A" Stroke="#00E5FF" StrokeThickness="2.5" />' % (x - 16, y - 16)

def name_label(x, y, name, has_ctrl):
    # 站名统一置于指示灯正上方（节点中线居中、轨道线之上）；极限位同样处理
    return '            <TextBlock Canvas.Left="%d" Canvas.Top="%d" Width="80" Text="%s" FontSize="12" FontWeight="Bold" Foreground="#1F2D3D" TextAlignment="Center" />' % (x - 40, y - 64, name)

def light(x, y, d):
    name, addr, pos, taddr, islim = d
    lx, ly = x - 8, y - 8
    if islim:
        return '            <myplc:PlcIndicator Canvas.Left="%d" Canvas.Top="%d" Width="16" Height="16" TagName="%s" TagAddress="%s" AutoRegister="True" OnBrush="LimeGreen" OffBrush="Red" %s />' % (lx, ly, name, addr, CONN)
    else:
        return '            <myplc:PlcRangeIndicator Canvas.Left="%d" Canvas.Top="%d" Width="16" Height="16" TagName="小车实际位置" TagAddress="DB1.DBW240" DataType="UInt16" Condition="Equal" CompareValue="%d" TrueBrush="#67C23A" FalseBrush="#F56C6C" %s />' % (lx, ly, pos, CONN)

def in_transit_h(x1, x2, top_y, value):
    left = x1 + 16
    width = (x2 - x1) - 32
    t = top_y - 4
    return ('            <myplc:PlcStatusBar Canvas.Left="%d" Canvas.Top="%d" Width="%d" Height="8" Orientation="Horizontal" BarHeight="8" CornerRadius="4" '
            'TagName="小车实际位置" TagAddress="DB1.DBW240" ActiveValue="%d" Blink="True" %s />' % (left, t, width, value, CONN))

def in_transit_v(x, y1, y2, value):
    left = x - 4
    height = y2 - y1
    return ('            <myplc:PlcStatusBar Canvas.Left="%d" Canvas.Top="%d" Width="8" Height="%d" Orientation="Vertical" BarHeight="8" CornerRadius="4" '
            'TagName="小车实际位置" TagAddress="DB1.DBW240" ActiveValue="%d" Blink="True" %s />' % (left, y1, height, value, CONN))

TIME_W = 112
def ctrl_around(x, y, time_side):
    # 降速时间框：上排在指示灯右侧、下排在左侧，均位于轨道上方；选定按钮在灯正下方
    s = []
    tx = (x + 14) if time_side == 'right' else (x - 14 - TIME_W)
    s.append('            <myplc:PlcNumericInput Canvas.Left="%d" Canvas.Top="%d" Width="%d" Height="32" Label="降速时间" TagName="降速时间%d" TagAddress="%s" DataType="Int16" FormatString="F0" Unit="100ms" %s />' % (tx, y - 40, TIME_W, y_pos + 1, time_addr, CONN))
    s.append('            <myplc:PlcSelectButton Canvas.Left="%d" Canvas.Top="%d" Width="60" Height="26" ButtonText="选定" WriteValue="%d" WriteTagAddress="DB1.DBW250" WriteTagName="小车设定位置" HorizontalAlignment="Center" %s />' % (x - 30, y + 18, y_pos, CONN))
    return "\n".join(s)

# top nodes + names + lights + controls
for i, d in enumerate(top):
    x = XT(i); y = Y_TOP
    globals()['y_pos'], globals()['time_addr'] = d[2], d[3]
    parts.append(node_ellipse(x, y))
    parts.append(name_label(x, y, d[0], d[3] is not None))
    parts.append(light(x, y, d))
    if d[3] is not None:
        parts.append(ctrl_around(x, y, 'right'))
# top in-transit bars (track blinks by actual position)
for i in range(8):
    v = 25 + i * 2
    parts.append(in_transit_h(XT(i), XT(i + 1), Y_TOP, v))
# right vertical 7<->8
parts.append(in_transit_v(XT(8), Y_TOP, Y_BOT, 41))
# bottom nodes + names + lights + controls
for i, d in enumerate(bot):
    x = XB(i); y = Y_BOT
    globals()['y_pos'], globals()['time_addr'] = d[2], d[3]
    parts.append(node_ellipse(x, y))
    parts.append(name_label(x, y, d[0], d[3] is not None))  # 下排站名置于指示灯正上方
    parts.append(light(x, y, d))
    if d[3] is not None:
        parts.append(ctrl_around(x, y, 'left'))
# bottom in-transit bars
for i in range(8):
    v = 43 + i * 2
    parts.append(in_transit_h(XB(i + 1), XB(i), Y_BOT, v))

parts.append('        </Canvas>')
parts.append('    </Viewbox>')
parts.append('</Grid>')

new_block = "\n".join(parts)

marker = "<!-- ===== 位置指示器："
start = content.index(marker)
end = content.index("</myplc:PlcPanel.PanelContent>", start)
new_content = content[:start] + new_block + content[end:]

with io.open(PATH, "w", encoding="utf-8") as f:
    f.write(new_content)

print("OK replaced. new_block lines:", new_block.count(chr(10)) + 1)

from docx import Document
from docx.shared import Emu
from docx.oxml.ns import qn

path = r'C:\Users\Administrator\source\repos\综合场站大宗骨料智慧管控与检测系统_产品介绍_整理版.docx'
d = Document(path)
sec = d.sections[0]

sw = Emu(sec.page_width).inches; sh = Emu(sec.page_height).inches
lm = Emu(sec.left_margin).inches; rm = Emu(sec.right_margin).inches
tm = Emu(sec.top_margin).inches; bm = Emu(sec.bottom_margin).inches
usable = sw - lm - rm
print(f'[页面] {sw:.2f}x{sh:.2f}in  边距 L{lm:.2f} R{rm:.2f} T{tm:.2f} B{bm:.2f}  可用宽={usable:.2f}in')

print('\n[图片]')
for i, s in enumerate(d.inline_shapes, 1):
    w = Emu(s.width).inches; h = Emu(s.height).inches
    print(f'  图{i}: {w:.2f}x{h:.2f}in  {"⚠️超出可用宽!" if w > usable else "OK"}')

print('\n[图片详情] inline_shapes=%d' % len(d.inline_shapes))
draw_paras = [(i, p.alignment) for i, p in enumerate(d.paragraphs) if p._element.find('.//' + qn('w:drawing')) is not None]
print('  含drawing的段落数=%d, 段落索引=%s' % (len(draw_paras), [x[0] for x in draw_paras]))

print('\n[表格]')
for ti, t in enumerate(d.tables, 1):
    tw = t._tbl.tblPr.find(qn('w:tblW'))
    if tw is not None:
        typ = tw.get(qn('w:type')); val = tw.get(qn('w:w'))
        w_in = Emu(int(val)).inches if (val and val.isdigit()) else 'auto'
    else:
        typ = val = w_in = '未设(Word默认auto撑满)'
    # 列宽
    colws = []
    for c in t.columns:
        cw = c.cells[0]._tc.get_or_add_tcPr().find(qn('w:tcW'))
        colws.append(cw.get(qn('w:w')) if cw is not None else 'auto')
    print(f'  表{ti}: tblW type={typ} w={val}({w_in}in) 列宽={colws} 行={len(t.rows)} 列={len(t.columns)} 可用={usable:.2f}in')

print('\n[标题层级映射]')
for p in d.paragraphs:
    st = p.style.name
    if st.startswith('Heading') or st == 'List Bullet':
        print(f'  [{st:11}] {p.text.strip()[:42]}')

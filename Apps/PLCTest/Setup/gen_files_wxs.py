#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""遍历自包含发布目录，生成 WiX v4/v5 格式的 files.wxs（完整目录树 + 每个文件一个 Component）。"""
import os, re, uuid

SRC = r"C:/Users/Administrator/source/repos/Apps/PLCTest/PLCTesting-SelfContained-win-x64"
OUT = r"C:/Users/Administrator/source/repos/Apps/PLCTest/Setup/files.wxs"

def safe(s: str) -> str:
    s = s.replace('\\', '/')
    return re.sub(r'[^A-Za-z0-9]', '_', s)

# 收集所有文件相对路径（统一用 /）
files = []
for dp, dns, fns in os.walk(SRC):
    for fn in fns:
        full = os.path.join(dp, fn)
        rel = os.path.relpath(full, SRC).replace('\\', '/')
        files.append(rel)

# 构建目录树
tree = {'dirs': {}, 'files': []}  # node -> {'dirs':{name:node}, 'files':[name]}
for rel in files:
    parts = rel.split('/')
    node = tree
    for p in parts[:-1]:
        node = node.setdefault(p, {'dirs': {}, 'files': []})
    node['files'].append(parts[-1])

refs = []  # 所有 Component Id

def render(node, prefix):
    """返回 (xml 字符串, [component ids])，xml 为该目录下的 Component + 子 Directory。"""
    comps = []
    subdirs = []
    for fn in sorted(node['files']):
        rel = prefix + fn
        cid = "cmp_" + safe(rel)
        fid = "file_" + safe(rel)
        guid = uuid.uuid5(uuid.NAMESPACE_DNS, "PLCTesting/" + rel)
        comps.append(
            f'      <Component Id="{cid}" Guid="{{{guid}}}" KeyPath="yes">\n'
            f'        <File Id="{fid}" Source="$(var.SourceDir)/{rel}" />\n'
            f'      </Component>'
        )
        refs.append(cid)
    for dn in sorted(node['dirs'].keys()):
        child = node['dirs'][dn]
        inner, _ = render(child, prefix + dn + '/')
        did = "dir_" + safe(prefix + dn)
        subdirs.append(
            f'      <Directory Id="{did}" Name="{dn}">\n'
            f'{inner}\n'
            f'      </Directory>'
        )
    return '\n'.join(comps + subdirs), refs

body, refs = render(tree, "")

wxs = f'''<?xml version="1.0" encoding="UTF-8"?>
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
  <Fragment>
    <DirectoryRef Id="INSTALLFOLDER">
{body}
    </DirectoryRef>

    <ComponentGroup Id="AppFiles">
'''
for cid in refs:
    wxs += f'      <ComponentRef Id="{cid}" />\n'
wxs += '''    </ComponentGroup>
  </Fragment>
</Wix>
'''

os.makedirs(os.path.dirname(OUT), exist_ok=True)
with open(OUT, 'w', encoding='utf-8') as f:
    f.write(wxs)

print(f"files.wxs generated: {len(refs)} components, {len(files)} files -> {OUT}")

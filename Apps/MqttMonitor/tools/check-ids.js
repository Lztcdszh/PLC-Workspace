'use strict';
/* 自检：找出 app.js 里 $('xxx') 引用但 index.html 中不存在的元素 id */

const fs = require('fs');
const path = require('path');

/* 本文件在 tools/ 下，public/ 在项目根目录 */
const dir = path.join(__dirname, '..', 'public');
const js = fs.readFileSync(path.join(dir, 'app.js'), 'utf8');
const html = fs.readFileSync(path.join(dir, 'index.html'), 'utf8');

const used = new Set();
for (const m of js.matchAll(/\$\('([^']+)'\)/g)) used.add(m[1]);

const declared = new Set();
for (const m of html.matchAll(/\bid="([^"]+)"/g)) declared.add(m[1]);

const missing = [...used].filter((id) => !declared.has(id)).sort();
const unused = [...declared].filter((id) => !used.has(id)).sort();

console.log('app.js 引用的 id 数:', used.size);
console.log('index.html 定义的 id 数:', declared.size);
console.log('');
console.log(missing.length ? '❌ JS 引用了但 HTML 不存在（会报 null 错）:' : '✅ JS 引用的 id 全部存在');
for (const id of missing) console.log('   -', id);
console.log('');
console.log(unused.length ? 'ℹ️ HTML 有但 JS 未引用（可能是纯样式钩子）:' : 'ℹ️ 无多余 id');
for (const id of unused) console.log('   -', id);

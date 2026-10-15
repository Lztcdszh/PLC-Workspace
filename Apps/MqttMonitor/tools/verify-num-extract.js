'use strict';
/**
 * 报文数值提取回归（不需要起服务，秒级跑完）。
 *
 * 验的是 mqttBridge 里新加的 pickNumericField：它决定落盘库 num 列的值，
 * 也就是导出 CSV 里「数值」列的内容。
 * 背景见 tools/backfill-num.js 顶部注释。
 *
 *   node tools/verify-num-extract.js
 */
const { pickNumericField } = require('../src/mqttBridge');

let pass = 0;
let fail = 0;
function check(name, got, want) {
  const ok = got === null ? want === null : got === want;
  if (ok) pass += 1; else fail += 1;
  console.log(`  ${ok ? 'PASS' : 'FAIL'}  ${name}   [${JSON.stringify(got)}${ok ? '' : ` 期望 ${JSON.stringify(want)}`}]`);
}

console.log('');
console.log('══ 报文数值提取（num 列）══');
console.log('');
console.log('▸ 设备实际报文：value 是字符串也要认');
check('{"tag":…,"dataType":"Int16","value":"0",…} → 0', pickNumericField({
  tag: '电池电量', address: 'DB1.DBW290', description: '电量',
  dataType: 'Int16', value: '0', ts: '2026-09-28T10:57:09.1012518+08:00',
}), 0);
check('{"value":"23.5"} → 23.5', pickNumericField({ value: '23.5' }), 23.5);
check('{"value":"-7"} → -7', pickNumericField({ value: '-7' }), -7);

console.log('');
console.log('▸ 常见字段名的优先级与大小写');
check('{"val":1.5} → 1.5', pickNumericField({ val: 1.5 }), 1.5);
check('{"V":"12"}（大写） → 12', pickNumericField({ V: '12' }), 12);
check('{"pv":1.5} → 1.5', pickNumericField({ pv: 1.5 }), 1.5);
check('{"num":0} → 0（0 不能被当成"没有值"）', pickNumericField({ num: 0 }), 0);
check('{"value":9,"val":1} → 取 value 的 9', pickNumericField({ value: 9, val: 1 }), 9);

console.log('');
console.log('▸ 兜底：整个对象只有一个数值字段（兼容自定义 schema）');
check('{"temp":23.5} → 23.5', pickNumericField({ temp: 23.5 }), 23.5);
check('{"a":1,"b":2} 多个数值字段 → 猜不出来，null', pickNumericField({ a: 1, b: 2 }), null);
check('{"ts":1790584694860} 只有时间戳 → null（别把时间戳当测量值）', pickNumericField({ ts: 1790584694860 }), null);
check('{"created_at":123}（_at 结尾）也算时间戳 → null', pickNumericField({ created_at: 123 }), null);
check('{"updatedAt":123}（驼峰 At 结尾）也算时间戳 → null', pickNumericField({ updatedAt: 123 }), null);
check('{"format":7} 不像时间戳，正常认（`at` 结尾但前面不是分隔符）', pickNumericField({ format: 7 }), 7);
check('{"temp":23.5,"ts":123} 排除时间戳后剩一个 → 23.5', pickNumericField({ temp: 23.5, ts: 123 }), 23.5);

console.log('');
console.log('▸ 不该认的别硬认');
check('{"value":"ON"} 非数字字符串 → null', pickNumericField({ value: 'ON' }), null);
check('{"value":""} → null', pickNumericField({ value: '' }), null);
check('{"nested":{"value":5}} 不做深层挖掘 → null', pickNumericField({ nested: { value: 5 } }), null);
check('数组 → null（数组报文的语义交给前端挑字段）', pickNumericField([{ value: 5 }]), null);
check('{} → null', pickNumericField({}), null);
check('null → null', pickNumericField(null), null);
check('布尔字段按前端口径 ON=1', pickNumericField({ value: true }), 1);

console.log('');
console.log('────────────────────────────────────────');
console.log(`  通过 ${pass} 项，失败 ${fail} 项`);
console.log('────────────────────────────────────────');
console.log('');
process.exit(fail ? 1 : 0);

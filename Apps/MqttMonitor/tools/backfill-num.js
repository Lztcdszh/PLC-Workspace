'use strict';
/**
 * 给落盘库补「数值」列：把 num 为 NULL 的 JSON 报文解出一个数值。
 *
 * 背景：设备上报的是 {"tag":"电池电量","dataType":"Int16","value":"0",…}，
 * 老版本的服务端只把「裸数字」报文认成数值，JSON 报文的 num 一律 NULL，
 * 于是导出的 CSV「数值」列整列空白（曲线本身不受影响）。
 * 新版本起会在收报文时就抽好，本脚本是给**已经存下来的老数据**补课。
 *
 * 默认只统计不写库，确认无误后再加 --apply。
 *
 *   node tools/backfill-num.js              # 只统计
 *   node tools/backfill-num.js --apply      # 真的写库（脚本会先自动备份 .db 文件）
 */
const fs = require('fs');
const path = require('path');
const { HistoryStore } = require('../src/historyStore');
const config = require('../src/configStore');

const apply = process.argv.includes('--apply');

(async () => {
  const cfg = config.load().history || {};
  // 保留期沿用正式配置：open() 里会按这个天数清理过期行，
  // 用错值（比如默认值比正式配置小）会把用户的历史数据顺手删掉。
  const store = new HistoryStore({
    dir: cfg.dir || undefined,
    keepDays: cfg.keepDays,
    maxRows: cfg.maxRows,
  });
  const ok = store.open();
  if (!ok) {
    console.error(`✗ 历史库打不开：${store.reason}`);
    process.exit(1);
  }
  console.log(`库文件：${store.dbPath}`);

  const before = store.db.prepare('SELECT COUNT(*) c FROM samples').get().c;
  const nullNum = store.db.prepare('SELECT COUNT(*) c FROM samples WHERE num IS NULL').get().c;
  console.log(`总行数：${before}   其中 num 为空：${nullNum}`);

  // 先干跑一次，拿到要改多少
  const dry = store.backfillNum({ apply: false });
  console.log('');
  console.log(`扫描到 JSON 报文（num 为空）：${dry.scanned} 行`);
  console.log(`其中能解出数值的：${dry.fillable} 行`);
  if (dry.examples.length) console.log(`样例：${dry.examples.join('  |  ')}`);

  if (!dry.fillable) {
    console.log('');
    console.log('没有可补的行，收工。');
    store.close?.();
    process.exit(0);
  }

  if (!apply) {
    console.log('');
    console.log('（这是干跑。确认无误后执行：node tools/backfill-num.js --apply）');
    store.close?.();
    process.exit(0);
  }

  // 写库前先备份一份，万一要回退直接换回来
  const backup = `${store.dbPath}.bak-${Date.now()}`;
  try {
    store.flushNow();
    fs.copyFileSync(store.dbPath, backup);
    console.log('');
    console.log(`已备份：${backup}`);
  } catch (err) {
    console.error(`✗ 备份失败，为安全起见中止：${err.message}`);
    process.exit(1);
  }

  const res = store.backfillNum({ apply: true });
  const after = store.db.prepare('SELECT COUNT(*) c FROM samples WHERE num IS NULL').get().c;
  console.log(`已写入：${res.updated} 行`);
  console.log(`num 为空的行：${nullNum} → ${after}`);
  console.log('');
  console.log('完成。');
  store.close?.();
})().catch((err) => { console.error(`✗ ${err.message}`); process.exit(1); });

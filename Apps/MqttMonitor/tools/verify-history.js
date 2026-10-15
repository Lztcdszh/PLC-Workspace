'use strict';

/**
 * 历史落盘（SQLite）端到端验证
 *
 * 为什么是一个自包含脚本：起临时实例、发数据、断言、杀进程、再重启，
 * 全部在一个进程里完成 —— 不依赖任何后台常驻服务，跑完即净。
 *
 * 全程使用临时目录（MQTT_MONITOR_CONFIG / MQTT_MONITOR_DATA_DIR 指向 tmp），
 * 不动正式实例的 config.json 与 data/history.db。
 *
 * 用法：node tools/verify-history.js
 * 退出码：0 = 全部通过，1 = 有失败项
 */

const { spawn } = require('child_process');
const fs = require('fs');
const os = require('os');
const path = require('path');
const mqtt = require('mqtt');

// node:sqlite 会打实验性特性警告，验证脚本里静音掉，输出更干净
const _emitWarning = process.emitWarning;
process.emitWarning = function patched(w) {
  if (String(w).includes('SQLite is an experimental feature')) return;
  return _emitWarning.call(process, w);
};

const ROOT = path.join(__dirname, '..');
const WEB_PORT = 8091;
const BROKER_PORT = 18831;
const BASE = `http://127.0.0.1:${WEB_PORT}`;
const TOPIC_A = 'plc/DB1.DBW230';
const TOPIC_B = 'plc/DB1.DBW240';
const N_A = 12;
const N_B = 3;

const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'mqttmon-e2e-'));
const cfgPath = path.join(tmp, 'config.json');
const dataDir = path.join(tmp, 'data');
const dbPath = path.join(dataDir, 'history.db');

let pass = 0;
let fail = 0;
const log = [];

function check(name, ok, detail) {
  ok ? pass++ : fail++;
  const line = `${ok ? '  PASS' : '  FAIL'}  ${name}${detail ? `   [${detail}]` : ''}`;
  console.log(line);
  log.push(line);
  return ok;
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function getJson(p, opt) {
  const res = await fetch(BASE + p, opt);
  const text = await res.text();
  try { return { status: res.status, body: JSON.parse(text), raw: text }; }
  catch { return { status: res.status, body: null, raw: text }; }
}

async function waitOk(p, timeoutMs = 15000) {
  const t0 = Date.now();
  while (Date.now() - t0 < timeoutMs) {
    try {
      const r = await getJson(p);
      if (r.status === 200) return true;
    } catch { /* 还没起来 */ }
    await sleep(200);
  }
  return false;
}

/** 起一个临时实例（同端口，用来验证「重启后历史还在」） */
function startServer(tag) {
  const out = [];
  const child = spawn(process.execPath, [
    'server.js',
    '--port', String(WEB_PORT),
    '--broker-port', String(BROKER_PORT),
    '--no-sim',          // 不要模拟器，免得数据混进来
    '--no-open',         // 别弹浏览器
  ], {
    cwd: ROOT,
    env: { ...process.env, MQTT_MONITOR_CONFIG: cfgPath, MQTT_MONITOR_DATA_DIR: dataDir },
    stdio: ['ignore', 'pipe', 'pipe'],
    windowsHide: true,
  });
  child.stdout.on('data', (d) => out.push(String(d)));
  child.stderr.on('data', (d) => out.push('[stderr] ' + String(d)));
  return { child, out, tag };
}

/** 强杀（Windows 上等同 TerminateProcess，不给优雅关闭机会）——这样更能验证 WAL 的持久性 */
async function stopServer(srv) {
  if (srv.child.exitCode !== null || srv.child.signalCode) return;
  const done = new Promise((r) => srv.child.once('exit', r));
  srv.child.kill('SIGKILL');
  await Promise.race([done, sleep(4000)]);
}

/** Windows 上被 TerminateProcess 杀掉的子进程 exitCode 恒为 null，要用 signalCode/killed 判活 */
const isDead = (child) => child.exitCode !== null || child.signalCode !== null || child.killed;

/** 模拟设备：连上内置 broker，按用户真实报文格式发若干条 */
async function publishFromDevice(topic, n) {
  const client = mqtt.connect(`mqtt://127.0.0.1:${BROKER_PORT}`, {
    clientId: `e2e-device-${Date.now()}-${Math.random().toString(16).slice(2, 6)}`,
    clean: true,
    reconnectPeriod: 0,
    connectTimeout: 5000,
  });
  await new Promise((resolve, reject) => {
    const t = setTimeout(() => reject(new Error('连接 broker 超时')), 6000);
    client.once('connect', () => { clearTimeout(t); resolve(); });
    client.once('error', (e) => { clearTimeout(t); reject(e); });
  });
  for (let i = 0; i < n; i++) {
    // 完全照用户贴的真实报文：数值是字符串、ts 是 ISO
    const payload = JSON.stringify({
      tag: 'DB1.DBW230',
      address: 'DB1.DBW230',
      description: '小车状态',
      dataType: 'Int16',
      value: String(100 + i),
      ts: new Date().toISOString(),
    });
    await new Promise((res) => client.publish(topic, payload, { qos: 0 }, res));
    await sleep(25);
  }
  await sleep(250);
  await new Promise((res) => client.end(false, {}, res));
}

/** 轮询等落盘条数达标 */
async function waitRows(n, timeoutMs = 10000) {
  const t0 = Date.now();
  let last = null;
  while (Date.now() - t0 < timeoutMs) {
    const r = await getJson('/api/history/stats');
    last = r.body?.data;
    if (last && last.rows >= n) return last;
    await sleep(250);
  }
  return last;
}

async function main() {
  console.log('');
  console.log('══ 历史落盘（SQLite）端到端验证 ══');
  console.log(`   临时目录 ${tmp}`);
  console.log('');

  // ────────── 阶段 1：新库启动 + 设备发数据 ──────────
  console.log('▸ 阶段 1  启动临时实例，模拟设备上报');
  let srv = startServer('run1');
  if (!check('临时实例可访问', await waitOk('/api/state'))) {
    console.log(srv.out.join(''));
    return;
  }
  await sleep(400);
  const bootLog = srv.out.join('');
  check('启动日志确认已落盘', bootLog.includes('已落盘（SQLite）'),
    (bootLog.match(/历史数据.*/) || [''])[0].trim());
  check('启动日志打印库文件路径', bootLog.includes(dbPath), dbPath);

  const st0 = (await getJson('/api/history/stats')).body?.data;
  check('新库初始为空', st0?.rows === 0, `rows=${st0?.rows}`);
  check('默认保留 7 天', st0?.keepDays === 7, `keepDays=${st0?.keepDays}`);
  check('/api/state 带 historyState', !!(await getJson('/api/state')).body?.data?.historyState?.available);

  try {
    await publishFromDevice(TOPIC_A, N_A);
    await publishFromDevice(TOPIC_B, N_B);
    check(`设备上报 ${N_A + N_B} 条已发送`, true);
  } catch (err) {
    check('设备上报', false, err.message);
  }

  const total = N_A + N_B;
  const st1 = await waitRows(total);
  check(`落盘条数 = ${total}`, st1?.rows === total, `rows=${st1?.rows}, pending=${st1?.pendingRows}`);
  check('记录了主题数 = 2', st1?.topics === 2, `topics=${st1?.topics}`);
  check('库文件已生成', fs.existsSync(dbPath), `${((st1?.bytes || 0) / 1024).toFixed(1)} KB`);
  check('时间范围已记录', !!st1?.oldest && !!st1?.newest,
    st1?.oldest ? `${new Date(st1.oldest).toLocaleTimeString('zh-CN', { hour12: false })} ~ ${new Date(st1.newest).toLocaleTimeString('zh-CN', { hour12: false })}` : '空');

  const hist1 = (await getJson(`/api/history?topic=${encodeURIComponent(TOPIC_A)}&limit=100`)).body?.data;
  check(`从库回填 ${TOPIC_A} 共 ${N_A} 条`, Array.isArray(hist1) && hist1.length === N_A, `实际 ${hist1?.length}`);
  check('回填记录带解析后的 json', hist1?.[0]?.json?.value === '100', `json.value=${hist1?.[0]?.json?.value}`);
  check('回填记录按时间升序', hist1?.length > 1 && hist1[0].ts <= hist1[hist1.length - 1].ts);
  // 设备报文是 {"dataType":"Int16","value":"100",…}（value 是**字符串**）。
  // 老版本只把「裸数字」报文认成数值，这里 num 会全是 null，
  // 连带导出的 CSV「数值」列整列空白。
  check('★ 收报文时就把 JSON 里的 value 抽成了 num', hist1?.[0]?.num === 100, `第一条 num=${hist1?.[0]?.num}`);
  check('★ 全部记录的 num 都不为空', (hist1 || []).every((r) => typeof r.num === 'number'),
    `空值 ${(hist1 || []).filter((r) => r.num === null).length} 条`);

  const tl = (await getJson('/api/history/topics')).body?.data;
  check('/api/history/topics 列出 2 个主题', Array.isArray(tl) && tl.length === 2,
    (tl || []).map((t) => `${t.topic}×${t.count}`).join(', '));

  // CSV 导出
  const csvRes = await fetch(`${BASE}/api/history/export?topic=${encodeURIComponent(TOPIC_A)}`);
  const csvBuf = Buffer.from(await csvRes.arrayBuffer());
  const hasBom = csvBuf[0] === 0xEF && csvBuf[1] === 0xBB && csvBuf[2] === 0xBF;
  const csvText = csvBuf.toString('utf8').replace(/^\uFEFF/, '');
  const csvLines = csvText.trim().split(/\r?\n/);
  check('CSV 带 UTF-8 BOM（Excel 不乱码）', hasBom);
  check(`CSV 表头 + ${N_A} 行数据`, csvLines.length === N_A + 1 && csvLines[0] === '时间,主题,数值,原始载荷',
    `行数=${csvLines.length}`);
  check('CSV 带下载文件名头', /attachment;.*mqtt-history-/.test(csvRes.headers.get('content-disposition') || ''),
    (csvRes.headers.get('content-disposition') || '').slice(0, 40));
  // 前三列是「时间,主题,数值」，原始载荷在后（含逗号、被引号包住），所以只截前三段
  const csvNums = csvLines.slice(1).map((l) => l.split(',')[2]);
  const wantNums = Array.from({ length: N_A }, (_, i) => String(100 + i));
  check('★ CSV「数值」列不再是空白，就是设备上报的那些值', csvNums.join(',') === wantNums.join(','),
    `前 3 行 = ${csvNums.slice(0, 3).join(' / ')}`);

  // ────────── 阶段 2：强杀进程（模拟断电/崩溃）──────────
  console.log('');
  console.log('▸ 阶段 2  强杀进程（不给优雅退出机会），验证数据不丢');
  await stopServer(srv);
  check('临时实例已退出', isDead(srv.child), `exitCode=${srv.child.exitCode}, signal=${srv.child.signalCode}`);
  check('强杀后库文件仍在磁盘', fs.existsSync(dbPath));

  // 直接往库里插一条 5 天前的老数据：
  // 它还在 7 天保留期内 → 重启后应该能读到；等下把保留期改成 1 天，它就该被裁掉
  const { DatabaseSync } = require('node:sqlite');
  const raw = new DatabaseSync(dbPath);
  const oldTs = Date.now() - 5 * 86400000;
  raw.prepare('INSERT INTO samples (topic, ts, num, payload) VALUES (?, ?, ?, ?)')
    .run('plc/DB1.DBW999', oldTs, 42, '{"value":"42"}');
  check('离线写入 1 条 5 天前的老数据', true, `ts=${new Date(oldTs).toLocaleDateString('zh-CN')}`);
  raw.close();

  // ────────── 阶段 3：重启，验证历史仍在 ──────────
  console.log('');
  console.log('▸ 阶段 3  重启实例，验证历史不丢（核心诉求）');
  srv = startServer('run2');
  if (!check('重启后实例可访问', await waitOk('/api/state'))) {
    console.log(srv.out.join(''));
    return;
  }
  await sleep(500);

  const st2 = (await getJson('/api/history/stats')).body?.data;
  check(`重启后仍有 ${total + 1} 条（含老数据）`, st2?.rows === total + 1, `rows=${st2?.rows}`);
  check('启动日志显示「已有 N 条」', /已有\s+[\d,]+\s*条/.test(srv.out.join('')),
    (srv.out.join('').match(/已有.*/) || [''])[0].trim());

  const hist2 = (await getJson(`/api/history?topic=${encodeURIComponent(TOPIC_A)}&limit=100`)).body?.data;
  check('重启后曲线数据仍能回填', Array.isArray(hist2) && hist2.length === N_A, `实际 ${hist2?.length} 条`);
  const tl2 = (await getJson('/api/history/topics')).body?.data;
  check('重启后老数据主题可见', (tl2 || []).some((t) => t.topic === 'plc/DB1.DBW999'));

  // 重启后继续上报：新数据应该追加在后，老数据不受影响（验证长期运行不覆盖）
  const N_APPEND = 2;
  try { await publishFromDevice(TOPIC_A, N_APPEND); } catch (err) { check('重启后上报', false, err.message); }
  const st2b = await waitRows(total + 1 + N_APPEND);
  const afterAll = total + 1 + N_APPEND;                 // 17 条
  check(`重启后追加 ${N_APPEND} 条，共 ${afterAll} 条`, st2b?.rows === afterAll, `rows=${st2b?.rows}`);
  const hist2b = (await getJson(`/api/history?topic=${encodeURIComponent(TOPIC_A)}&limit=100`)).body?.data;
  check('追加后旧数据未被覆盖', hist2b?.length === N_A + N_APPEND && hist2b[0]?.json?.value === '100',
    `${hist2b?.length} 条，首条 value=${hist2b?.[0]?.json?.value}`);

  // ────────── 阶段 4：保留策略裁剪 ──────────
  console.log('');
  console.log('▸ 阶段 4  改保留天数为 1 天，验证超期自动清理');
  const kd = await getJson('/api/history/keep-days', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ keepDays: 1 }),
  });
  const kdData = kd.body?.data;
  check('接口返回新保留天数', kdData?.keepDays === 1, `keepDays=${kdData?.keepDays}`);
  check('超期老数据被清掉', kdData?.deleted >= 1, `deleted=${kdData?.deleted}`);
  check(`剩余 ${afterAll - 1} 条（保留期内数据未受影响）`, kdData?.stats?.rows === afterAll - 1,
    `rows=${kdData?.stats?.rows}`);
  const tl3 = (await getJson('/api/history/topics')).body?.data;
  check('老数据主题已消失', !(tl3 || []).some((t) => t.topic === 'plc/DB1.DBW999'));

  // 改配置后要落进 config.json，重启才记得住
  const savedCfg = JSON.parse(fs.readFileSync(cfgPath, 'utf8'));
  check('保留天数已写入配置文件', savedCfg.history?.keepDays === 1, `config.history.keepDays=${savedCfg.history?.keepDays}`);
  await getJson('/api/history/keep-days', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ keepDays: 7 }),
  });

  // ────────── 阶段 5：一键清空（内存 + 落盘）──────────
  console.log('');
  console.log('▸ 阶段 5  一键清空，验证内存与落盘一起清');
  const reset = (await getJson('/api/reset', { method: 'POST' })).body?.data;
  check(`清空返回清理行数 ${afterAll - 1}`, reset?.purgedRows === afterAll - 1, `purgedRows=${reset?.purgedRows}`);
  const st3 = (await getJson('/api/history/stats')).body?.data;
  check('库已清空', st3?.rows === 0, `rows=${st3?.rows}`);
  const hist3 = (await getJson(`/api/history?topic=${encodeURIComponent(TOPIC_A)}`)).body?.data;
  check('清空后查历史为空', Array.isArray(hist3) && hist3.length === 0, `实际 ${hist3?.length}`);

  await stopServer(srv);
  console.log('');
  console.log('▸ 清理临时目录');
  try { fs.rmSync(tmp, { recursive: true, force: true }); console.log(`  已删除 ${tmp}`); }
  catch (err) { console.log(`  删除失败（可手动清理）：${err.message}`); }
}

main()
  .catch((err) => {
    console.error('\n验证脚本异常：', err);
    fail++;
  })
  .finally(() => {
    console.log('');
    console.log('════════════════════════════════════════');
    console.log(`  结果：${pass} 项通过，${fail} 项失败`);
    console.log('════════════════════════════════════════');
    console.log('');
    process.exit(fail ? 1 : 0);
  });

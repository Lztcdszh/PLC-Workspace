'use strict';

/**
 * 「曲线显示 XY 坐标值」的前端验证（真实浏览器，无头 Edge + CDP）
 *
 * 为什么要有这个脚本：
 *   canvas 画的刻度是"像素"，不是 DOM —— 单测跑不到、肉眼又容易漏。
 *   这里在真实 Edge 里打开页面，**勾住 CanvasRenderingContext2D.prototype.fillText**，
 *   直接把"画了哪些文字、画在哪个坐标、什么对齐方式"记下来，
 *   再断言这些文字确实落在刻度栏里、数值确实是"好看"的步长。
 *   比去数像素点可靠得多（曲线本身也是像素，会干扰判断）。
 *
 * 另外把 niceTicks / fmtAxis 从源码里抠出来在 Node 里做纯函数断言。
 *
 * 全程用临时目录，不动正式实例的 config.json / data。
 * 用法：node tools/verify-axis-ui.js
 */

const { spawn } = require('child_process');
const fs = require('fs');
const os = require('os');
const path = require('path');
const mqtt = require('mqtt');
const WebSocket = require('ws');

const _emitWarning = process.emitWarning;
process.emitWarning = function patched(w) {
  if (String(w).includes('SQLite is an experimental feature')) return;
  return _emitWarning.call(process, w);
};

const ROOT = path.join(__dirname, '..');
const EDGE = 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';
const WEB_PORT = 8093;
const BROKER_PORT = 18833;
const CDP_PORT = 9335;
const BASE = `http://127.0.0.1:${WEB_PORT}`;
const VIEW = { width: 1500, height: 900 };

const REAL_TOPIC = 'plc/axis.real';     // 走真实 broker 链路（验 ts 端到端传下来）
const SPREAD_TOPIC = 'plc/axis.spread'; // 页面内注入，跨 40 分钟（跟随全局 X 轴范围）
const SPREAD_TOPIC2 = 'plc/axis.spread2'; // 同样跨 40 分钟，但单卡覆盖成“全部”
const N_REAL = 40;
const N_SPREAD = 40;

const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'mqttmon-axis-'));
const cfgPath = path.join(tmp, 'config.json');
// 本脚本要的是"一个主题一张卡"（要对照两张卡的坐标轴）：关掉同前缀自动合并，
// 否则 plc/axis.real / plc/axis.spread / plc/axis.spread2 会被并成一张卡（见 verify-group.js）。
fs.writeFileSync(cfgPath, JSON.stringify({ ui: { autoGroup: false } }), 'utf8');
const dataDir = path.join(tmp, 'data');
const profileDir = path.join(tmp, 'edgeprofile');
const shotPath = path.join(ROOT, 'tools', '_ui_axis.png');
const zoomPath = path.join(ROOT, 'tools', '_ui_axis_zoom.png');

let pass = 0;
let fail = 0;
function check(name, ok, detail) {
  ok ? pass++ : fail++;
  console.log(`${ok ? '  PASS' : '  FAIL'}  ${name}${detail !== undefined ? `   [${detail}]` : ''}`);
  return ok;
}
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function fetchOk(url, timeoutMs = 20000) {
  const t0 = Date.now();
  while (Date.now() - t0 < timeoutMs) {
    try {
      const r = await fetch(url);
      if (r.ok) return r;
    } catch { /* 还没起来 */ }
    await sleep(250);
  }
  throw new Error(`等待超时：${url}`);
}

function startPlatform() {
  const out = [];
  const child = spawn(process.execPath, [
    'server.js', '--port', String(WEB_PORT), '--broker-port', String(BROKER_PORT),
    '--no-sim', '--no-open',
  ], {
    cwd: ROOT,
    env: { ...process.env, MQTT_MONITOR_CONFIG: cfgPath, MQTT_MONITOR_DATA_DIR: dataDir },
    stdio: ['ignore', 'pipe', 'pipe'],
    windowsHide: true,
  });
  child.stdout.on('data', (d) => out.push(String(d)));
  child.stderr.on('data', (d) => out.push('[stderr] ' + String(d)));
  return { child, out };
}

/** 设备侧发报文：锯齿上升，量程 100 ~ 1450，好让 Y 轴刻度看得出分档 */
async function publishFromDevice(topic, n) {
  const client = mqtt.connect(`mqtt://127.0.0.1:${BROKER_PORT}`, {
    clientId: `axis-dev-${Date.now()}-${Math.random().toString(16).slice(2, 6)}`,
    clean: true, reconnectPeriod: 0, connectTimeout: 5000,
  });
  await new Promise((resolve, reject) => {
    const t = setTimeout(() => reject(new Error('连接 broker 超时')), 6000);
    client.once('connect', () => { clearTimeout(t); resolve(); });
    client.once('error', (e) => { clearTimeout(t); reject(e); });
  });
  for (let i = 0; i < n; i++) {
    const v = 100 + ((i * 37) % 1350);
    await new Promise((res) => client.publish(topic, JSON.stringify({
      tag: 'DB1.DBW230', address: 'DB1.DBW230', description: '小车状态',
      dataType: 'Int16', value: String(v), ts: new Date().toISOString(),
    }), { qos: 0 }, res));
    await sleep(25);
  }
  await sleep(250);
  await new Promise((res) => client.end(false, {}, res));
}

/** 极简 CDP 客户端：按 id 路由回复，其余当事件收 */
function cdpConnect(wsUrl) {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(wsUrl, { maxPayload: 128 * 1024 * 1024 });
    let msgId = 0;
    const pending = new Map();
    const exceptions = [];
    const consoles = [];
    ws.on('message', (raw) => {
      let m;
      try { m = JSON.parse(raw.toString()); } catch { return; }
      if (m.id !== undefined && pending.has(m.id)) {
        const p = pending.get(m.id);
        pending.delete(m.id);
        m.error ? p.reject(new Error(m.error.message)) : p.resolve(m.result);
        return;
      }
      if (m.method === 'Runtime.exceptionThrown') {
        const d = m.params?.exceptionDetails || {};
        exceptions.push(d.exception?.description || d.text || '未知异常');
      } else if (m.method === 'Runtime.consoleAPICalled') {
        consoles.push({
          type: m.params?.type,
          text: (m.params?.args || []).map((a) => a.value ?? a.description ?? '').join(' '),
        });
      }
    });
    ws.on('error', reject);
    ws.on('open', () => {
      const send = (method, params, sessionId) => new Promise((res, rej) => {
        const id = ++msgId;
        pending.set(id, { resolve: res, reject: rej });
        ws.send(JSON.stringify({ id, method, params: params || {}, ...(sessionId ? { sessionId } : {}) }));
      });
      const evaluate = async (sessionId, expression) => {
        const r = await send('Runtime.evaluate', {
          expression, returnByValue: true, awaitPromise: true,
        }, sessionId);
        if (r.exceptionDetails) {
          throw new Error(r.exceptionDetails.exception?.description || '页面内求值失败');
        }
        return r.result?.value;
      };
      resolve({ send, evaluate, exceptions, consoles, close: () => ws.close() });
    });
  });
}

/**
 * 杀整个进程树。
 * Edge 会 fork 出一堆子进程，只 kill 主进程的话它们仍活着并占着 profile 目录，
 * 后续 rm 就会卡在文件锁上 —— 表现为脚本跑完不退出。
 */
function killTree(child) {
  if (!child || child.pid === undefined) return;
  try {
    if (process.platform === 'win32') {
      spawn('taskkill', ['/F', '/T', '/PID', String(child.pid)], { stdio: 'ignore', windowsHide: true });
    }
  } catch { /* ignore */ }
  try { child.kill('SIGKILL'); } catch { /* ignore */ }
}

// ============================================================
//  一、纯函数断言：把 niceTicks / fmtAxis 从源码里抠出来跑
// ============================================================
function extractPureFns() {
  const src = fs.readFileSync(path.join(ROOT, 'public', 'app.js'), 'utf8');
  const grab = (name) => {
    const i = src.indexOf(`function ${name}(`);
    if (i < 0) throw new Error(`源码里找不到 ${name}`);
    const end = src.indexOf('\n  }', i);
    if (end < 0) throw new Error(`${name} 找不到结束大括号`);
    return src.slice(i, end + 4);
  };
  const names = ['fmtTime', 'fmtAxisTime', 'niceTicks', 'fmtAxis', 'fmtDuration', 'windowLabel', 'normWindow'];
  const body = `${names.map(grab).join('\n')}\nreturn { ${names.join(', ')} };`;
  return new Function(body)();
}

function unitTests() {
  console.log('▸ 纯函数断言（直接跑 public/app.js 里的实现）');
  let fns;
  try {
    fns = extractPureFns();
  } catch (e) {
    check('能从 app.js 抠出 niceTicks / fmtAxis', false, e.message);
    return;
  }
  check('能从 app.js 抠出 niceTicks / fmtAxis', true);
  const { fmtTime, fmtAxisTime, niceTicks, fmtAxis, fmtDuration, windowLabel, normWindow } = fns;

  const eq = (label, got, want) => check(
    label, JSON.stringify(got) === JSON.stringify(want),
    `得到 ${JSON.stringify(got)}`,
  );

  // niceTicks：步长必须是 1 / 2 / 5 × 10^n
  eq('niceTicks(0,100,2) → [0,50,100]', niceTicks(0, 100, 2), [0, 50, 100]);
  eq('niceTicks(0,10,2) → [0,5,10]', niceTicks(0, 10, 2), [0, 5, 10]);
  eq('niceTicks(0,1,4) → [0,0.5,1]', niceTicks(0, 1, 4), [0, 0.5, 1]);
  eq('niceTicks(62,64,2) → [62,63,64]', niceTicks(62, 64, 2), [62, 63, 64]);
  eq('niceTicks(5,5,2) 退化 → [5]', niceTicks(5, 5, 2), [5]);
  check('niceTicks 刻度数落在合理区间(2~8)',
    (() => { const t = niceTicks(100, 1450, 2); return t.length >= 2 && t.length <= 8; })(),
    JSON.stringify(niceTicks(100, 1450, 2)));

  // fmtAxis：按量程决定小数位，大数走 k / M
  eq('fmtAxis(1000000) → 1.0M', fmtAxis(1000000, 1000), '1.0M');
  eq('fmtAxis(100000) → 100k', fmtAxis(100000, 1000), '100k');
  eq('fmtAxis(12500,1000) → 12.5k', fmtAxis(12500, 1000), '12.5k');
  eq('fmtAxis(1450,1350) → 1450（无多余小数）', fmtAxis(1450, 1350), '1450');
  eq('fmtAxis(62.5,4) → 62.5', fmtAxis(62.5, 4), '62.5');
  eq('fmtAxis(0.85,0.1) → 0.85', fmtAxis(0.85, 0.1), '0.85');
  eq('fmtAxis(0,1) → 0（不显示 0.00）', fmtAxis(0, 1), '0');
  check('fmtAxis 对非法值不抛异常',
    fmtAxis(NaN, 1) === '' && fmtAxis(Infinity, 1) === '', `NaN→"${fmtAxis(NaN, 1)}"`);

  // X 轴时间格式：跨度大只标到分钟
  const t0 = new Date(2026, 8, 28, 12, 38, 49).getTime();
  eq('fmtAxisTime 跨度 40 分钟 → 只到分钟', fmtAxisTime(t0, 40 * 60000), '12:38');
  eq('fmtAxisTime 跨度 30 分钟 → 只到分钟', fmtAxisTime(t0, 30 * 60000), '12:38');
  eq('fmtAxisTime 跨度正好 60 秒 → 已到分钟档', fmtAxisTime(t0, 60000), '12:38');
  eq('fmtAxisTime 跨度 59999ms（差 1ms 不到 1 分钟）→ 仍带秒', fmtAxisTime(t0, 59999), '12:38:49');
  eq('fmtAxisTime 跨度 30 秒 → 带秒', fmtAxisTime(t0, 30000), '12:38:49');

  // 窗口文案
  eq('windowLabel(0) → 全部', windowLabel(0), '全部');
  eq('windowLabel(30) → 30 分钟', windowLabel(30), '30 分钟');
  eq('windowLabel(60) → 1 小时', windowLabel(60), '1 小时');
  eq('windowLabel(240) → 4 小时', windowLabel(240), '4 小时');
  eq('windowLabel(1440) → 1 天', windowLabel(1440), '1 天');

  // 时长文案
  eq('fmtDuration(45s) → 45 秒', fmtDuration(45 * 1000), '45 秒');
  eq('fmtDuration(30min) → 30 分钟', fmtDuration(30 * 60 * 1000), '30 分钟');
  eq('fmtDuration(134min) → 2 小时 14 分', fmtDuration(134 * 60 * 1000), '2 小时 14 分');
  eq('fmtDuration(26h) → 1 天 2 小时', fmtDuration(26 * 3600 * 1000), '1 天 2 小时');

  // 卡片窗口归一：'' / null → null（跟随全局），0 是有效值不能丢
  eq('normWindow("") → null（跟随全局）', normWindow(''), null);
  eq('normWindow(null) → null', normWindow(null), null);
  eq('normWindow(undefined) → null', normWindow(undefined), null);
  eq('normWindow(0) → 0（显式全部，不能变成 null）', normWindow(0), 0);
  eq('normWindow("0") → 0', normWindow('0'), 0);
  eq('normWindow(5) → 5', normWindow(5), 5);
  eq('normWindow("30") → 30', normWindow('30'), 30);
  eq('normWindow(-1) → null（非法值退回跟随全局）', normWindow(-1), null);
  eq('normWindow("abc") → null', normWindow('abc'), null);
}

// ============================================================
//  二、页面内探针
// ============================================================
/** 勾 fillText + 给画布打标签，之后所有绘制文字都会被记录 */
const HOOK_JS = `(() => {
  const M = window.MQTTMonitor;
  if (!M) return 'no MQTTMonitor';
  if (!window.__axisLog) {
    window.__axisLog = [];
    const P = CanvasRenderingContext2D.prototype;
    const orig = P.fillText;
    P.fillText = function (text, x, y) {
      try {
        const cv = this.canvas;
        let tag = cv && cv.__tag;
        // 勾子装好之后才新建的卡片拿不到 __tag，就用所在 .card 的 data-topic 兜底
        if (tag === undefined && cv && cv.closest) {
          const host = cv.closest('.card');
          if (host) { tag = host.dataset.topic; cv.__tag = tag; }
        }
        window.__axisLog.push({
          tag: tag, text: String(text), x: x, y: y,
          align: this.textAlign, font: this.font,
          cw: cv ? cv.clientWidth : 0,
          ch: cv ? cv.clientHeight : 0,
        });
        if (window.__axisLog.length > 6000) window.__axisLog.splice(0, 3000);
      } catch (e) { /* 记录失败不能影响绘制 */ }
      return orig.apply(this, arguments);
    };
  }
  M.state.cards.forEach((c) => { if (c.el && c.el.canvas) c.el.canvas.__tag = c.topic; });
  return 'hooked';
})()`;

// ============================================================
//  注入数据：两张卡片的曲线跨度完全一样（40 分钟、每分钟一条），
//  差别只在"X 轴范围"设在哪 —— 这样能干净地对比全局默认与单卡覆盖。
// ============================================================
function makeSpreadItems(topic) {
  const base = Date.now() - (N_SPREAD - 1) * 60 * 1000;
  const items = [];
  for (let i = 0; i < N_SPREAD; i++) {
    const v = 100 + Math.round(650 + 600 * Math.sin(i / 5));
    items.push({
      topic,
      ts: base + i * 60 * 1000,
      payload: String(v),
      num: v,
      json: null,
      binary: false,
      qos: 0, retain: false, size: 3,
    });
  }
  return items;
}

const SPREAD_ITEMS = makeSpreadItems(SPREAD_TOPIC);
const SPREAD2_ITEMS = makeSpreadItems(SPREAD_TOPIC2);

/** ts → 与页面 fmtAxisTime 在"跨度 ≥1 分钟"时一致的 HH:MM */
function hhmm(ts) {
  const d = new Date(ts);
  const p = (n) => String(n).padStart(2, '0');
  return `${p(d.getHours())}:${p(d.getMinutes())}`;
}

const INJECT_SPREAD_JS = `(() => {
  const M = window.MQTTMonitor;
  M.ingestMessages(${JSON.stringify(SPREAD_ITEMS)});
  M.ingestMessages(${JSON.stringify(SPREAD2_ITEMS)});
  // 第二张卡片单卡覆盖成"全部"，用来对照全局默认没影响到它
  const c2 = M.state.cards.find(x => x.topic === ${JSON.stringify(SPREAD_TOPIC2)});
  if (c2) c2.windowMin = 0;
  return M.state.cards.filter(c => c.topic.indexOf('plc/axis.spread') === 0).length;
})()`;

/** 从 fillText 记录里筛出某张卡片的刻度文字 */
const REPORT_JS = (topic) => `(() => {
  const log = (window.__axisLog || []).filter(e => e.tag === ${JSON.stringify(topic)});
  const M = window.MQTTMonitor;
  const card = M.state.cards.find(c => c.topic === ${JSON.stringify(topic)});
  const cv = card && card.el && card.el.canvas;
  const timeRe = /^\\d{2}:\\d{2}(:\\d{2})?$/;
  const numRe = /^-?[\\d.]+[kM]?$/;
  const ch = cv ? cv.clientHeight : 0;

  // 跟 drawCard 用同一套取数逻辑：有数据的曲线，主曲线被挪到末尾；
  // Y 轴量程取 main，X 轴时间取 lines[0]。
  let lines = card ? (card.series || []).filter(s => s.data.length > 0) : [];
  if (lines.length > 1 && card.main) {
    const mi = lines.indexOf(card.main);
    if (mi >= 0 && mi < lines.length - 1) lines.push(lines.splice(mi, 1)[0]);
  }
  const ySer = lines.length === 1 ? lines[0]
    : (card.main && lines.includes(card.main) ? card.main : lines[lines.length - 1]);
  const xSer = lines[0] || null;

  // Y 刻度是按数值升序画的 → y 严格递减，按这个单调性把累积的记录切成一轮轮。
  function splitRounds(items, key, dir) {
    const rounds = []; let cur = [];
    for (const it of items) {
      if (cur.length) {
        const prev = cur[cur.length - 1][key];
        if (dir < 0 ? !(it[key] < prev) : !(it[key] > prev)) { rounds.push(cur); cur = []; }
      }
      cur.push(it);
    }
    if (cur.length) rounds.push(cur);
    return rounds;
  }
  const map = (e) => ({ text: e.text, x: e.x, y: e.y, align: e.align, ch: e.ch, cw: e.cw });

  const yAll = log.filter(e => e.align === 'right' && numRe.test(e.text)).map(map);
  const xAll = log.filter(e => timeRe.test(e.text) && e.y > ch - 20).map(map);
  const yRounds = splitRounds(yAll, 'y', -1);

  // X 标签不能用 x 单调性切轮次 —— 画图顺序是「左、右、中点」，x 是 29 / 705 / 367，不是递增的。
  // 同一轮的三条 X 标签共享同一个 y，所以从末尾往前收同 y 的连续记录。
  let xRound = [];
  if (xAll.length) {
    const ty = xAll[xAll.length - 1].y;
    for (let i = xAll.length - 1; i >= 0; i -= 1) {
      if (xAll[i].y !== ty) break;
      xRound.unshift(xAll[i]);
    }
  }
  // 再按对齐方式去重（左/中/右各留一条），最后按 x 升序排成「左 → 中 → 右」
  const seen = new Set();
  const xDedup = [];
  for (let i = xRound.length - 1; i >= 0; i -= 1) {
    if (seen.has(xRound[i].align)) continue;
    seen.add(xRound[i].align);
    xDedup.push(xRound[i]);
  }
  xDedup.sort((a, b) => a.x - b.x);

  return {
    hasCard: !!card,
    cw: cv ? cv.clientWidth : 0, ch: ch,
    seriesCount: card ? card.series.length : 0,
    liveCount: lines.length,
    yKey: ySer ? ySer.key : null,
    xKey: xSer ? xSer.key : null,
    // 缓冲里的原始点数（未经窗口裁剪）
    bufLen: xSer ? xSer.data.length : 0,
    // ⚠️ 下面这些读的是产品自己挂的 card._chart（drawCard 每次重绘都会更新），
    // 不是探针自己再实现一遍窗口逻辑 —— 这样断言的是"真的画了什么"。
    chart: (card && card._chart) || null,
    drawnPoints: card && card._chart ? card._chart.points : -1,
    drawnWinMin: card && card._chart ? card._chart.winMin : -1,
    cardWindowMin: card ? (card.windowMin === undefined ? 'MISSING' : card.windowMin) : 'NOCARD',
    globalWindowMin: (M.state.ui && M.state.ui.chartWindowMin === undefined)
      ? 'MISSING' : (M.state.ui && M.state.ui.chartWindowMin),
    yMin: ySer ? ySer._min : null,
    yMax: ySer ? ySer._max : null,
    tsLen: xSer && xSer.ts ? xSer.ts.length : 0,
    tsFirst: xSer && xSer.ts ? xSer.ts[0] : null,
    tsLast: xSer && xSer.ts ? xSer.ts[xSer.ts.length - 1] : null,
    tsMonotonic: xSer && xSer.ts
      ? xSer.ts.every((t, i) => i === 0 || t >= xSer.ts[i - 1]) : false,
    // 不变量：**每条**曲线的 ts 都要和 data 等长（不只是被画的那条）
    allTsAligned: card ? (card.series || []).every(s => (s.ts || []).length === s.data.length) : false,
    // 注意：这里是模板字符串，页面里要写的插值会被 Node 抢先求值，所以用字符串拼接
    seriesTsLens: card ? (card.series || []).map(s => s.key + ':' + s.data.length + '/' + (s.ts || []).length) : [],
    y: yRounds.length ? yRounds[yRounds.length - 1] : [],
    x: xDedup,
    yAllCount: yAll.length,
    xAllCount: xAll.length,
  };
})()`;

/** 清空绘制记录 —— 这样随后读到的就是「这一次重绘」的刻度，不用猜哪几条是同一轮 */
const RESET_LOG_JS = `(() => { if (window.__axisLog) window.__axisLog.length = 0; return 1; })()`;

/**
 * 强制重绘全部卡片。
 * 用 markDirty 而不是再灌一条报文 —— 灌报文会污染数据：
 * 裸数字报文走的是 __v 曲线，和 JSON 报文的 value 曲线不是同一条，
 * 会凭空多出一条 1 个点的短曲线，反而把被测的图表搅浑。
 */
const REDRAW_ALL_JS = `(() => {
  const M = window.MQTTMonitor;
  M.state.cards.forEach((c) => M.markDirty(c));
  return M.state.cards.length;
})()`;

async function main() {
  console.log('');
  console.log('══ 「曲线显示 XY 坐标值」前端验证（无头 Edge + CDP）══');
  console.log(`   临时目录 ${tmp}`);
  console.log('');
  console.log('▸ 启动临时平台');
  const app = startPlatform();

  let edge;
  let cdp;
  try {
    await fetchOk(`${BASE}/api/state`);
    check('临时平台已就绪', true, BASE);

    console.log('');
    console.log('▸ 启动无头 Edge');
    fs.mkdirSync(profileDir, { recursive: true });
    edge = spawn(EDGE, [
      '--headless=new', '--disable-gpu', '--no-sandbox', '--hide-scrollbars', '--no-first-run',
      `--remote-debugging-port=${CDP_PORT}`, `--user-data-dir=${profileDir}`,
      `--window-size=${VIEW.width},${VIEW.height}`, 'about:blank',
    ], { stdio: 'ignore', windowsHide: true });

    const verRes = await fetchOk(`http://127.0.0.1:${CDP_PORT}/json/version`);
    const ver = await verRes.json();
    cdp = await cdpConnect(ver.webSocketDebuggerUrl);
    check('CDP 已连上浏览器', true, ver.Browser);

    const { targetId } = await cdp.send('Target.createTarget', { url: 'about:blank' });
    const { sessionId } = await cdp.send('Target.attachToTarget', { targetId, flatten: true });
    await cdp.send('Page.enable', null, sessionId);
    await cdp.send('Runtime.enable', null, sessionId);
    await cdp.send('Page.navigate', { url: BASE }, sessionId);

    let booted = false;
    for (let i = 0; i < 60; i++) {
      await sleep(250);
      const ok = await cdp.evaluate(sessionId, `!!window.MQTTMonitor`);
      if (ok) { booted = true; break; }
    }
    check('页面已加载（MQTTMonitor 就绪）', booted);
    if (!booted) throw new Error('页面没起来，后面的断言没意义');

    // 关键顺序：**页面先开着，再发设备数据**。
    // 因为「历史回放」只回填配置里已存在的卡片，而卡片是收到实时消息才自动上板的 ——
    // 先发后开页面的话，那批数据只进库、不进看板。
    console.log('');
    console.log('▸ 设备侧发报文（页面已在线，走真实 broker 链路）');
    await publishFromDevice(REAL_TOPIC, N_REAL);
    const st = (await (await fetch(`${BASE}/api/history/stats`)).json()).data;
    check(`设备报文已进平台（${N_REAL} 条）`, st?.rows >= N_REAL, `rows=${st?.rows}`);

    let ready = false;
    for (let i = 0; i < 30; i++) {
      await sleep(300);
      const n = await cdp.evaluate(sessionId,
        `(() => { const M = window.MQTTMonitor; const c = M.state.cards.find(x => x.topic === ${JSON.stringify(REAL_TOPIC)});
                  return c ? (c.series[0] ? c.series[0].data.length : 0) : -1; })()`);
      if (n >= 5) { ready = true; break; }
    }
    check('实时链路把曲线推到了页面', ready);

    // 让它把曲线和历史都吃满
    await sleep(1200);

    console.log('');
    console.log('▸ 卡片结构（排查用）');
    const dump = await cdp.evaluate(sessionId, `(() => {
      const c = window.MQTTMonitor.state.cards.find(x => x.topic === ${JSON.stringify(REAL_TOPIC)});
      if (!c) return null;
      return { topic: c.topic, name: c.name, points: c.points, main: c.main && c.main.key,
               series: c.series.map(s => ({ key: s.key, n: s.data.length, ts: s.ts ? s.ts.length : -1,
                                            first: s.data[0], last: s.data[s.data.length-1] })) };
    })()`);
    console.log('    ' + JSON.stringify(dump));

    console.log('');
    console.log('▸ 勾住 fillText，观察刻度文字');
    const hooked = await cdp.evaluate(sessionId, HOOK_JS);
    check('fillText 勾子已安装', hooked === 'hooked', hooked);

    // 触发一次干净的重绘：先灌跨时间的数据把两张 spread 卡片建出来，然后统一重绘
    await cdp.evaluate(sessionId, INJECT_SPREAD_JS);
    await sleep(500);
    await cdp.evaluate(sessionId, RESET_LOG_JS);
    await cdp.evaluate(sessionId, REDRAW_ALL_JS);
    await sleep(700);

    // ── 断言 1：时间戳与数据一一对应 ──
    console.log('');
    console.log('▸ X 轴时间戳');
    const R = await cdp.evaluate(sessionId, REPORT_JS(REAL_TOPIC));
    check('卡片已建出曲线', R.hasCard && R.liveCount >= 1,
      `series=${R.seriesCount} 有数据=${R.liveCount} 画的是 ${R.xKey}`);
    check('**每条**曲线的 ts 都与 data 等长', R.allTsAligned === true,
      R.seriesTsLens.join(' '));
    check('被画的那条曲线有不少于 30 个点', R.bufLen >= 30, `data=${R.bufLen} ts=${R.tsLen}`);
    check('X 轴取的那条曲线 ts 与 data 一致', R.tsLen === R.bufLen,
      `data=${R.bufLen} ts=${R.tsLen}`);
    check('ts 严格非递减', R.tsMonotonic === true);
    check('ts 是真实时间（最后一条在 5 分钟内）',
      R.tsLast !== null && Math.abs(Date.now() - R.tsLast) < 5 * 60 * 1000,
      R.tsLast ? new Date(R.tsLast).toISOString() : 'null');

    // ── 断言 2：Y 轴刻度真的画出来了 ──
    console.log('');
    console.log('▸ Y 轴刻度');
    const yLbl = R.y;
    check('画出了 Y 轴刻度标签', yLbl.length >= 2, JSON.stringify(yLbl.map((l) => l.text)));
    check('Y 刻度是右对齐的（贴在绘图区左缘）', yLbl.every((l) => l.align === 'right'));
    check('Y 刻度落在左侧栏内（x < 40）', yLbl.length > 0 && yLbl.every((l) => l.x < 40),
      `x=${yLbl.map((l) => Math.round(l.x)).join(',')}`);
    check('Y 刻度在竖直方向拉开分布', yLbl.length >= 2 &&
      (Math.max(...yLbl.map((l) => l.y)) - Math.min(...yLbl.map((l) => l.y))) > 5,
      `y=${yLbl.map((l) => Math.round(l.y)).join(',')}`);
    check('Y 刻度都在画布内（0 < y < ch）', yLbl.every((l) => l.y > 0 && l.y < l.ch));

    // 刻度值本身要"好看"：彼此等距
    if (yLbl.length >= 2) {
      const nums = yLbl.map((l) => {
        const t = l.text.replace('k', 'e3').replace('M', 'e6');
        return Number(t);
      });
      const gaps = nums.slice(1).map((v, i) => v - nums[i]).filter((g) => Math.abs(g) > 1e-9);
      const uniform = gaps.length === 0 || gaps.every((g) => Math.abs(g - gaps[0]) < Math.abs(gaps[0]) * 0.02 + 1e-9);
      check('Y 刻度等距（nice ticks 生效）', uniform, `值=${nums.join(',')}`);
    }

    // ── 断言 3：X 轴时间真的画出来了 ──
    console.log('');
    console.log('▸ X 轴时间');
    const xLbl = R.x;
    check('画出了 X 轴时间标签', xLbl.length >= 2, JSON.stringify(xLbl.map((l) => l.text)));
    check('X 标签是 HH:MM(:SS) 格式', xLbl.every((l) => /^\d{2}:\d{2}(:\d{2})?$/.test(l.text)));
    check('X 标签贴在画布底部', xLbl.length > 0 && xLbl.every((l) => l.y > l.ch - 20),
      `y=${xLbl.map((l) => Math.round(l.y)).join(',')} ch=${R.ch}`);
    check('X 标签首左尾右（分别贴绘图区两端）',
      xLbl.length >= 2 &&
      xLbl[0].align === 'left' && xLbl[xLbl.length - 1].align === 'right' &&
      Math.abs(xLbl[0].x - xLbl[xLbl.length - 1].x) > 30,
      `${xLbl[0]?.align}@${Math.round(xLbl[0]?.x)} ~ ${xLbl[xLbl.length - 1]?.align}@${Math.round(xLbl[xLbl.length - 1]?.x)}`);

    // ── 断言 4：跨时间的卡片，首/中/尾时间要真的不同 ──
    console.log('');
    console.log('▸ 时间跨度大的卡片（40 分钟）');
    await cdp.evaluate(sessionId, RESET_LOG_JS);
    await cdp.evaluate(sessionId, REDRAW_ALL_JS);
    await sleep(700);
    const S2 = await cdp.evaluate(sessionId, REPORT_JS(SPREAD_TOPIC));
    check('注入的数据上板成卡片', S2.hasCard, `series=${S2.seriesCount} data=${S2.bufLen}`);
    check('注入卡片 ts 与 data 一致', S2.tsLen === S2.bufLen && S2.bufLen === N_SPREAD,
      `data=${S2.bufLen} ts=${S2.tsLen}`);
    check('注入卡片 ts 跨度约 40 分钟',
      S2.tsFirst !== null && (S2.tsLast - S2.tsFirst) > 30 * 60 * 1000,
      S2.tsFirst ? `${((S2.tsLast - S2.tsFirst) / 60000).toFixed(1)} 分钟` : 'null');
    const spread = S2.x;
    check('跨 40 分钟时 X 轴只标到分钟（不带秒）',
      spread.length >= 2 && spread.every((l) => /^\d{2}:\d{2}$/.test(l.text)),
      JSON.stringify(spread.map((l) => l.text)));
    check('X 轴首尾两个时间确实不同',
      spread.length >= 2 && spread[0].text !== spread[spread.length - 1].text,
      JSON.stringify(spread.map((l) => l.text)));

    // ── 断言 5：放大模式要有更细的刻度 ──
    console.log('');
    console.log('▸ 放大模式（单击卡片）');
    const smallTicks = yLbl.length;
    await cdp.evaluate(sessionId, RESET_LOG_JS);
    await cdp.evaluate(sessionId,
      `(() => { const c = document.querySelector('.card[data-topic=${JSON.stringify(REAL_TOPIC)}]');
                c.click(); return c.className; })()`);
    await sleep(900);
    const BIG = await cdp.evaluate(sessionId, REPORT_JS(REAL_TOPIC));
    check('放大后画布变高（进入 big 模式）', BIG.ch > R.ch, `${R.ch} → ${BIG.ch}`);
    check('放大后进入 big 模式（h >= 90）', BIG.ch >= 90, `ch=${BIG.ch}`);
    const bigY = BIG.y;
    const bigX = BIG.x;
    check('放大后 Y 轴刻度不少于小图', bigY.length >= smallTicks, `小图 ${smallTicks} → 大图 ${bigY.length}`);
    check('放大后 Y 轴刻度更细（>= 3 档）', bigY.length >= 3, JSON.stringify(bigY.map((l) => l.text)));
    check('放大后 X 轴补出中点，共 3 个时间',
      bigX.length >= 3 && bigX.some((l) => l.align === 'center'),
      `${JSON.stringify(bigX.map((l) => l.align + ':' + l.text))} 原始记录 ${BIG.xAllCount} 条`);

    // ── 断言 6：X 轴时间范围可设置（全局默认 + 单卡覆盖）──
    console.log('');
    console.log('▸ X 轴时间范围');
    const T = SPREAD_ITEMS.map((x) => x.ts);
    const LAST = hhmm(T[T.length - 1]);

    // 先确认两张卡片初始状态：一张跟随全局、一张被单卡覆盖成"全部"
    const selExists = await cdp.evaluate(sessionId,
      `(() => { const s = document.getElementById('fWindow'); return s ? [...s.options].map(o => o.value).join(',') : null; })()`);
    check('顶栏有 X 轴范围下拉且带预设', selExists !== null && selExists.includes('0,1,5,15,30,60'),
      selExists === null ? '找不到 #fWindow' : selExists);

    const init = await cdp.evaluate(sessionId, `(() => {
      const M = window.MQTTMonitor;
      const a = M.state.cards.find(c => c.topic === ${JSON.stringify(SPREAD_TOPIC)});
      const b = M.state.cards.find(c => c.topic === ${JSON.stringify(SPREAD_TOPIC2)});
      return { globalWin: M.state.ui.chartWindowMin, aWin: a && a.windowMin, bWin: b && b.windowMin };
    })()`);
    check('跟随全局的卡片 windowMin 为 null', init.aWin === null, `windowMin=${JSON.stringify(init.aWin)}`);
    check('被覆盖的卡片 windowMin 为 0（全部）', init.bWin === 0, `windowMin=${JSON.stringify(init.bWin)}`);

    // ── 把顶栏全局改成「最近 15 分钟」（必须是下拉里真有的预设值，否则 <select> 会被静默置空）──
    const WIN_MIN = 15;
    const optOk = await cdp.evaluate(sessionId,
      `[...document.getElementById('fWindow').options].some(o => o.value === '${WIN_MIN}')`);
    check(`下拉里确实有「${WIN_MIN} 分钟」这个预设`, optOk === true);

    await cdp.evaluate(sessionId, `(() => {
      const s = document.getElementById('fWindow');
      s.value = '${WIN_MIN}';
      s.dispatchEvent(new Event('change', { bubbles: true }));
      return s.value;
    })()`);
    await sleep(600);
    // 改全局时弹的提示：要提醒"缓冲不够长"的卡片 —— 用户最容易懵的点
    const toastTxt = await cdp.evaluate(sessionId, `document.getElementById('toastWrap').textContent`);
    check('改全局后弹出提示，说明有多少张卡片跟随', /跟随/.test(toastTxt || ''), (toastTxt || '').slice(0, 110));
    check('提示里点出“缓冲不够长”的卡片（plc/axis.real 只有 1 秒数据却跟随 15 分钟窗口）',
      /缓冲不够长/.test(toastTxt || ''), (toastTxt || '').slice(0, 110));
    check('提示里点出有卡片已单独设置不受影响',
      /单独设置/.test(toastTxt || ''), (toastTxt || '').slice(0, 110));
    // 顶部下拉只重绘"没被单卡覆盖"的卡片；这里统一重绘一次，好让两张卡片都有新的刻度记录
    await cdp.evaluate(sessionId, RESET_LOG_JS);
    await cdp.evaluate(sessionId, REDRAW_ALL_JS);
    await sleep(700);
    const G = await cdp.evaluate(sessionId, REPORT_JS(SPREAD_TOPIC));
    const G2 = await cdp.evaluate(sessionId, REPORT_JS(SPREAD_TOPIC2));

    // 15 分钟窗口：起点 = 最新点 - 15 分钟，正好落在下标 24 上 → 窗口内 16 个点
    const expectWinFirst = hhmm(T[T.length - 1] - WIN_MIN * 60 * 1000);
    check('全局改成 15 分钟后，跟随的卡片 X 轴首标签 = 窗口起点',
      G.x.length >= 2 && G.x[0].text === expectWinFirst,
      `得到 ${JSON.stringify(G.x.map((l) => l.text))}，期望起点 ${expectWinFirst}`);
    check('跟随的卡片 X 轴尾标签仍是最新数据时间',
      G.x.length >= 2 && G.x[G.x.length - 1].text === LAST,
      `得到 ${G.x.length ? G.x[G.x.length - 1].text : '无'}，期望 ${LAST}`);
    check('窗口生效：实际画出的点数从 40 降到 16',
      G.drawnPoints === 16, `drawnPoints=${G.drawnPoints}，期望 16`);
    check('卡片上记录的生效窗口 = 15 分钟',
      G.drawnWinMin === WIN_MIN && G.globalWindowMin === WIN_MIN,
      `drawnWinMin=${G.drawnWinMin} globalWindowMin=${G.globalWindowMin}`);

    check('单卡覆盖为「全部」的卡片不受全局影响，X 轴仍是完整 40 分钟',
      G2.x.length >= 2 && G2.x[0].text === hhmm(T[0]) && G2.x[G2.x.length - 1].text === LAST,
      `${JSON.stringify(G2.x.map((l) => l.text))} 期望 [${hhmm(T[0])}, ${LAST}]`);
    check('被覆盖的卡片点数仍是 40，生效窗口为 0',
      G2.drawnPoints === N_SPREAD && G2.drawnWinMin === 0,
      `drawnPoints=${G2.drawnPoints} drawnWinMin=${G2.drawnWinMin}`);

    // ── 走真实 UI 路径改单卡：⚙ → 设 X 轴范围 → 保存 ──
    const modalOpened = await cdp.evaluate(sessionId, `(() => {
      const c = document.querySelector('.card[data-topic=${JSON.stringify(SPREAD_TOPIC)}]');
      if (!c) return 'no card';
      c.querySelector('.t-add').click();
      const m = document.getElementById('cardModal');
      return m.classList.contains('show') ? 'open' : 'closed';
    })()`);
    check('点 ⚙ 能打开测点设置弹窗', modalOpened === 'open', modalOpened);

    const noteTxt = await cdp.evaluate(sessionId, `document.getElementById('mWindowNote').textContent`);
    check('弹窗里给出了缓冲覆盖提示', /缓冲.*覆盖/.test(noteTxt || ''), (noteTxt || '').slice(0, 90));
    check('弹窗提示里点明了当前生效窗口是 15 分钟',
      /15 分钟/.test(noteTxt || ''), (noteTxt || '').slice(0, 90));

    await cdp.evaluate(sessionId, `(() => {
      const s = document.getElementById('mWindow');
      s.value = '5';
      s.dispatchEvent(new Event('change', { bubbles: true }));
      return s.value;
    })()`);
    await sleep(300);
    const noteTxt5 = await cdp.evaluate(sessionId, `document.getElementById('mWindowNote').textContent`);
    check('改弹窗下拉后提示立即刷新成 5 分钟（不用等保存）',
      /5 分钟/.test(noteTxt5 || ''), (noteTxt5 || '').slice(0, 90));

    await cdp.evaluate(sessionId, RESET_LOG_JS);
    await cdp.evaluate(sessionId, `document.getElementById('mSave').click()`);
    await sleep(900);
    const C = await cdp.evaluate(sessionId, REPORT_JS(SPREAD_TOPIC));
    const expectFive = hhmm(T[T.length - 1] - 5 * 60 * 1000);
    check('弹窗把单卡设成 5 分钟后，X 轴首标签跟着变',
      C.x.length >= 2 && C.x[0].text === expectFive,
      `${JSON.stringify(C.x.map((l) => l.text))} 期望起点 ${expectFive}`);
    check('窗口 5 分钟时实际画出 6 个点', C.drawnPoints === 6, `drawnPoints=${C.drawnPoints}，期望 6`);
    check('单卡覆盖值写入卡片', C.cardWindowMin === 5, `card.windowMin=${JSON.stringify(C.cardWindowMin)}`);

    // ── 落盘：配置里要真的存下来 ──
    const persisted = await (await fetch(`${BASE}/api/state`)).json();
    const pCards = persisted?.data?.cards || persisted?.cards || [];
    const pUi = persisted?.data?.ui || persisted?.ui || {};
    check('全局 X 轴范围已落盘', Number(pUi.chartWindowMin) === WIN_MIN, `chartWindowMin=${pUi.chartWindowMin}`);
    const pc = pCards.find((c) => c.topic === SPREAD_TOPIC);
    check('单卡 X 轴范围已落盘', pc && Number(pc.windowMin) === 5, `windowMin=${pc && pc.windowMin}`);
    const pc2 = pCards.find((c) => c.topic === SPREAD_TOPIC2);
    check('被覆盖成“全部”的卡片也落盘为 0', pc2 && Number(pc2.windowMin) === 0, `windowMin=${pc2 && pc2.windowMin}`);

    // ── 故意保留这个混合状态去截图：
    //    此时全局 = 15 分钟、spread 单卡覆盖 = 5 分钟、spread2 单卡覆盖 = 0（全部），
    //    三张卡片数据一样但 X 轴范围各不相同 —— 正好把两个层级的设置都拍进一张图。
    //    多等一会儿是为了让 toast 淡出，别挡住卡片。
    await sleep(3200);

    // ── 截图 ──
    console.log('');
    console.log('▸ 截图');
    const shot = await cdp.send('Page.captureScreenshot', { format: 'png' }, sessionId);
    fs.writeFileSync(shotPath, Buffer.from(shot.data, 'base64'));
    check('全景截图已保存', fs.existsSync(shotPath) && fs.statSync(shotPath).size > 5000,
      `${shotPath} (${(fs.statSync(shotPath).size / 1024).toFixed(0)} KB)`);

    // 再来一张卡片区特写（CDP 的 clip + scale，不用装任何图像库就能放大）
    const rect = await cdp.evaluate(sessionId, `(() => {
      const g = document.getElementById('cardGrid');
      if (!g) return null;
      const r = g.getBoundingClientRect();
      const cards = Array.from(g.querySelectorAll('.card')).map(c => c.getBoundingClientRect());
      if (!cards.length) return null;
      const top = Math.min(r.top, ...cards.map(c => c.top));
      const bottom = Math.max(...cards.map(c => c.bottom));
      const left = Math.min(r.left, ...cards.map(c => c.left));
      const right = Math.max(...cards.map(c => c.right), r.left + 320);
      return { x: left, y: top, width: right - left, height: bottom - top };
    })()`);
    if (rect && rect.width > 50 && rect.height > 50) {
      const zoom = await cdp.send('Page.captureScreenshot', {
        format: 'png',
        clip: { x: rect.x, y: rect.y, width: rect.width, height: rect.height, scale: 2 },
      }, sessionId);
      fs.writeFileSync(zoomPath, Buffer.from(zoom.data, 'base64'));
      check('卡片特写截图已保存（2 倍放大）',
        fs.existsSync(zoomPath) && fs.statSync(zoomPath).size > 5000,
        `${zoomPath} (${(fs.statSync(zoomPath).size / 1024).toFixed(0)} KB, ${Math.round(rect.width)}×${Math.round(rect.height)} → ×2)`);
    } else {
      check('卡片特写截图已保存（2 倍放大）', false, '取不到卡片区域');
    }

    // ── 断言 6：没有 JS 异常 ──
    console.log('');
    console.log('▸ 运行时报错');
    const errs = cdp.consoles.filter((c) => c.type === 'error');
    check('无 JS 未捕获异常', cdp.exceptions.length === 0,
      cdp.exceptions.slice(0, 3).join(' | ') || '无');
    check('无 console.error', errs.length === 0, errs.slice(0, 3).map((e) => e.text).join(' | ') || '无');
  } catch (e) {
    console.log('');
    console.log(`  ✗ 执行中断：${e.message}`);
    fail++;
  } finally {
    try { cdp && cdp.close(); } catch { /* ignore */ }
    killTree(edge);
    killTree(app.child);
    await sleep(400);
    fs.rm(tmp, { recursive: true, maxRetries: 2, retryDelay: 200 }, () => {});
  }

  console.log('');
  console.log('────────────────────────────────────────────');
  console.log(`  通过 ${pass} 项，失败 ${fail} 项`);
  if (app.out.length && fail) {
    console.log('  ── 平台日志尾部 ──');
    console.log(app.out.join('').split('\n').slice(-15).map((l) => '  ' + l).join('\n'));
  }
  console.log('────────────────────────────────────────────');
  console.log('');
  process.exitCode = fail ? 1 : 0;
}

unitTests();
main();

'use strict';

/**
 * 「曲线显示不全」修复的端到端验证
 *
 * 复现的正是用户报的问题：
 *   落盘库里某个主题有几百上千条，曲线却只画最后 120 个点（旧版的默认「保留点数」）。
 *
 * 这个脚本造一个"库里数据远多于画布能画"的场景（30000 条 / 跨 10 小时），然后断言：
 *   A. 服务端能把整段数据交出来（均匀抽样，而不是只给最新的一段）
 *   B. 页面启动时进内存的不再是 120 个点，而是整段
 *   C. 用户设的「点数上限」只影响**抽稀**：点数变少，但铺开的时间跨度不变
 *   D. X 轴第一个刻度 = 库里最早那条数据的时间（证明曲线真的从头铺到尾）
 *   E. 老配置里的 points:120 会被迁移成"不限"并落盘
 *
 * 全程用临时目录（MQTT_MONITOR_CONFIG / MQTT_MONITOR_DATA_DIR），不碰正式实例的数据。
 *
 * 用法：node tools/verify-chart-full.js
 * 退出码：0 = 全部通过，1 = 有失败项
 */

const { spawn } = require('child_process');
const fs = require('fs');
const os = require('os');
const path = require('path');
const WebSocket = require('ws');   // CDP 客户端用（Node 自带的 WebSocket 是 addEventListener 风格，不适用）

// node:sqlite 的实验性特性警告静音，输出更干净
const _emitWarning = process.emitWarning;
process.emitWarning = function patched(w) {
  if (String(w).includes('SQLite is an experimental feature')) return;
  return _emitWarning.call(process, w);
};

const ROOT = path.join(__dirname, '..');
const EDGE = 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';
const WEB_PORT = 8094;
const BROKER_PORT = 18834;
const CDP_PORT = 9336;
const BASE = `http://127.0.0.1:${WEB_PORT}`;
const VIEW = { width: 1500, height: 900 };

const TOPIC = 'plc/full.big';
const N_BIG = 30000;              // 库里的条数 —— 远多于旧版能画的 120
const STEP = 1200;                // 每条间隔 1.2 秒 → 整段 10 小时
const T0 = Date.now() - (N_BIG - 1) * STEP;
const LAST_TS = T0 + (N_BIG - 1) * STEP;

const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'mqttmon-full-'));
const cfgPath = path.join(tmp, 'config.json');
const dataDir = path.join(tmp, 'data');
const profileDir = path.join(tmp, 'edgeprofile');
const shotPath = path.join(ROOT, 'tools', '_ui_full.png');

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

/** 时间 → HH:MM，与页面里 fmtAxisTime 在跨度 ≥1 分钟时的行为一致 */
function hhmm(ts) {
  const d = new Date(ts);
  const p = (n) => String(n).padStart(2, '0');
  return `${p(d.getHours())}:${p(d.getMinutes())}`;
}

/** 毫秒 → 小时（用于报错信息里给人看） */
const hrs = (ms) => `${(ms / 3600000).toFixed(2)} h`;

// ============================================================
//  一、造数据：直接往临时库里写 30000 条
// ============================================================
function seedDatabase() {
  const { HistoryStore } = require('../src/historyStore');
  const hs = new HistoryStore({ dir: dataDir, keepDays: 7, maxRows: 2000000 });
  if (!hs.open()) throw new Error(`临时库打不开：${hs.reason}`);
  for (let i = 0; i < N_BIG; i += 1) {
    const v = 500 + Math.round(300 * Math.sin(i / 200));
    hs.record({ topic: TOPIC, ts: T0 + i * STEP, num: v, payload: String(v) });
  }
  hs.flushNow();
  const span = hs.topicSpan(TOPIC);
  hs.close();
  return span;
}

function writeConfig() {
  const cfg = {
    connection: {
      protocol: 'mqtt', host: '127.0.0.1', port: BROKER_PORT, clientId: '',
      username: '', password: '', keepalive: 60, cleanSession: true, rejectUnauthorized: false,
    },
    subscriptions: [{ topic: '#', qos: 0 }],
    broker: {
      enabled: true, port: BROKER_PORT, username: '', password: '',
      autoConnect: true, simEnabled: false, simIntervalMs: 1000,
    },
    // 故意用老配置的写法：points: 120。旧版默认值，迁移后应该变成 0（不限）
    cards: [{
      topic: TOPIC, name: TOPIC, unit: '', fields: [],
      low: null, high: null, points: 120, windowMin: null,
    }],
    history: { enabled: true, keepDays: 7, maxRows: 2000000, dir: '' },
    ui: { maxLogRows: 300, chartPoints: 120, theme: 'light', chartWindowMin: 0, autoGroup: false },
  };
  fs.writeFileSync(cfgPath, JSON.stringify(cfg, null, 2), 'utf8');
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
//  二、CDP 极简客户端
// ============================================================
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
        const r = await send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true }, sessionId);
        if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description || '页面内求值失败');
        return r.result?.value;
      };
      resolve({ send, evaluate, exceptions, consoles, close: () => ws.close() });
    });
  });
}

// ============================================================
//  三、页面探针
// ============================================================
/** 勾 fillText，之后所有画布文字都被记录 —— 用它读 X 轴刻度 */
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
        if (tag === undefined && cv && cv.closest) {
          const host = cv.closest('.card');
          if (host) { tag = host.dataset.topic; cv.__tag = tag; }
        }
        window.__axisLog.push({ tag: tag, text: String(text), x: x, y: y,
          align: this.textAlign, ch: cv ? cv.clientHeight : 0 });
        if (window.__axisLog.length > 6000) window.__axisLog.splice(0, 3000);
      } catch (e) { /* 记录失败不能影响绘制 */ }
      return orig.apply(this, arguments);
    };
  }
  M.state.cards.forEach((c) => { if (c.el && c.el.canvas) c.el.canvas.__tag = c.topic; });
  return 'hooked';
})()`;

const RESET_LOG_JS = `(() => { if (window.__axisLog) window.__axisLog.length = 0; return 1; })()`;

/** 强制重绘，保证读到的刻度是"刚刚画的这一轮" */
const REDRAW_JS = `(() => { window.MQTTMonitor.state.cards.forEach((c) => window.MQTTMonitor.markDirty(c)); return 1; })()`;

/**
 * 读取一张卡片的真实状态。
 * chart 用的是产品自己挂在卡片上的 card._chart —— 不在探针里重实现一遍绘制逻辑，
 * 那样只会验证"我写的探针"，验证不了产品。
 */
const REPORT_JS = (topic) => `(() => {
  const M = window.MQTTMonitor;
  const card = M.state.cards.find(c => c.topic === ${JSON.stringify(topic)});
  const cv = card && card.el && card.el.canvas;
  const log = (window.__axisLog || []).filter(e => e.tag === ${JSON.stringify(topic)});
  const timeRe = /^\\d{2}:\\d{2}(:\\d{2})?$/;
  const ch = cv ? cv.clientHeight : 0;

  // X 轴标签是「左、右、中点」的顺序画的，x 不单调 ——
  // 同一轮的三条共享同一个 y，所以从末尾往前收同 y 的连续记录
  const xAll = log.filter(e => timeRe.test(e.text) && e.y > 0 && e.y > ch - 20);
  let xRound = [];
  if (xAll.length) {
    const ty = xAll[xAll.length - 1].y;
    for (let i = xAll.length - 1; i >= 0; i -= 1) {
      if (xAll[i].y !== ty) break;
      xRound.unshift(xAll[i]);
    }
  }
  const seen = new Set();
  const x = [];
  for (let i = xRound.length - 1; i >= 0; i -= 1) {
    if (seen.has(xRound[i].align)) continue;
    seen.add(xRound[i].align);
    x.push(xRound[i]);
  }
  x.sort((a, b) => a.x - b.x);

  const s0 = card && card.series[0];
  return {
    hasCard: !!card,
    cardPoints: card ? card.points : 'NOCARD',
    winMin: card ? card.windowMin : 'NOCARD',
    bufLen: s0 ? s0.data.length : 0,
    bufFirstTs: s0 && s0.ts ? s0.ts[0] : null,
    bufLastTs: s0 && s0.ts ? s0.ts[s0.ts.length - 1] : null,
    allTsAligned: card ? (card.series || []).every(s => (s.ts || []).length === s.data.length) : false,
    tsMonotonic: s0 && s0.ts ? s0.ts.every((t, i) => i === 0 || t >= s0.ts[i - 1]) : false,
    chart: card && card._chart ? card._chart : null,
    x: x.map(e => e.text),
    note: card && card.el && card.el.note ? card.el.note.textContent : null,
    canvasW: cv ? cv.clientWidth : 0,
  };
})()`;

// ============================================================
//  主流程
// ============================================================
async function main() {
  console.log('');
  console.log('══ 「曲线显示不全」修复验证（库里 30000 条 / 跨 10 小时）══');
  console.log(`   临时目录 ${tmp}`);
  console.log('');

  console.log('▸ 造数据：直接往临时库写 30000 条');
  const seeded = seedDatabase();
  check(`库里已写入 ${N_BIG} 条`, seeded && seeded.count === N_BIG, `count=${seeded && seeded.count}`);
  check('库内跨度约 10 小时', seeded && Math.abs(seeded.lastTs - seeded.firstTs - (N_BIG - 1) * STEP) < 10,
    seeded ? hrs(seeded.lastTs - seeded.firstTs) : '空');
  writeConfig();

  console.log('');
  console.log('▸ 启动临时平台');
  const srv = startPlatform();
  let edge;
  let cdp;
  try {
    await fetchOk(`${BASE}/api/state`);
    check('临时平台已就绪', true, BASE);

    // ────────── 阶段 A：服务端能不能把整段数据交出来 ──────────
    console.log('');
    console.log('▸ 阶段 A  服务端取数：整段均匀抽样 vs 只取最新一段');

    const span = (await (await fetch(`${BASE}/api/history/span?topic=${encodeURIComponent(TOPIC)}`)).json()).data;
    check('span 接口报出库内条数', span?.db?.count === N_BIG, `count=${span?.db?.count}`);
    check('span 接口报出内存缓冲的条数', span?.mem === null || typeof span.mem?.count === 'number',
      `mem=${JSON.stringify(span?.mem)}`);
    check('windowMin=0（全部）时不报"窗口内条数"', span?.dbInWindow === null, `dbInWindow=${JSON.stringify(span?.dbInWindow)}`);

    const span60 = (await (await fetch(
      `${BASE}/api/history/span?topic=${encodeURIComponent(TOPIC)}&windowMin=60`)).json()).data;
    const expectInHour = 3600000 / STEP;      // 3000
    check('最近 1 小时窗口内约 3000 条',
      Math.abs(span60?.dbInWindow - expectInHour) <= 2,
      `dbInWindow=${span60?.dbInWindow}，期望≈${expectInHour}`);

    // 均匀抽样：limit 从"最多多少条"变成"最多多少个点"
    // （首尾两条一定保留，所以可能比 limit 多 1~2 个 —— 数量级对就行）
    const sampled = (await (await fetch(
      `${BASE}/api/history?topic=${encodeURIComponent(TOPIC)}&limit=500&sample=1`)).json()).data;
    check('sample=1 + limit=500 → 约 500 个点', Array.isArray(sampled) && sampled.length >= 500 && sampled.length <= 502,
      `len=${sampled?.length}`);
    check('抽样结果覆盖整段（首条=库里最早那条）',
      Array.isArray(sampled) && Math.abs(sampled[0].ts - T0) <= STEP,
      `first=${sampled?.[0]?.ts} 期望≈${T0}`);
    check('抽样结果保留最新那条（曲线右端=当前值）',
      Array.isArray(sampled) && sampled[sampled.length - 1].ts === LAST_TS,
      `last=${sampled?.[sampled.length - 1]?.ts} 期望=${LAST_TS}`);
    check('抽样后时间跨度仍是 10 小时',
      Array.isArray(sampled) && Math.abs((sampled[sampled.length - 1].ts - sampled[0].ts) - (N_BIG - 1) * STEP) <= STEP * 2,
      sampled?.length ? hrs(sampled[sampled.length - 1].ts - sampled[0].ts) : '空');

    // 对照：不带 sample 就是老行为（只给最新一段）
    const plain = (await (await fetch(
      `${BASE}/api/history?topic=${encodeURIComponent(TOPIC)}&limit=500`)).json()).data;
    check('对照：不带 sample 只给最新一段（跨度很小）',
      Array.isArray(plain) && plain.length === 500 && (plain[499].ts - plain[0].ts) < 1000 * STEP,
      plain?.length ? hrs(plain[plain.length - 1].ts - plain[0].ts) : '空');

    // 指定窗口 + 抽样 → 拿到那一小时里均匀分布的 500 个点
    const hour = (await (await fetch(
      `${BASE}/api/history?topic=${encodeURIComponent(TOPIC)}&limit=500&sample=1&windowMin=60`)).json()).data;
    check('windowMin=60 + sample=1 → 约 500 个点只覆盖那一小时',
      Array.isArray(hour) && hour.length >= 500 && hour.length <= 502
        && Math.abs((hour[hour.length - 1].ts - hour[0].ts) - 3600000) <= STEP * 2,
      hour?.length ? `${hour.length} 点 / ${hrs(hour[hour.length - 1].ts - hour[0].ts)}` : '空');

    const hourFull = (await (await fetch(
      `${BASE}/api/history?topic=${encodeURIComponent(TOPIC)}&limit=5000&sample=1&windowMin=60`)).json()).data;
    check('★ limit 提到 5000 时，这一小时能拿到全部 3001 条（分辨率不被前面的抽样拖累）',
      Array.isArray(hourFull) && hourFull.length === 3001,
      `len=${hourFull?.length}`);

    // ────────── 阶段 B：页面启动时到底进内存多少个点 ──────────
    console.log('');
    console.log('▸ 阶段 B  启动无头 Edge，看页面真的把数据画出来了没有');
    fs.mkdirSync(profileDir, { recursive: true });
    edge = spawn(EDGE, [
      '--headless=new', '--disable-gpu', '--no-sandbox', '--hide-scrollbars', '--no-first-run',
      `--remote-debugging-port=${CDP_PORT}`, `--user-data-dir=${profileDir}`,
      `--window-size=${VIEW.width},${VIEW.height}`, 'about:blank',
    ], { stdio: 'ignore', windowsHide: true });

    const ver = await (await fetchOk(`http://127.0.0.1:${CDP_PORT}/json/version`)).json();
    cdp = await cdpConnect(ver.webSocketDebuggerUrl);
    check('CDP 已连上浏览器', true, ver.Browser);

    const { targetId } = await cdp.send('Target.createTarget', { url: 'about:blank' });
    const { sessionId } = await cdp.send('Target.attachToTarget', { targetId, flatten: true });
    await cdp.send('Page.enable', null, sessionId);
    await cdp.send('Runtime.enable', null, sessionId);
    await cdp.send('Page.navigate', { url: BASE }, sessionId);

    let booted = false;
    for (let i = 0; i < 80; i += 1) {
      await sleep(250);
      if (await cdp.evaluate(sessionId, `!!window.MQTTMonitor`)) { booted = true; break; }
    }
    check('页面已加载（MQTTMonitor 就绪）', booted);
    if (!booted) throw new Error('页面没起来，后面的断言没意义');

    // 等历史回填 + 首屏绘制完成
    let B = null;
    for (let i = 0; i < 40; i += 1) {
      await sleep(300);
      B = await cdp.evaluate(sessionId, REPORT_JS(TOPIC));
      if (B && B.chart && B.chart.points > 0) break;
    }
    check('卡片已建出来', B?.hasCard === true);
    check('卡片 ts 与 data 等长', B?.allTsAligned === true);
    check('卡片 ts 严格非递减', B?.tsMonotonic === true);

    // ★ 核心：进内存的不再是 120 个点
    check(`★ 页面进内存的点数 = ${B?.bufLen}（旧版被截成 120）`,
      B?.bufLen >= 5000 && B?.bufLen <= 5100, `bufLen=${B?.bufLen}`);
    check('★ 缓冲首条 = 库里最早那条（不是"最新 120 条"）',
      B?.bufFirstTs !== null && Math.abs(B.bufFirstTs - T0) <= STEP,
      `first=${B?.bufFirstTs} 期望≈${T0}`);
    check('★ 缓冲末条 = 库里最新那条',
      B?.bufLastTs === LAST_TS, `last=${B?.bufLastTs} 期望=${LAST_TS}`);
    check('★ 曲线铺开的真实时间是 10 小时',
      B?.chart && Math.abs(B.chart.spanMs - (N_BIG - 1) * STEP) <= STEP * 3,
      B?.chart ? hrs(B.chart.spanMs) : '无 _chart');
    check('★ 一次画不下就抽稀（rawPoints > points），不是把前面的丢掉',
      B?.chart && B.chart.rawPoints >= 5000 && B.chart.points < B.chart.rawPoints && B.chart.points >= 600,
      B?.chart ? `points=${B.chart.points} raw=${B.chart.rawPoints} dropped=${B.chart.dropped}` : '无 _chart');

    // X 轴首刻度 = 库里最早那条的时间
    await cdp.evaluate(sessionId, RESET_LOG_JS);
    await cdp.evaluate(sessionId, HOOK_JS);
    await cdp.evaluate(sessionId, REDRAW_JS);
    await sleep(700);
    const Bx = await cdp.evaluate(sessionId, REPORT_JS(TOPIC));
    check('★ X 轴第一个刻度 = 库里最早那条数据的时间',
      Bx?.x?.length >= 2 && Bx.x[0] === hhmm(T0),
      `${JSON.stringify(Bx?.x)} 期望首项 ${hhmm(T0)}`);
    check('X 轴最后一个刻度 = 库里最新那条数据的时间',
      Bx?.x?.length >= 2 && Bx.x[Bx.x.length - 1] === hhmm(LAST_TS),
      `${JSON.stringify(Bx?.x)} 期望末项 ${hhmm(LAST_TS)}`);
    check('卡片右下角有「画了多少点 / 铺开多久」的小字',
      !!Bx?.note && /点/.test(Bx.note), `note=${JSON.stringify(Bx?.note)}`);

    // ────────── 阶段 C：点数上限只影响抽稀，不影响跨度 ──────────
    console.log('');
    console.log('▸ 阶段 C  把「点数上限」设成 50 —— 点数该少，跨度不该变');
    const opened = await cdp.evaluate(sessionId, `(() => {
      const c = document.querySelector('.card[data-topic=${JSON.stringify(TOPIC)}]');
      if (!c) return 'no card';
      c.querySelector('.t-add').click();
      return document.getElementById('cardModal').classList.contains('show') ? 'open' : 'closed';
    })()`);
    check('⚙ 弹窗能打开', opened === 'open', opened);
    check('老配置的「保留点数 120」已显示为不限（输入框为空）',
      (await cdp.evaluate(sessionId, `document.getElementById('mPoints').value`)) === '',
      `value=${JSON.stringify(await cdp.evaluate(sessionId, `document.getElementById('mPoints').value`))}`);

    await sleep(500);
    const noteTxt = await cdp.evaluate(sessionId, `document.getElementById('mWindowNote').textContent`);
    check('弹窗提示里带「缓冲…覆盖」口径', /缓冲.*覆盖/.test(noteTxt || ''), (noteTxt || '').slice(0, 90));
    check('弹窗提示里报出历史库的真实条数 30000', /30000/.test(noteTxt || ''), (noteTxt || '').slice(0, 200));

    await cdp.evaluate(sessionId, `(() => {
      document.getElementById('mPoints').value = '50';
      document.getElementById('mSave').click();
      return 1;
    })()`);
    await sleep(800);
    await cdp.evaluate(sessionId, RESET_LOG_JS);
    await cdp.evaluate(sessionId, REDRAW_JS);
    await sleep(700);
    const C = await cdp.evaluate(sessionId, REPORT_JS(TOPIC));
    check('点数上限 50 生效：正好画 50 个点', C?.chart?.points === 50, `points=${C?.chart?.points}`);
    check('★ 抽稀后时间跨度**不变**（仍是 10 小时）—— 这才是"不显示不全"',
      C?.chart && Math.abs(C.chart.spanMs - (N_BIG - 1) * STEP) <= STEP * 3,
      C?.chart ? hrs(C.chart.spanMs) : '无 _chart');
    check('★ 抽稀后 X 轴首刻度仍是库里最早那条',
      C?.x?.length >= 2 && C.x[0] === hhmm(T0),
      `${JSON.stringify(C?.x)} 期望首项 ${hhmm(T0)}`);
    check('点数上限已落盘为 50',
      Number((await (await fetch(`${BASE}/api/state`)).json()).data?.cards?.[0]?.points) === 50,
      `points=${(await (await fetch(`${BASE}/api/state`)).json()).data?.cards?.[0]?.points}`);

    // ────────── 阶段 D：改 X 轴窗口 → 跨度跟着变 ──────────
    console.log('');
    console.log('▸ 阶段 D  顶栏窗口改成「最近 1 小时」');
    await cdp.evaluate(sessionId, `(() => {
      const s = document.getElementById('fWindow');
      s.value = '60';
      s.dispatchEvent(new Event('change', { bubbles: true }));
      return s.value;
    })()`);
    await sleep(900);
    await cdp.evaluate(sessionId, RESET_LOG_JS);
    await cdp.evaluate(sessionId, REDRAW_JS);
    await sleep(700);
    const D = await cdp.evaluate(sessionId, REPORT_JS(TOPIC));
    check('窗口=60 生效', D?.chart?.winMin === 60, `winMin=${D?.chart?.winMin}`);
    check('窗口内约 3000 点，抽稀到 50', D?.chart?.points === 50 && D.chart.rawPoints > 2500,
      `points=${D?.chart?.points} raw=${D?.chart?.rawPoints}`);
    check('★ 曲线跨度收成 1 小时（数据是够的，不用回补）',
      D?.chart && Math.abs(D.chart.spanMs - 3600000) <= STEP * 3,
      D?.chart ? hrs(D.chart.spanMs) : '无 _chart');

    // 窗口比库里的数据还长 → 应该如实说"库里也只有这么多"
    console.log('');
    console.log('▸ 阶段 E  窗口设成 7 天（比库里 10 小时还长）—— 界面要说实话');
    await cdp.evaluate(sessionId, `document.getElementById('toastWrap').innerHTML = ''`);
    await cdp.evaluate(sessionId, `(() => {
      const s = document.getElementById('fWindow');
      s.value = '10080';
      s.dispatchEvent(new Event('change', { bubbles: true }));
      return s.value;
    })()`);
    await sleep(1500);
    const toastTxt = await cdp.evaluate(sessionId, `document.getElementById('toastWrap').textContent`);
    check('提示里说明库里数据不够长（不假装能显示满）',
      /库里也只有这么多|不够长/.test(toastTxt || ''), (toastTxt || '').slice(0, 140));
    const E = await cdp.evaluate(sessionId, REPORT_JS(TOPIC));
    check('数据不够时跨度就是库里的 10 小时（没被清空、也没谎报 7 天）',
      E?.chart && Math.abs(E.chart.spanMs - (N_BIG - 1) * STEP) <= STEP * 3,
      E?.chart ? hrs(E.chart.spanMs) : '无 _chart');

    // 回到「全部」
    await cdp.evaluate(sessionId, `(() => {
      const s = document.getElementById('fWindow');
      s.value = '0';
      s.dispatchEvent(new Event('change', { bubbles: true }));
      return s.value;
    })()`);
    await sleep(900);
    await cdp.evaluate(sessionId, REDRAW_JS);
    await sleep(600);
    const F = await cdp.evaluate(sessionId, REPORT_JS(TOPIC));
    check('切回「全部」后仍是完整 10 小时', F?.chart && Math.abs(F.chart.spanMs - (N_BIG - 1) * STEP) <= STEP * 3,
      F?.chart ? hrs(F.chart.spanMs) : '无 _chart');

    // ────────── 阶段 F：落盘迁移 ──────────
    console.log('');
    console.log('▸ 阶段 F  老配置 points:120 的迁移');
    const savedCfg = JSON.parse(fs.readFileSync(cfgPath, 'utf8'));
    check('迁移后的 points 已写进 config.json（50，不是 120）',
      Number(savedCfg?.cards?.[0]?.points) === 50, `points=${savedCfg?.cards?.[0]?.points}`);

    // ────────── 阶段 G：刷新页面时"实时点先到、历史后到"的顺序陷阱 ──────────
    // 这是实测踩到的坑：WS 一连上就会回放"最近一条报文"，那条最新的点先落进数组；
    // 紧接着 boot 把历史 append 上去 → ts 变成 [最新, 更早..., 更早...]，
    // 于是窗口裁剪失效、X 轴左端刻度显示成最新时间、「覆盖多久」算成 0 秒。
    console.log('');
    console.log('▸ 阶段 G  制造"实时点先到"的场景，刷新页面看顺序对不对');
    const pub = await fetch(`${BASE}/api/publish`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ topic: TOPIC, payload: '777' }),
    }).then((r) => r.json());
    check('已让设备侧发出一条新报文（会进内存缓冲，WS 连接时会被回放）', pub?.ok === true,
      JSON.stringify(pub).slice(0, 80));
    await sleep(600);

    await cdp.send('Page.navigate', { url: BASE }, sessionId);
    await sleep(1200);
    let G = null;
    for (let i = 0; i < 40; i += 1) {
      await sleep(300);
      G = await cdp.evaluate(sessionId, REPORT_JS(TOPIC));
      if (G && G.chart && G.chart.points > 0) break;
    }
    check('刷新后卡片仍在', G?.hasCard === true);
    check('★ 刷新后 ts 仍严格非递减（历史是归并进去的，不是 append 在实时点后面）',
      G?.tsMonotonic === true);
    check('★ 刷新后首条仍是库里最早那条', G?.bufFirstTs !== null && Math.abs(G.bufFirstTs - T0) <= STEP,
      `first=${G?.bufFirstTs} 期望≈${T0}`);
    check('★ 刷新后跨度仍是 10 小时（没被"最新一条"挤成 0 秒）',
      G?.chart && G.chart.spanMs >= (N_BIG - 1) * STEP - STEP * 3,
      G?.chart ? hrs(G.chart.spanMs) : '无 _chart');
    check('刷新后最后一条是刚发的那条（比库里的最新还新）',
      G?.bufLastTs > LAST_TS, `last=${G?.bufLastTs} > ${LAST_TS}`);

    // 手动造出坏顺序，验证归并修复真的有效（不依赖上面那次刷新的竞态是否刚好发生）
    const repair = await cdp.evaluate(sessionId, `(() => {
      const M = window.MQTTMonitor;
      const c = M.state.cards.find(x => x.topic === ${JSON.stringify(TOPIC)});
      const s = c.series[0];
      const len0 = s.ts.length;
      s.ts.push(s.ts.shift());       // 把最早那条挪到末尾 -> ts 不再递增
      s.data.push(s.data.shift());
      // 破坏点出现在末尾（最早那条被挪到了最后），所以查尾部而不是头部
      const broken = s.ts[s.ts.length - 1] < s.ts[s.ts.length - 2];
      const changed = M.normalizeSeriesTs(c);
      let mono = true;
      for (let i = 1; i < s.ts.length; i += 1) if (s.ts[i] < s.ts[i - 1]) { mono = false; break; }
      M.markDirty(c);
      return { len0, len1: s.ts.length, broken, changed, mono, first: s.ts[0], aligned: s.ts.length === s.data.length };
    })()`);
    check('能造出"ts 不递增"的坏数据（证明检查项有意义）', repair?.broken === true, JSON.stringify(repair));
    check('★ 归并修复被触发', repair?.changed === true);
    check('★ 修复后 ts 恢复递增、data/ts 仍等长', repair?.mono === true && repair?.aligned === true);
    check('★ 修复后首条重新变成库里最早那条', Math.abs((repair?.first ?? 0) - T0) <= STEP,
      `first=${repair?.first} 期望≈${T0}`);

    // ────────── 阶段 H：离线判定的阈值要跟着设备上报节拍走 ──────────
    console.log('');
    console.log('▸ 阶段 H  离线判定：固定 15 秒会把 60~80 秒上报一次的现场 PLC 一直判成"无数据"');
    const staleCase = await cdp.evaluate(sessionId, `(() => {
      const M = window.MQTTMonitor;
      // 造一张干净的假卡片，只喂 ts/data，不碰真实卡片
      const mkCard = (gapMs, lastAgoMs) => {
        const now = Date.now();
        const n = 20;
        const ts = [], data = [];
        for (let i = 0; i < n; i += 1) ts.push(now - lastAgoMs - (n - 1 - i) * gapMs);
        for (let i = 0; i < n; i += 1) data.push(i);
        return { topic: '__stale_probe', series: [{ key: '__v', ts, data }], lastTs: ts[ts.length - 1] };
      };
      const fast = mkCard(2000, 3000);        // 2 秒一个点，最后一条在 3 秒前
      const slow = mkCard(80000, 60000);      // 80 秒一个点，最后一条在 60 秒前 —— 就是现场 PLC 的节奏
      const dead = mkCard(80000, 600000);     // 同样节奏，但已经 10 分钟没来了
      const ultraSlow = mkCard(200000, 60000); // 200 秒一个点 → ×3 = 600 秒，要被上限压到 300 秒
      const lone = { topic: '__stale_lone', series: [{ key: '__v', ts: [], data: [] }], lastTs: 0 };
      // 只有一条报文、而且是 10 分钟前 —— 页面刚打开、历史还没回填完时就是这个样子。
      // 一个间隔都估不出来，绝不能拿 15 秒兜底去判它"掉线"。
      const onePt = {
        topic: '__stale_onepoint',
        series: [{ key: '__v', ts: [Date.now() - 600000], data: [1] }],
        lastTs: Date.now() - 600000,
      };
      return {
        fastIv: M.reportIntervalMs(fast), fastThr: M.staleThreshold(fast), fastStale: M.isStale(fast),
        slowIv: M.reportIntervalMs(slow), slowThr: M.staleThreshold(slow), slowStale: M.isStale(slow),
        deadStale: M.isStale(dead),
        ultraThr: M.staleThreshold(ultraSlow),
        loneStale: M.isStale(lone), loneThr: M.staleThreshold(lone),
        onePtStale: M.isStale(onePt), onePtThr: M.staleThreshold(onePt),
      };
    })()`);

    check('节拍估得出来：2 秒/点', Math.abs((staleCase?.fastIv ?? 0) - 2000) <= 200,
      `iv=${staleCase?.fastIv}ms`);
    check('节拍估得出来：80 秒/点（现场 PLC 的节奏）', Math.abs((staleCase?.slowIv ?? 0) - 80000) <= 5000,
      `iv=${staleCase?.slowIv}ms`);
    check('快发设备：阈值就是下限 15 秒', staleCase?.fastThr === 15000, `thr=${staleCase?.fastThr}`);
    check('★ 慢发设备：阈值放宽到节拍 ×3（80 秒 → 240 秒），不会一直灰着',
      staleCase?.slowThr === 240000, `thr=${staleCase?.slowThr}`);
    check('★ 80 秒节奏、最后一条在 60 秒前 → 不算离线（旧逻辑会判成"无数据"）',
      staleCase?.slowStale === false);
    check('★ 2 秒节奏、最后一条在 3 秒前 → 正常', staleCase?.fastStale === false);
    check('真断了还是判得出来：80 秒节奏但 10 分钟没来 → 离线', staleCase?.deadStale === true);
    check('阈值有上限：200 秒节奏 ×3=600 秒也不会超过 5 分钟', staleCase?.ultraThr === 300000,
      `thr=${staleCase?.ultraThr}`);
    // ⚠️ 这条 2026-10-10 改了语义：以前"估不出节拍"退回 15 秒兜底，会在页面刚打开、
    //    历史还没回填完的那几十秒里把 60 秒节拍的设备误判成"无数据"（线上实测 8 张卡有 4 张在闪）。
    //    现在**判不了就返回 null**，调用方跟着不判离线 —— 15 秒只在下限的意义上用。
    check('★ 估不出节拍（还没有数据点）→ 阈值是 null（判不了），且不判离线',
      staleCase?.loneThr === null && staleCase?.loneStale === false,
      `loneThr=${staleCase?.loneThr} loneStale=${staleCase?.loneStale}`);
    check('★ 只有一个数据点也算"估不出"：绝不拿 15 秒兜底去判一个还不知道节拍的设备',
      staleCase?.onePtThr === null && staleCase?.onePtStale === false,
      `onePtThr=${staleCase?.onePtThr} onePtStale=${staleCase?.onePtStale}`);

    // ────────── 截图 ──────────
    console.log('');
    console.log('▸ 截图');
    // 截图前把点数上限恢复成「不限」，让画面反映默认状态
    await cdp.evaluate(sessionId, `(() => {
      const M = window.MQTTMonitor;
      M.state.cards.forEach((c) => { c.points = 0; M.markDirty(c); });
      M.saveCards();
      return 1;
    })()`);
    await sleep(900);
    const shot = await cdp.send('Page.captureScreenshot', { format: 'png' }, sessionId);
    fs.writeFileSync(shotPath, Buffer.from(shot.data, 'base64'));
    check('全景截图已保存', fs.existsSync(shotPath) && fs.statSync(shotPath).size > 5000,
      `${(fs.statSync(shotPath).size / 1024).toFixed(0)} KB → tools/_ui_full.png`);

    check('无 JS 未捕获异常', cdp.exceptions.length === 0, cdp.exceptions.slice(0, 2).join(' | ') || '无');
    const errs = cdp.consoles.filter((c) => c.type === 'error');
    check('无 console.error', errs.length === 0, errs.slice(0, 2).map((e) => e.text).join(' | ') || '无');
  } finally {
    cdp?.close();
    killTree(edge);
    killTree(srv.child);
    await sleep(600);
    try { fs.rmSync(tmp, { recursive: true, force: true }); }
    catch { /* Edge 还在占目录时忽略，系统会自己清 */ }
  }
}

main()
  .catch((err) => {
    console.error('\n验证脚本异常：', err);
    fail += 1;
  })
  .finally(() => {
    console.log('');
    console.log('════════════════════════════════════════');
    console.log(`  结果：${pass} 项通过，${fail} 项失败`);
    console.log('════════════════════════════════════════');
    console.log('');
    process.exit(fail ? 1 : 0);
  });

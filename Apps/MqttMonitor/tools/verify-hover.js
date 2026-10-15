'use strict';

/**
 * 「鼠标悬停读任意一点的 X / Y + 刻度更密」的前端验证（真实浏览器，无头 Edge + CDP）
 *
 * 为什么要有这个脚本：
 *   悬停取点是纯像素活 —— 十字线画在 canvas 上、提示框是一块 DOM。
 *   单测跑不到，"看着像对"和"真的对"差得很远：
 *     · 提示框里的数字到底是**真实采样值**，还是被插值/四舍五入出来的？
 *     · 多曲线时，几条线取的是不是**同一个时刻**的点？（各算各的最近点会各指一个时间）
 *     · 十字线是不是真的画在覆层上、没去动主曲线的画布？
 *   这里在真实 Edge 里打开页面，用 **Input.dispatchMouseEvent 派发真实鼠标移动**
 *   （不是直接调内部函数），再勾住 canvas 的 arc / lineTo 看它画了什么、画在哪一层。
 *
 * 全程用临时目录，不动正式实例的 config.json / data。
 * 用法：node tools/verify-hover.js
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
const WEB_PORT = 8096;
const BROKER_PORT = 18836;
const CDP_PORT = 9338;
const BASE = `http://127.0.0.1:${WEB_PORT}`;
const VIEW = { width: 1500, height: 900 };

const TOPIC = 'plc/hover.a';        // 单曲线：走真实 broker 链路
const MULTI_TOPIC = 'plc/hover.multi'; // 多测点：同一主题下 3 个 tag，页内注入

const N = 60;                        // 单曲线点数（量程铺得开，好验刻度档数）
const MULTI_TAGS = ['DB1.DBW241', 'DB1.DBW242', 'DB1.DBW243'];

const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'mqttmon-hover-'));
const cfgPath = path.join(tmp, 'config.json');
// 单曲线卡和多曲线卡要各占一张（悬停断言要按卡片区分）：关掉同前缀自动合并，
// 否则 plc/hover.a 和 plc/hover.multi 会被并成一张（见 verify-group.js）。
fs.writeFileSync(cfgPath, JSON.stringify({ ui: { autoGroup: false } }), 'utf8');
const dataDir = path.join(tmp, 'data');
const profileDir = path.join(tmp, 'edgeprofile');
const shotPath = path.join(ROOT, 'tools', '_ui_hover.png');
const shotZoomPath = path.join(ROOT, 'tools', '_ui_hover_zoom.png');

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

/** 设备侧发报文：100 → 1280 线性上升，量程足够宽，Y 轴刻度才看得出分档。
 *  间隔 200ms：整段约 12 秒 —— 够长，X 轴按秒显示时相邻标签不会撞成同一个时刻；
 *  又不到 1 分钟，X 标签保持"带秒"的精度。 */
async function publishFromDevice(topic, n) {
  const client = mqtt.connect(`mqtt://127.0.0.1:${BROKER_PORT}`, {
    clientId: `hover-dev-${Date.now()}-${Math.random().toString(16).slice(2, 6)}`,
    clean: true, reconnectPeriod: 0, connectTimeout: 5000,
  });
  await new Promise((resolve, reject) => {
    const t = setTimeout(() => reject(new Error('连接 broker 超时')), 6000);
    client.once('connect', () => { clearTimeout(t); resolve(); });
    client.once('error', (e) => { clearTimeout(t); reject(e); });
  });
  for (let i = 0; i < n; i++) {
    await new Promise((res) => client.publish(topic, JSON.stringify({
      tag: 'DB1.DBW240', address: 'DB1.DBW240', dataType: 'Int16',
      value: String(100 + i * 20), ts: new Date().toISOString(),
    }), { qos: 0 }, res));
    await sleep(200);
  }
  await sleep(400);
  await new Promise((res) => client.end(false, {}, res));
}

/** 极简 CDP 客户端：按 id 路由回复，其余当事件收 */
function cdpConnect(wsUrl) {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(wsUrl, { maxPayload: 128 * 1024 * 1024 });
    let msgId = 0;
    const pending = new Map();
    const exceptions = [];
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
      resolve({ send, evaluate, exceptions, close: () => ws.close() });
    });
  });
}

/** 杀整个进程树（Edge 会 fork 一堆子进程，只 kill 主进程会卡住 profile 目录） */
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
//  一、纯函数断言：把 nearestIdx / thinTicks 抠出来单跑
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
  const names = ['nearestIdx', 'thinTicks', 'fmtAxisTime', 'fmtTime'];
  const body = `${names.map(grab).join('\n')}\nreturn { ${names.join(', ')} };`;
  return new Function(body)();
}

function unitTests() {
  console.log('▸ 纯函数断言（直接跑 public/app.js 里的实现）');
  let fns;
  try {
    fns = extractPureFns();
  } catch (e) {
    check('能从 app.js 抠出 nearestIdx / thinTicks', false, e.message);
    return;
  }
  check('能从 app.js 抠出 nearestIdx / thinTicks', true);
  const { nearestIdx, thinTicks } = fns;

  const xs = [0, 10, 20, 30, 40];
  check('nearestIdx 命中正中', nearestIdx(xs, 20) === 2, String(nearestIdx(xs, 20)));
  check('nearestIdx 取更近的那边（21→2，26→3）',
    nearestIdx(xs, 21) === 2 && nearestIdx(xs, 26) === 3,
    `${nearestIdx(xs, 21)} / ${nearestIdx(xs, 26)}`);
  check('nearestIdx 正好卡在中点取左边（不来回抖）',
    nearestIdx(xs, 25) === 2, String(nearestIdx(xs, 25)));
  check('nearestIdx 越界夹到端点', nearestIdx(xs, -999) === 0 && nearestIdx(xs, 999) === 4);
  check('nearestIdx 空数组返回 -1', nearestIdx([], 5) === -1);
  check('nearestIdx 单元素数组返回 0', nearestIdx([7], 100) === 0);
  check('nearestIdx 在等间距数组上与朴素遍历一致',
    (() => {
      const big = Array.from({ length: 997 }, (_, i) => i * 0.7);
      for (let t = -5; t < 700; t += 3.3) {
        let want = 0;
        let best = Infinity;
        big.forEach((v, i) => { const d = Math.abs(v - t); if (d < best) { best = d; want = i; } });
        if (nearestIdx(big, t) !== want) return false;
      }
      return true;
    })());

  check('thinTicks 不超过上限', thinTicks([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], 4).length === 4,
    JSON.stringify(thinTicks([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], 4)));
  check('thinTicks 抽稀后仍然等距（不破坏 Y 刻度等距）',
    (() => {
      const t = thinTicks(Array.from({ length: 11 }, (_, i) => i * 5), 4);
      const g = t.slice(1).map((v, i) => v - t[i]);
      return g.length >= 1 && g.every((x) => x === g[0]);
    })(), JSON.stringify(thinTicks(Array.from({ length: 11 }, (_, i) => i * 5), 4)));
  check('thinTicks 对任意长度都保持等距（穷举 2~40 档）',
    (() => {
      for (let m = 2; m <= 40; m += 1) {
        const src = Array.from({ length: m }, (_, i) => i * 3 + 7);
        for (let cap = 2; cap <= 12; cap += 1) {
          const t = thinTicks(src, cap);
          if (t.length > cap) return `m=${m} cap=${cap} 档数超限 ${t.length}`;
          const g = t.slice(1).map((v, i) => v - t[i]);
          if (g.length && !g.every((x) => x === g[0])) return `m=${m} cap=${cap} 不等距 ${JSON.stringify(t)}`;
        }
      }
      return true;
    })() === true, thinTicks(Array.from({ length: 11 }, (_, i) => i * 5), 3).join(','));
  check('thinTicks 本身够少就不动（返回原数组）',
    (() => { const a = [1, 2, 3]; return thinTicks(a, 5) === a; })());
  check('thinTicks 首尾都保留',
    (() => {
      const t = thinTicks([0, 1, 2, 3, 4, 5, 6, 7, 8, 9], 3);
      return t[0] === 0 && t[t.length - 1] === 9;
    })());
  check('thinTicks 上限为 1 或 0 时不返回空数组（刻度不能消失）',
    (() => {
      const a = thinTicks([0, 1, 2, 3], 1);
      const b = thinTicks([0, 1, 2, 3], 0);
      return a.length >= 2 && b.length >= 2;
    })());
}

// ============================================================
//  二、页面内探针
// ============================================================

/** 勾 canvas 的 arc / moveTo / lineTo / fillText，把"画了什么、画在哪一层"记下来 */
const HOOK_JS = `(() => {
  if (window.__hvLog) return 'hooked';
  window.__hvLog = [];
  const P = CanvasRenderingContext2D.prototype;
  const layerOf = (cv) => (cv && cv.className) || '?';
  const push = (rec) => {
    window.__hvLog.push(rec);
    if (window.__hvLog.length > 24000) window.__hvLog.splice(0, 18000);
  };
  const wrap = (name, mk) => {
    const ori = P[name];
    P[name] = function () {
      try { push(mk(this, arguments)); } catch (e) {}
      return ori.apply(this, arguments);
    };
  };
  wrap('arc', (c, a) => ({ k: 'arc', layer: layerOf(c.canvas), x: a[0], y: a[1], r: a[2] }));
  wrap('moveTo', (c, a) => ({ k: 'moveTo', layer: layerOf(c.canvas), x: a[0], y: a[1] }));
  wrap('lineTo', (c, a) => ({ k: 'lineTo', layer: layerOf(c.canvas), x: a[0], y: a[1] }));
  wrap('fillText', (c, a) => ({ k: 'text', layer: layerOf(c.canvas), text: String(a[0]), x: a[1], y: a[2],
    align: c.textAlign, ch: c.canvas ? c.canvas.clientHeight : 0 }));
  window.__hvClear = () => { window.__hvLog.length = 0; };
  return 'hooked';
})()`;

const REDRAW_JS = `(() => {
  const M = window.MQTTMonitor;
  M.state.cards.forEach((c) => M.markDirty(c));
  return M.state.cards.length;
})()`;

/** 同一主题下 3 个 tag 的报文：走"测点模式"，一个主题画 3 条曲线 */
function makeMultiItems(topic) {
  const base = Date.now() - 59 * 1000;
  const items = [];
  for (let i = 0; i < 60; i++) {
    MULTI_TAGS.forEach((tag, k) => {
      const v = 10 + k * 100 + Math.round(500 * Math.sin((i + k * 7) / 9));
      items.push({
        topic,
        ts: base + i * 1000,
        payload: JSON.stringify({ tag, value: String(v), ts: new Date(base + i * 1000).toISOString() }),
        num: v,
        json: { tag, value: String(v), ts: new Date(base + i * 1000).toISOString() },
        binary: false, qos: 0, retain: false, size: 40,
      });
    });
  }
  return items;
}
const MULTI_ITEMS = makeMultiItems(MULTI_TOPIC);
const INJECT_MULTI_JS = `(() => {
  window.MQTTMonitor.ingestMessages(${JSON.stringify(MULTI_ITEMS)});
  const c = window.MQTTMonitor.state.cards.find(x => x.topic === ${JSON.stringify(MULTI_TOPIC)});
  return c ? c.series.length : -1;
})()`;

/** 某张卡片的几何信息 + 画布在视口里的位置（鼠标坐标要从这里算） */
const GEOM_JS = (topic) => `(() => {
  const M = window.MQTTMonitor;
  const c = M.state.cards.find(x => x.topic === ${JSON.stringify(topic)});
  if (!c || !c.el) return null;
  const r = c.el.canvas.getBoundingClientRect();
  return {
    rect: { left: r.left, top: r.top, width: r.width, height: r.height },
    g: c._geometry,
    markers: (c._markers || []).map(m => ({
      key: m.key, label: m.label, color: m.color, n: m.n,
      tsLen: m.ts ? m.ts.length : 0,
      xs: Array.from(m.xs), ys: Array.from(m.ys),
      dataFirst: m.data[0], dataLast: m.data[m.n - 1],
      sameRef: (c.series.find(s => s.key === m.key) || {}).data === m.data,
    })),
    series: (c.series || []).map(s => ({ key: s.key, n: s.data.length, tsLen: s.ts ? s.ts.length : 0 })),
    cardRect: (() => { const b = c.el.root.getBoundingClientRect();
      return { left: b.left, top: b.top, right: b.right, bottom: b.bottom }; })(),
  };
})()`;

/** 悬停结果 + 提示框的 DOM 现状（是不是真的显示、有没有盖在卡片里/被裁掉） */
const HIT_JS = (topic) => `(() => {
  const M = window.MQTTMonitor;
  const c = M.state.cards.find(x => x.topic === ${JSON.stringify(topic)});
  const hit = c ? c._hoverHit : null;
  const tip = document.getElementById('chartTip');
  const cs = tip ? getComputedStyle(tip) : null;
  const tr = tip ? tip.getBoundingClientRect() : null;
  const cvs = c && c.el ? c.el.root.getBoundingClientRect() : null;
  // 提示框里的数字是不是就是 rows 里那些值 —— 直接从 DOM 读回来对。
  // 单条曲线时提示框压成一行（.tip-solo），多曲线才是 .tip-row 列表，两种都要认。
  const domRows = tip ? [...tip.querySelectorAll('.tip-row')].map(el => ({
    name: el.querySelector('.tip-name').textContent,
    val: el.querySelector('.tip-val').textContent,
  })) : [];
  const solo = tip ? tip.querySelector('.tip-solo') : null;
  if (solo) domRows.push({ name: '', val: solo.querySelector('.tip-val').textContent });
  return {
    hasHit: !!hit,
    hit: hit ? {
      anchorKey: hit.anchorKey, x: hit.x, ts: hit.ts,
      rows: hit.rows.map(r => ({ key: r.key, label: r.label, i: r.i, v: r.v, ts: r.ts })),
    } : null,
    // 回查：提示框里的 v 必须就是该曲线 data[i] 本身（不是插值、不是重新算的）
    rowsAreRealSamples: hit ? hit.rows.every(r => {
      const s = c.series.find(x => x.key === r.key);
      return !!s && s.data[r.i] === r.v && (r.ts === null || (s.ts && s.ts[r.i] === r.ts));
    }) : false,
    tip: {
      shown: !!(tip && tip.classList.contains('show')),
      display: cs ? cs.display : null,
      position: cs ? cs.position : null,
      parentIsBody: !!(tip && tip.parentElement === document.body),
      x: tr ? tr.left : null, y: tr ? tr.top : null,
      w: tr ? tr.width : null, h: tr ? tr.height : null,
      right: tr ? tr.right : null, bottom: tr ? tr.bottom : null,
      xText: tip ? (tip.querySelector('.tip-x') || {}).textContent : null,
      rows: domRows,
    },
    view: { w: window.innerWidth, h: window.innerHeight },
    cardRect: cvs ? { left: cvs.left, top: cvs.top, right: cvs.right, bottom: cvs.bottom } : null,
  };
})()`;

/**
 * 从 canvas 绘制记录里数某张卡片的刻度文字。
 * 按 (文字, 位置) 去重：重绘可能在一轮记录里发生多次（数据在更新），
 * 不去重的话"8 档刻度"会变成"16 条记录"，断言就变成在考验时序运气了。
 */
const TICKS_JS = `(() => {
  const log = window.__hvLog || [];
  const isChart = (e) => e.k === 'text' && String(e.layer).indexOf('card-chart') >= 0;
  const timeRe = /^\\d{2}:\\d{2}(:\\d{2})?$/;
  const numRe = /^-?[\\d.]+[kM]?$/;
  const txt = log.filter(isChart);
  const dedup = (arr, keyOf) => {
    const seen = new Set();
    const out = [];
    for (const e of arr) { const k = keyOf(e); if (seen.has(k)) continue; seen.add(k); out.push(e); }
    return out;
  };
  const x = dedup(txt.filter(e => timeRe.test(e.text)), e => e.text + '@' + Math.round(e.y));
  const y = dedup(txt.filter(e => e.align === 'right' && numRe.test(e.text)),
    e => e.text + '@' + Math.round(e.y));
  return {
    xCount: x.length, xTexts: x.map(e => e.text), xAligns: x.map(e => e.align),
    yCount: y.length, yTexts: y.map(e => e.text), yYs: y.map(e => Math.round(e.y)),
    hoverLayerTexts: log.filter(e => e.k === 'text' && String(e.layer).indexOf('card-hover') >= 0).length,
    total: log.length,
  };
})()`;

/** 覆层上画了什么：竖线（必要）+ 单曲线时的横线（可选）+ 每条曲线一个高亮点 */
const OVERLAY_JS = `(() => {
  const log = window.__hvLog || [];
  const on = log.filter(e => String(e.layer).indexOf('card-hover') >= 0);
  const rd = (v) => Math.round(v * 10) / 10;
  return {
    moves: on.filter(e => e.k === 'moveTo').map(e => ({ x: rd(e.x), y: rd(e.y) })),
    lineTo: on.filter(e => e.k === 'lineTo').map(e => ({ x: rd(e.x), y: rd(e.y) })),
    arcs: on.filter(e => e.k === 'arc').map(e => ({ x: rd(e.x), y: rd(e.y), r: e.r })),
    chartArcs: log.filter(e => e.k === 'arc' && String(e.layer).indexOf('card-chart') >= 0).length,
  };
})()`;

// ============================================================
//  三、主流程
// ============================================================
async function main() {
  console.log('');
  console.log('══ 「悬停读任意一点的 X / Y + 刻度更密」前端验证（无头 Edge + CDP）══');
  console.log(`   临时目录 ${tmp}`);
  console.log('');

  unitTests();

  console.log('');
  console.log('▸ 启动临时平台');
  const app = startPlatform();

  let edge;
  let cdp;
  let exitCode = 0;
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
      if (await cdp.evaluate(sessionId, `!!window.MQTTMonitor`)) { booted = true; break; }
    }
    check('页面已加载（MQTTMonitor 就绪）', booted);
    if (!booted) throw new Error('页面没起来，后面的断言没意义');

    // 顺序要紧：**页面先开着，再发设备数据**。
    // 历史回放只回填配置里已存在的卡片，而卡片是收到实时消息才自动上板的。
    console.log('');
    console.log('▸ 设备侧发报文（页面已在线，走真实 broker 链路）');
    await publishFromDevice(TOPIC, N);
    const st = (await (await fetch(`${BASE}/api/history/stats`)).json()).data;
    check(`设备报文已进平台（${N} 条）`, st?.rows >= N, `rows=${st?.rows}`);

    let ready = false;
    let stable = -1;
    for (let i = 0; i < 40; i++) {
      await sleep(300);
      const n = await cdp.evaluate(sessionId,
        `(() => { const c = window.MQTTMonitor.state.cards.find(x => x.topic === ${JSON.stringify(TOPIC)});
                  return c && c.series[0] ? c.series[0].data.length : -1; })()`);
      if (n >= 30) ready = true;
      if (n === stable && n >= N * 0.9) break;   // 点数不再涨了 —— 确保读几何时数据是静止的
      stable = n;
    }
    check('实时链路把曲线推到了页面', ready);
    check(`曲线点数稳定在 ${N} 附近（读数不会读一半变一半）`, stable >= N * 0.9, `data=${stable}`);

    await cdp.evaluate(sessionId, HOOK_JS);
    await cdp.evaluate(sessionId, REDRAW_JS);
    await sleep(600);
    await cdp.evaluate(sessionId, `window.__hvClear()`);

    // ── 断言 1：几何与命中点表 ──
    console.log('');
    console.log('▸ 命中点表（card._markers）');
    let G = await cdp.evaluate(sessionId, GEOM_JS(TOPIC));
    check('卡片已建出几何信息', !!(G && G.g), G ? JSON.stringify(G.g) : 'null');
    check('命中点表与有数据的曲线一一对应',
      G.markers.length === G.series.filter((s) => s.n > 0).length,
      `markers=${G.markers.length} series=${G.series.filter((s) => s.n > 0).length}`);
    const m0 = G.markers[0];
    check('命中点数 = 曲线点数', m0.n === G.series.find((s) => s.key === m0.key).n,
      `${m0.n} vs ${G.series.find((s) => s.key === m0.key).n}`);
    check('命中点 x 从绘图区左缘到右缘',
      Math.abs(m0.xs[0] - G.g.plotX0) < 0.01 && Math.abs(m0.xs[m0.n - 1] - G.g.plotX1) < 0.01,
      `${m0.xs[0].toFixed(1)} → ${m0.xs[m0.n - 1].toFixed(1)}，绘图区 ${G.g.plotX0} → ${G.g.plotX1}`);
    check('命中点 x 单调不减（二分查找的前提）',
      m0.xs.every((v, i) => i === 0 || v >= m0.xs[i - 1]));
    check('命中点 y 落在绘图区内',
      m0.ys.every((v) => v >= G.g.plotY0 - 0.5 && v <= G.g.plotY1 + 0.5),
      `y ∈ [${Math.min(...m0.ys).toFixed(1)}, ${Math.max(...m0.ys).toFixed(1)}]，绘图区 [${G.g.plotY0}, ${G.g.plotY1}]`);
    check('命中点持有曲线的数据引用（取值就是真实采样，不插值）', m0.sameRef === true);

    // ── 断言 2：真的鼠标移动 → 提示框出现且读数正确 ──
    console.log('');
    console.log('▸ 派发真实鼠标移动，读任意一点');
    const midX = G.g.plotX0 + 0.5 * G.g.plotW;
    const midY = (G.g.plotY0 + G.g.plotY1) / 2;
    await cdp.send('Input.dispatchMouseEvent', {
      type: 'mouseMoved', x: G.rect.left + midX, y: G.rect.top + midY, button: 'none', buttons: 0,
    }, sessionId);
    await sleep(220);

    let H = await cdp.evaluate(sessionId, HIT_JS(TOPIC));
    check('悬停后有命中记录', H.hasHit === true);
    check('提示框已显示', H.tip.shown === true, `display=${H.tip.display}`);
    check('提示框挂在 <body> 上（否则会被卡片的 overflow:hidden 裁掉）',
      H.tip.parentIsBody === true);
    check('提示框是 fixed 定位（跟着视口走，不随卡片滚动漂移）',
      H.tip.position === 'fixed', String(H.tip.position));
    check('提示框完全在视口内（没被裁）',
      H.tip.x >= 0 && H.tip.y >= 0 && H.tip.right <= H.view.w && H.tip.bottom <= H.view.h,
      `x=${Math.round(H.tip.x)} y=${Math.round(H.tip.y)} r=${Math.round(H.tip.right)} b=${Math.round(H.tip.bottom)} / ${H.view.w}x${H.view.h}`);
    check('提示框有实际尺寸（不是 0 高度的空壳）', H.tip.w > 40 && H.tip.h >= 14,
      `${Math.round(H.tip.w)}x${Math.round(H.tip.h)}`);
    check('小卡片上提示框比绘图区矮（只压住一小条，不整段盖住曲线）',
      H.tip.h <= G.g.plotH, `提示框 ${Math.round(H.tip.h)}px / 绘图区 ${G.g.plotH}px`);
    check('提示框没压住左边的 Y 刻度栏',
      H.tip.x >= G.rect.left + G.g.plotX0 - 1,
      `提示框左缘 ${Math.round(H.tip.x)}，刻度栏右缘 ${Math.round(G.rect.left + G.g.plotX0)}`);
    check('提示框里的值是真实采样点（data[i] 本身）', H.rowsAreRealSamples === true);

    // 独立复算：用朴素遍历算"离鼠标最近的那个点"，跟产品给的比
    const wantI = (() => {
      let best = 0;
      let bd = Infinity;
      m0.xs.forEach((v, i) => { const d = Math.abs(v - midX); if (d < bd) { bd = d; best = i; } });
      return best;
    })();
    check('十字线落在离鼠标最近的那个数据点上',
      Math.abs(H.hit.x - m0.xs[wantI]) < 0.01,
      `十字线 x=${H.hit.x.toFixed(2)}，期望 x=${m0.xs[wantI].toFixed(2)}（第 ${wantI} 点）`);
    check('命中点比左右邻居都近',
      (wantI === 0 || Math.abs(H.hit.x - midX) <= Math.abs(m0.xs[wantI - 1] - midX))
      && (wantI === m0.n - 1 || Math.abs(H.hit.x - midX) <= Math.abs(m0.xs[wantI + 1] - midX)));

    // Y 值：提示框里那个数字必须等于 value(data[wantI])
    const wantVal = String(100 + wantI * 20);
    check('提示框里的 Y 就是该点的真实值',
      H.tip.rows.length === 1 && H.tip.rows[0].val.startsWith(wantVal),
      `提示框="${H.tip.rows[0] && H.tip.rows[0].val}"，该点真实值=${wantVal}`);
    check('提示框里的 X 是 HH:MM:SS 时间',
      typeof H.tip.xText === 'string' && /^\d{2}:\d{2}:\d{2}$/.test(H.tip.xText), String(H.tip.xText));
    check('提示框的 X 时间戳与该数据点的 ts 一致',
      (() => {
        const d = new Date(H.hit.ts);
        const p = (n) => String(n).padStart(2, '0');
        return H.tip.xText === `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
      })(), `hit.ts=${new Date(H.hit.ts).toISOString()}`);

    // ── 断言 3：十字线画在覆层上，不动主曲线 ──
    console.log('');
    console.log('▸ 十字线画在哪一层');
    const ov = await cdp.evaluate(sessionId, OVERLAY_JS);
    const near = (a, b, tol) => Math.abs(a - b) <= (tol === undefined ? 1.5 : tol);
    // 竖线 = 一条 moveTo(x, plotY0) → lineTo(x, plotY1)；横线 = moveTo(plotX0, y) → lineTo(plotX1, y)。
    // 两条线都记在同一个数组里，所以要先按起点把线段分出来，不能要求"所有 x 都等于十字线"。
    const vMove = ov.moves.find((p) => near(p.y, G.g.plotY0));
    const vLine = ov.lineTo.find((p) => near(p.y, G.g.plotY1));
    const hMove = ov.moves.find((p) => !near(p.y, G.g.plotY0));
    const hLine = ov.lineTo.find((p) => !near(p.y, G.g.plotY1));
    check('覆层上有一根贯穿绘图区的竖线',
      !!vMove && !!vLine && near(vMove.x, vLine.x) && near(vMove.y, G.g.plotY0) && near(vLine.y, G.g.plotY1),
      `moves=${JSON.stringify(ov.moves)} lineTo=${JSON.stringify(ov.lineTo)}`);
    check('竖线正好落在命中点上（和十字线同一横坐标）',
      !!vMove && near(vMove.x, H.hit.x),
      `竖线 x=${vMove ? vMove.x : 'null'}，命中点 x=${H.hit.x.toFixed(2)}`);
    check('覆层上有高亮点（每条曲线一个）', ov.arcs.length === G.markers.length,
      `弧 ${ov.arcs.length} 个 / 曲线 ${G.markers.length} 条`);
    check('高亮点落在十字线附近',
      ov.arcs.every((a) => Math.abs(a.x - H.hit.x) <= 1.5),
      JSON.stringify(ov.arcs));
    check('单曲线时补一条横线（贴绘图区左右缘）',
      !!hMove && !!hLine && near(hMove.x, G.g.plotX0) && near(hLine.x, G.g.plotX1) && near(hMove.y, hLine.y),
      `moves=${JSON.stringify(ov.moves)} lineTo=${JSON.stringify(ov.lineTo)}`);
    check('横线指向的 y 与高亮点同一个高度',
      !!hLine && near(hLine.y, ov.arcs[0].y), `${hLine && hLine.y} vs ${ov.arcs[0].y}`);
    check('覆层不画文字（刻度文字只可能来自主画布）',
      (await cdp.evaluate(sessionId, TICKS_JS)).hoverLayerTexts === 0);

    await cdp.send('Page.captureScreenshot', {}, sessionId).then(async (r) => {
      fs.writeFileSync(shotPath, Buffer.from(r.data, 'base64'));
    });

    // ── 断言 4：换一个位置，读数跟着变 ──
    console.log('');
    console.log('▸ 换位置：读数跟着鼠标走');
    const leftX = G.g.plotX0 + 0.15 * G.g.plotW;
    await cdp.send('Input.dispatchMouseEvent', {
      type: 'mouseMoved', x: G.rect.left + leftX, y: G.rect.top + midY, button: 'none', buttons: 0,
    }, sessionId);
    await sleep(200);
    const H2 = await cdp.evaluate(sessionId, HIT_JS(TOPIC));
    check('十字线左移了', H2.hasHit && H2.hit.x < H.hit.x - 10,
      `${H.hit.x.toFixed(1)} → ${H2.hit ? H2.hit.x.toFixed(1) : 'null'}`);
    check('时间更早（X 跟着变）', H2.hit.ts < H.hit.ts,
      `${H.tip.xText} → ${H2.tip.xText}`);
    check('值更小（Y 跟着变）', H2.tip.rows[0].val !== H.tip.rows[0].val,
      `${H.tip.rows[0].val} → ${H2.tip.rows[0].val}`);
    const wantI2 = (() => {
      let best = 0; let bd = Infinity;
      m0.xs.forEach((v, i) => { const d = Math.abs(v - leftX); if (d < bd) { bd = d; best = i; } });
      return best;
    })();
    check('左移后的点仍是最近点',
      Math.abs(H2.hit.x - m0.xs[wantI2]) < 0.01 && wantI2 < wantI,
      `第 ${wantI2} 点（前一次第 ${wantI} 点）`);

    // ── 断言 5：滑到绘图区外 / 离开画布 → 收掉 ──
    console.log('');
    console.log('▸ 边界：滑出绘图区要收掉');
    // 覆层的绘制记录是累积的，每次要"只看这一次画了什么"都得先把日志清空，
    // 否则会把前几次悬停的残留算成"没收干净"（这个坑我在第一版里就踩了）。
    await cdp.evaluate(sessionId, `window.__hvClear()`);
    await cdp.send('Input.dispatchMouseEvent', {
      type: 'mouseMoved', x: G.rect.left + Math.max(0, G.g.plotX0 - 30), y: G.rect.top + midY,
      button: 'none', buttons: 0,
    }, sessionId);
    await sleep(200);
    const H3 = await cdp.evaluate(sessionId, HIT_JS(TOPIC));
    check('滑到左侧刻度栏上：提示框收起', H3.tip.shown === false);
    check('滑到左侧刻度栏上：命中记录清空', H3.hasHit === false);
    const ov3 = await cdp.evaluate(sessionId, OVERLAY_JS);
    check('滑出后覆层被擦干净（没有残留的十字线）',
      ov3.lineTo.length === 0 && ov3.arcs.length === 0 && ov3.moves.length === 0,
      `moveTo=${ov3.moves.length} lineTo=${ov3.lineTo.length} arc=${ov3.arcs.length}`);

    await cdp.send('Input.dispatchMouseEvent', {
      type: 'mouseMoved', x: G.rect.left + midX, y: G.rect.top + midY, button: 'none', buttons: 0,
    }, sessionId);
    await sleep(150);
    check('移回来又能取到点', (await cdp.evaluate(sessionId, HIT_JS(TOPIC))).tip.shown === true);

    await cdp.send('Input.dispatchMouseEvent', {
      type: 'mouseMoved', x: G.rect.left + midX, y: G.rect.top - 40, button: 'none', buttons: 0,
    }, sessionId);
    await sleep(200);
    const H4 = await cdp.evaluate(sessionId, HIT_JS(TOPIC));
    check('鼠标离开画布：提示框收起（mouseleave）', H4.tip.shown === false);

    // ── 断言 6：刻度更密 ──
    console.log('');
    console.log('▸ 刻度密度');
    await cdp.evaluate(sessionId, `window.__hvClear()`);
    await cdp.evaluate(sessionId, REDRAW_JS);
    await sleep(500);
    const small = await cdp.evaluate(sessionId, TICKS_JS);
    check('小卡片画出了 Y 刻度', small.yCount >= 2, JSON.stringify(small.yTexts));
    check('小卡片 Y 刻度落在绘图区高度内（没叠成一团）',
      new Set(small.yYs).size === small.yCount, JSON.stringify(small.yYs));
    check('小卡片 Y 刻度等距',
      (() => {
        const n = small.yTexts.map((t) => Number(t.replace('k', 'e3').replace('M', 'e6')));
        const g = n.slice(1).map((v, i) => v - n[i]);
        return g.length === 0 || g.every((x) => Math.abs(x - g[0]) < Math.abs(g[0]) * 0.02 + 1e-9);
      })(), JSON.stringify(small.yTexts));
    check('小卡片画出了 X 时间标签', small.xCount >= 2, JSON.stringify(small.xTexts));
    check('小卡片 X 首个左对齐、末个右对齐（贴绘图区两端）',
      small.xAligns[0] === 'left' && small.xAligns[small.xAligns.length - 1] === 'right',
      JSON.stringify(small.xAligns));
    check('小卡片 X 时间标签互不重复',
      new Set(small.xTexts).size === small.xTexts.length, JSON.stringify(small.xTexts));

    await cdp.evaluate(sessionId,
      `(() => { const c = document.querySelector('.card[data-topic=${JSON.stringify(TOPIC)}]');
                c.click(); return c.className; })()`);
    await sleep(900);
    await cdp.evaluate(sessionId, `window.__hvClear()`);
    await cdp.evaluate(sessionId, REDRAW_JS);
    await sleep(500);
    const big = await cdp.evaluate(sessionId, TICKS_JS);
    check('放大后 Y 刻度不少于小卡片', big.yCount >= small.yCount,
      `小 ${small.yCount}（${small.yTexts.join(',')}）→ 大 ${big.yCount}（${big.yTexts.join(',')}）`);
    check('放大后 Y 刻度更密（>= 5 档）', big.yCount >= 5, JSON.stringify(big.yTexts));
    check('放大后 Y 刻度仍然等距',
      (() => {
        const n = big.yTexts.map((t) => Number(t.replace('k', 'e3').replace('M', 'e6')));
        const g = n.slice(1).map((v, i) => v - n[i]);
        return g.length === 0 || g.every((x) => Math.abs(x - g[0]) < Math.abs(g[0]) * 0.02 + 1e-9);
      })(), JSON.stringify(big.yTexts));
    check('放大的 Y 标签彼此不重叠', new Set(big.yYs).size === big.yCount, JSON.stringify(big.yYs));
    check('放大后 X 时间标签更多（>= 4 个）', big.xCount >= 4,
      `${small.xCount} → ${big.xCount}：${JSON.stringify(big.xTexts)}`);
    check('放大后 X 时间标签仍互不重复',
      new Set(big.xTexts).size === big.xTexts.length, JSON.stringify(big.xTexts));
    check('放大后 X 首左尾右', big.xAligns[0] === 'left' && big.xAligns[big.xAligns.length - 1] === 'right',
      JSON.stringify(big.xAligns));

    // 放大态下再悬停一次：几何变了，命中点表也得跟着更新
    const G2 = await cdp.evaluate(sessionId, GEOM_JS(TOPIC));
    check('放大后命中点表跟着重算（x 铺到新的绘图区宽度）',
      Math.abs(G2.markers[0].xs[G2.markers[0].n - 1] - G2.g.plotX1) < 0.01
      && G2.g.plotW > G.g.plotW,
      `plotW ${G.g.plotW.toFixed(0)} → ${G2.g.plotW.toFixed(0)}`);
    await cdp.send('Input.dispatchMouseEvent', {
      type: 'mouseMoved',
      x: G2.rect.left + G2.g.plotX0 + 0.3 * G2.g.plotW,
      y: G2.rect.top + (G2.g.plotY0 + G2.g.plotY1) / 2,
      button: 'none', buttons: 0,
    }, sessionId);
    await sleep(250);
    const H5 = await cdp.evaluate(sessionId, HIT_JS(TOPIC));
    check('放大态下悬停仍能取到点', H5.tip.shown === true && H5.rowsAreRealSamples === true,
      H5.tip.xText);
    await cdp.send('Page.captureScreenshot', {}, sessionId).then(async (r) => {
      fs.writeFileSync(shotZoomPath, Buffer.from(r.data, 'base64'));
    });
    // 收起放大，别影响后面
    await cdp.evaluate(sessionId,
      `(() => { const c = document.querySelector('.card[data-topic=${JSON.stringify(TOPIC)}]');
                if (c.classList.contains('card-large')) c.click(); return 1; })()`);
    await sleep(700);

    // ── 断言 7：多测点卡片 —— 十字线是一根，读数是同一时刻 ──
    console.log('');
    console.log('▸ 多测点卡片（同一主题 3 个 tag）');
    await cdp.evaluate(sessionId, INJECT_MULTI_JS);
    let nSeries = -1;
    for (let i = 0; i < 12; i += 1) {
      await sleep(250);
      nSeries = await cdp.evaluate(sessionId,
        `(() => { const c = window.MQTTMonitor.state.cards.find(x => x.topic === ${JSON.stringify(MULTI_TOPIC)});
                  return c ? c.series.length : -1; })()`);
      if (nSeries === 3) break;
    }
    check('同一主题下建出 3 条曲线', nSeries === 3, `series=${nSeries}`);
    let GM = await cdp.evaluate(sessionId, GEOM_JS(MULTI_TOPIC));
    if (!GM) {
      check('多测点卡片有几何信息', false);
    } else {
      check('命中点表有 3 条', GM.markers.length === 3, `markers=${GM.markers.length}`);
      check('3 条都有时间戳（能按时刻对齐）',
        GM.markers.every((m) => m.tsLen === m.n), GM.markers.map((m) => `${m.key}:${m.tsLen}/${m.n}`).join(' '));
      const mX = GM.g.plotX0 + 0.62 * GM.g.plotW;
      const mY = (GM.g.plotY0 + GM.g.plotY1) / 2;
      await cdp.evaluate(sessionId, `window.__hvClear()`);   // 只看这一次移动画了什么
      await cdp.send('Input.dispatchMouseEvent', {
        type: 'mouseMoved', x: GM.rect.left + mX, y: GM.rect.top + mY, button: 'none', buttons: 0,
      }, sessionId);
      await sleep(250);
      const HM = await cdp.evaluate(sessionId, HIT_JS(MULTI_TOPIC));
      check('多曲线悬停也有命中', HM.hasHit === true);
      check('提示框列出 3 条曲线', HM.tip.rows.length === 3,
        JSON.stringify(HM.tip.rows.map((r) => `${r.name}=${r.val}`)));
      check('提示框里列出的是 3 个测点名',
        JSON.stringify(HM.tip.rows.map((r) => r.name).sort()) === JSON.stringify([...MULTI_TAGS].sort()),
        JSON.stringify(HM.tip.rows.map((r) => r.name)));
      check('3 行读数对应 3 条不同的曲线（不是同一条画三遍）',
        new Set(HM.hit.rows.map((r) => r.key)).size === 3,
        JSON.stringify(HM.hit.rows.map((r) => r.key)));
      check('多曲线时各点取值仍是真实采样', HM.rowsAreRealSamples === true);
      const tsSet = new Set(HM.hit.rows.map((r) => r.ts).filter((t) => t !== null));
      check('★ 3 条曲线取到的是同一时刻的点（竖线只有一根）', tsSet.size === 1,
        `不同时间戳数=${tsSet.size}：${[...tsSet].map((t) => new Date(t).toISOString()).join(' / ')}`);
      check('提示框里的 3 行值跟 _hoverHit 一致',
        HM.tip.rows.every((r, i) => r.val.startsWith(String(HM.hit.rows[i].v))),
        JSON.stringify(HM.tip.rows.map((r) => r.val)));
      const ovM = await cdp.evaluate(sessionId, OVERLAY_JS);
      check('覆层上有 3 个高亮点（每条曲线一个）', ovM.arcs.length === 3, JSON.stringify(ovM.arcs));
      check('多曲线时不画横线（各自量程不同，横线会指向错误刻度）',
        ovM.moves.length === 1 && ovM.lineTo.length === 1,
        `moveTo=${ovM.moves.length} lineTo=${ovM.lineTo.length}`);
      const tz = await cdp.evaluate(sessionId, TICKS_JS);
      check('多曲线悬停也没往覆层写文字', tz.hoverLayerTexts === 0);
      check('提示框宽度没超出视口', HM.tip.right <= HM.view.w && HM.tip.x >= 0,
        `x=${Math.round(HM.tip.x)} right=${Math.round(HM.tip.right)} / ${HM.view.w}`);
    }

    // ── 断言 8：看板滚动时收掉十字线（提示框是 fixed，不跟着滚会指错） ──
    console.log('');
    console.log('▸ 滚动看板');
    if (GM) {
      const mX = GM.g.plotX0 + 0.4 * GM.g.plotW;
      await cdp.send('Input.dispatchMouseEvent', {
        type: 'mouseMoved', x: GM.rect.left + mX, y: GM.rect.top + (GM.g.plotY0 + GM.g.plotY1) / 2,
        button: 'none', buttons: 0,
      }, sessionId);
      await sleep(220);
      check('滚动前提示框是显示的', (await cdp.evaluate(sessionId, HIT_JS(MULTI_TOPIC))).tip.shown === true);
      const sc = await cdp.evaluate(sessionId, `(() => {
        const g = document.getElementById('cardGrid');
        const before = g.scrollTop;
        g.scrollTop = before + 80;
        if (g.scrollTop === before) { g.dispatchEvent(new Event('scroll')); return { moved: false }; }
        return { moved: true };
      })()`);
      await sleep(280);
      const HS = await cdp.evaluate(sessionId, HIT_JS(MULTI_TOPIC));
      check('滚动后提示框收起（不会停在原地指错点）', HS.tip.shown === false,
        sc.moved ? '真实滚动' : '看板没超出一屏，改用派发 scroll 事件');
      await cdp.evaluate(sessionId, `document.getElementById('cardGrid').scrollTop = 0;`);
      await sleep(300);
    }

    // ── 断言 9：没有运行时异常 ──
    console.log('');
    console.log('▸ 运行时异常');
    const exc = cdp.exceptions.filter((e) => !/ResizeObserver loop/.test(e));
    check('页面没有抛未捕获异常', exc.length === 0, exc.slice(0, 3).join(' | '));
  } catch (err) {
    fail++;
    exitCode = 1;
    console.log('');
    console.log(`  FAIL  脚本执行中断：${err.message}`);
    if (process.env.HOVER_VERBOSE) console.log(err.stack);
  } finally {
    try { cdp && cdp.close(); } catch { /* ignore */ }
    killTree(edge);
    await sleep(400);
    killTree(app.child);
  }

  console.log('');
  console.log('──────────────────────────────────────────');
  console.log(`  通过 ${pass} / 失败 ${fail}`);
  console.log(`  截图 ${shotPath}`);
  console.log(`       ${shotZoomPath}`);
  console.log('──────────────────────────────────────────');
  if (fail > 0) exitCode = 1;
  process.exit(exitCode);
}

main();

'use strict';

/**
 * 「一张卡片显示一个主题的多个测点、并可勾选画哪些」的端到端验证
 *
 * 需求原文：一个卡片可显示一个主题的多个消息数据，并可以选择哪些绘制曲线。
 *
 * 现场形态（这个脚本照抄的形态）：网关把一台设备的点位全发到**同一个主题**，
 * 靠报文里的 tag / address 区分 ——
 *   {"tag":"小车状态","address":"DB1.DBW230","dataType":"Int16","value":"100",…}
 *   {"tag":"设定位置","address":"DB1.DBW250","dataType":"Int16","value":"200",…}
 *   {"tag":"电池电量","address":"DB1.DBW290","dataType":"Int16","value":"300",…}
 * 应该在同一张卡片上画成 3 条曲线，并且能只勾其中 2 条。
 *
 * 断言分四段：
 *   A. 自动模式：3 个测点 = 3 条曲线，且**互不串数据**（每个测点只收自己那条报文的值）
 *   B. 勾选：只勾 2 个 → 只画 2 条；全不勾 → 回到自动；勾选落盘
 *   C. 测点字段覆盖：改成按 address 分组 → 测点名跟着变
 *   D. 上限与提示：8 个测点时只画 6 条，图例显示 "+2"，勾选列表里 8 个都能选到
 *   外加上限 6 之外，刷新页面后历史回填也要按测点分组
 *
 * 全程临时目录（MQTT_MONITOR_CONFIG / MQTT_MONITOR_DATA_DIR），不碰正式实例。
 *
 * 用法：node tools/verify-items.js
 * 退出码：0 = 全部通过，1 = 有失败项
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
const WEB_PORT = 8095;
const BROKER_PORT = 18835;
const CDP_PORT = 9337;
const BASE = `http://127.0.0.1:${WEB_PORT}`;
const VIEW = { width: 1500, height: 900 };

const TOPIC = 'plc/multi';        // 一个主题，多个测点
const TOPIC_MANY = 'plc/many';    // 用来验"超过上限"
const N_ROUND = 12;               // 每个测点发 12 条
const ROUND_GAP = 900;            // 每轮间隔 0.9 秒

// 三个测点：值域分开（100+ / 200+ / 300+），这样"串没串数据"一眼可验
const POINTS = [
  { tag: '小车状态', address: 'DB1.DBW230', base: 100 },
  { tag: '设定位置', address: 'DB1.DBW250', base: 200 },
  { tag: '电池电量', address: 'DB1.DBW290', base: 300 },
];
const MANY_TAGS = Array.from({ length: 8 }, (_, i) => `测点${i + 1}`);

const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'mqttmon-items-'));
const cfgPath = path.join(tmp, 'config.json');
const dataDir = path.join(tmp, 'data');
const profileDir = path.join(tmp, 'edgeprofile');
const shotPath = path.join(ROOT, 'tools', '_ui_items.png');

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
    try { const r = await fetch(url); if (r.ok) return r; } catch { /* 还没起来 */ }
    await sleep(300);
  }
  throw new Error(`等不到 ${url}`);
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
    cards: [
      { topic: TOPIC, name: TOPIC, unit: '', fields: [], items: [], groupBy: '', low: null, high: null, points: 0, windowMin: 0 },
      { topic: TOPIC_MANY, name: TOPIC_MANY, unit: '', fields: [], items: [], groupBy: '', low: null, high: null, points: 0, windowMin: 0 },
    ],
    history: { enabled: true, keepDays: 7, maxRows: 2000000, dir: '' },
    // autoGroup: false —— 本脚本验的是"一个主题一张卡"，两张卡必须各占一张
    // （同前缀自动合并会把它俩并起来，那个行为由 verify-group.js 专测）
    ui: { maxLogRows: 300, theme: 'light', chartWindowMin: 0, autoGroup: false },
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
//  设备侧：一个主题，多个测点轮流上报
// ============================================================
async function connectDevice() {
  const client = mqtt.connect(`mqtt://127.0.0.1:${BROKER_PORT}`, {
    clientId: `items-dev-${Date.now()}-${Math.random().toString(16).slice(2, 6)}`,
    clean: true, reconnectPeriod: 0, connectTimeout: 5000,
  });
  await new Promise((resolve, reject) => {
    const t = setTimeout(() => reject(new Error('连接 broker 超时')), 6000);
    client.once('connect', () => { clearTimeout(t); resolve(); });
    client.once('error', (e) => { clearTimeout(t); reject(e); });
  });
  return client;
}

const publish = (client, topic, payload) => new Promise((res) =>
  client.publish(topic, JSON.stringify(payload), { qos: 0 }, res));

/** 一轮：3 个测点各发一条，值域分开 */
async function publishRounds(client, rounds) {
  for (let i = 0; i < rounds; i++) {
    for (const p of POINTS) {
      await publish(client, TOPIC, {
        tag: p.tag, address: p.address, description: p.tag,
        dataType: 'Int16', value: String(p.base + i), ts: new Date().toISOString(),
      });
    }
    await sleep(ROUND_GAP);
  }
  await sleep(300);
}

/** 8 个测点，每个发 2 条 */
async function publishMany(client) {
  for (let r = 0; r < 2; r++) {
    for (let i = 0; i < MANY_TAGS.length; i++) {
      await publish(client, TOPIC_MANY, {
        tag: MANY_TAGS[i], address: `DB1.DBW${400 + i * 2}`, description: MANY_TAGS[i],
        dataType: 'Int16', value: String(i * 10 + r), ts: new Date().toISOString(),
      });
    }
    await sleep(700);
  }
  await sleep(300);
}

// ============================================================
//  页面探针
// ============================================================
/**
 * 读一张卡片的"测点 → 曲线"现状。
 * 刻意用产品自己挂的 card.series / card.seenItems，而不是探针另算一套 ——
 * 探针自己实现一遍的话，两边一起错也看不出来。
 */
const CARD_JS = (topic) => `(() => {
  const M = window.MQTTMonitor;
  const c = M.state.cards.find(x => x.topic === ${JSON.stringify(topic)});
  if (!c) return null;
  return {
    topic: c.topic,
    items: (c.items || []).slice(),
    groupBy: c.groupBy || '',
    seenIdField: c.seenIdField || '',
    seenItems: [...(c.seenItems || new Map()).keys()],
    overflow: c.overflow || 0,
    value: c.text,
    chart: c._chart ? { points: c._chart.points, raw: c._chart.rawPoints, series: c._chart.series } : null,
    series: c.series.map(s => ({
      key: s.key, label: s.label, color: s.color,
      n: s.data.length, tsN: s.ts ? s.ts.length : -1,
      min: s.data.length ? Math.min(...s.data) : null,
      max: s.data.length ? Math.max(...s.data) : null,
    })),
    legend: c.el && c.el.legend ? c.el.legend.textContent.trim() : '',
    note: c.el && c.el.note ? c.el.note.textContent : '',
  };
})()`;

const openModalJs = (topic) => `(() => {
  const M = window.MQTTMonitor;
  const c = M.state.cards.find(x => x.topic === ${JSON.stringify(topic)});
  if (!c || !c.el) return false;
  M.markDirty(c);
  c.el.root.querySelector('.t-add').click();      // 走真实的齿轮按钮，别直接调 openCardModal
  return document.getElementById('cardModal').classList.contains('show');
})()`;

/** 弹窗里勾选列表的现状：分组标题 + 每行的 value / 文案 / 是否勾上 */
const PICKER_JS = `(() => {
  const box = document.getElementById('mFieldPick');
  const groups = [...box.querySelectorAll('.fp-group')].map(g => g.textContent.trim());
  const rows = [...box.querySelectorAll('.fp-item')].map(r => ({
    value: r.querySelector('input').value,
    name: r.querySelector('.fp-name').textContent,
    text: r.querySelector('.fp-val').textContent,
    checked: r.querySelector('input').checked,
    isItem: r.classList.contains('is-item'),
    color: r.querySelector('.fp-dot').style.background,
  }));
  return { groups, rows, hint: document.getElementById('mFieldHint').textContent };
})()`;

/** 按 value 勾/取消勾，然后保存弹窗 */
const pickJs = (values) => `(() => {
  const box = document.getElementById('mFieldPick');
  const want = ${JSON.stringify(values)};
  for (const r of box.querySelectorAll('.fp-item')) {
    const cb = r.querySelector('input');
    const on = want.includes(cb.value);
    if (cb.checked !== on) { cb.checked = on; cb.dispatchEvent(new Event('change', { bubbles: true })); }
  }
  document.getElementById('mSave').click();
  return [...box.querySelectorAll('input:checked')].map(n => n.value);
})()`;

// ============================================================
//  主流程
// ============================================================
(async () => {
  console.log('');
  console.log('══ 「一个卡片多个测点 + 勾选画哪些」端到端验证 ══');
  console.log(`   临时目录 ${tmp}`);
  console.log(`   主题 ${TOPIC}：${POINTS.map((p) => p.tag).join(' / ')}（同一个主题，靠 tag 区分）`);
  console.log('');

  writeConfig();
  let srv = startPlatform();
  let edge = null;
  let cdp = null;

  try {
    await fetchOk(`${BASE}/api/state`);
    console.log('▸ 阶段 A  自动模式：一个主题 3 个测点，应该画 3 条曲线且互不串数据');
    const dev = await connectDevice();
    await publishRounds(dev, N_ROUND);
    await publishMany(dev);

    let target = null;
    edge = spawn(EDGE, [
      '--headless=new', `--remote-debugging-port=${CDP_PORT}`, `--user-data-dir=${profileDir}`,
      '--no-first-run', '--no-default-browser-check', '--disable-gpu',
      `--window-size=${VIEW.width},${VIEW.height}`, 'about:blank',
    ], { stdio: 'ignore' });

    for (let i = 0; i < 40 && !target; i++) {
      await sleep(400);
      try {
        const list = await fetch(`http://127.0.0.1:${CDP_PORT}/json/list`).then((r) => r.json());
        target = list.find((t) => t.type === 'page');
      } catch { /* 还没起来 */ }
    }
    if (!target) throw new Error('找不到 CDP 页面目标');
    cdp = await cdpConnect(target.webSocketDebuggerUrl);
    const { sessionId } = await cdp.send('Target.attachToTarget', { targetId: target.id, flatten: true });
    await cdp.send('Page.enable', {}, sessionId);
    await cdp.send('Runtime.enable', {}, sessionId);
    await cdp.send('Emulation.setDeviceMetricsOverride',
      { width: VIEW.width, height: VIEW.height, deviceScaleFactor: 1, mobile: false }, sessionId);
    await cdp.send('Page.navigate', { url: `${BASE}/` }, sessionId);
    for (let i = 0; i < 40; i++) {
      await sleep(400);
      const ok = await cdp.evaluate(sessionId, '!!(window.MQTTMonitor && window.MQTTMonitor.state.cards.length)')
        .catch(() => false);
      if (ok) break;
    }
    await sleep(1500);

    const A = await cdp.evaluate(sessionId, CARD_JS(TOPIC));
    check('卡片已建出来', !!A, A ? `series=${A.series.length}` : 'null');
    check('★ 一个主题的 3 个测点 = 3 条曲线', A?.series.length === 3,
      (A?.series || []).map((s) => s.label).join(' / '));
    check('★ 曲线 key 带测点前缀、label 是测点名',
      (A?.series || []).every((s) => s.key.startsWith('__item__:') && s.key === `__item__:${s.label}`),
      (A?.series || []).map((s) => s.key).join(' , '));
    check('用来自动识别的字段是 tag', A?.seenIdField === 'tag', A?.seenIdField);
    check('三个测点都被记下来了（勾选列表要列全）',
      (A?.seenItems || []).length === 3 && POINTS.every((p) => A.seenItems.includes(p.tag)),
      (A?.seenItems || []).join(' , '));
    check('没有多余的"未显示"曲线', A?.overflow === 0, `overflow=${A?.overflow}`);

    // 互不串数据：每个测点的曲线只含自己那一档的值
    const band = (label) => {
      const p = POINTS.find((x) => x.tag === label);
      const s = (A?.series || []).find((x) => x.label === label);
      if (!p || !s || s.n === 0) return false;
      return s.min >= p.base && s.max < p.base + N_ROUND;
    };
    check('★ 小车状态的曲线只含 100~111', band('小车状态'),
      JSON.stringify((A?.series || []).find((s) => s.label === '小车状态')));
    check('★ 设定位置的曲线只含 200~211（没串到小车状态的值）', band('设定位置'),
      JSON.stringify((A?.series || []).find((s) => s.label === '设定位置')));
    check('★ 电池电量的曲线只含 300~311', band('电池电量'),
      JSON.stringify((A?.series || []).find((s) => s.label === '电池电量')));
    check('每条曲线都攒到了 10 条以上（约等于发的轮数）',
      (A?.series || []).every((s) => s.n >= 10),
      (A?.series || []).map((s) => `${s.label}=${s.n}`).join(' '));
    check('每条曲线的 ts 与 data 等长', (A?.series || []).every((s) => s.tsN === s.n),
      (A?.series || []).map((s) => s.tsN).join(','));
    check('图例里三条曲线都列了名字', POINTS.every((p) => (A?.legend || '').includes(p.tag)),
      A?.legend);
    check('卡片右下角小字报出「3 条曲线」', /3 条曲线/.test(A?.note || ''), A?.note);

    console.log('');
    console.log('▸ 阶段 B  勾选：只画勾上的那几个');
    const opened = await cdp.evaluate(sessionId, openModalJs(TOPIC));
    await sleep(400);
    check('测点设置弹窗能打开', opened === true);

    const P0 = await cdp.evaluate(sessionId, PICKER_JS);
    const itemRows = (P0?.rows || []).filter((r) => r.isItem);
    check('★ 勾选列表里「测点」组有 3 行', itemRows.length === 3,
      itemRows.map((r) => r.name).join(' , '));
    check('自动模式下默认一个都不勾', itemRows.every((r) => !r.checked));
    check('列表里有「测点」这一组', (P0?.groups || []).some((g) => g.includes('测点')),
      (P0?.groups || []).join(' | '));
    check('每行都带当前值', itemRows.every((r) => r.text && r.text !== '—'),
      itemRows.map((r) => `${r.name}=${r.text}`).join(' '));
    check('提示里说明当前是自动、最多几条', /自动/.test(P0?.hint || ''), P0?.hint);

    // 只勾两个
    const picked = await cdp.evaluate(sessionId,
      pickJs([`__item__:小车状态`, `__item__:电池电量`]));
    check('勾选后保存，两个都记上了', (picked || []).length === 2, (picked || []).join(' , '));
    await sleep(900);

    const B = await cdp.evaluate(sessionId, CARD_JS(TOPIC));
    check('★ 只画勾上的 2 条曲线（设定位置被去掉了）', B?.series.length === 2,
      (B?.series || []).map((s) => s.label).join(' , '));
    check('★ 保留下来的正好是勾的那两个',
      (B?.series || []).map((s) => s.label).sort().join(',') === ['小车状态', '电池电量'].sort().join(','),
      (B?.series || []).map((s) => s.label).join(','));
    check('★ 勾选的曲线历史数据没丢（仍是 10 条以上）',
      (B?.series || []).every((s) => s.n >= 10), (B?.series || []).map((s) => s.n).join(','));
    check('落盘的 items 写进了 config.json',
      (JSON.parse(fs.readFileSync(cfgPath, 'utf8')).cards.find((c) => c.topic === TOPIC)?.items || []).length === 2,
      JSON.stringify(JSON.parse(fs.readFileSync(cfgPath, 'utf8')).cards.find((c) => c.topic === TOPIC)?.items));

    // 再点开一次，勾选状态要还在
    await cdp.evaluate(sessionId, openModalJs(TOPIC));
    await sleep(400);
    const P1 = await cdp.evaluate(sessionId, PICKER_JS);
    const onNames = (P1?.rows || []).filter((r) => r.checked).map((r) => r.name).sort();
    check('重新打开弹窗时勾选状态还在',
      onNames.join(',') === ['小车状态', '电池电量'].sort().join(','), onNames.join(','));
    await cdp.evaluate(sessionId, `document.getElementById('mCancel').click(); 1`);
    await sleep(300);

    console.log('');
    console.log('▸ 阶段 C  全部取消 → 回到自动');
    await cdp.evaluate(sessionId, openModalJs(TOPIC));
    await sleep(300);
    await cdp.evaluate(sessionId, pickJs([]));
    await sleep(1200);
    const C = await cdp.evaluate(sessionId, CARD_JS(TOPIC));
    check('★ 一个都不勾 = 自动，3 个测点又都回来了', C?.series.length === 3,
      (C?.series || []).map((s) => s.label).join(' , '));
    check('自动模式下 items 已清空', (C?.items || []).length === 0, JSON.stringify(C?.items));

    console.log('');
    console.log('▸ 阶段 D  测点字段可以显式指定（改成按 address 分组）');
    await cdp.evaluate(sessionId, openModalJs(TOPIC));
    await sleep(300);
    await cdp.evaluate(sessionId, `(() => {
      document.getElementById('mGroupBy').value = 'address';
      document.getElementById('mSave').click();
      return 1;
    })()`);
    await sleep(600);
    // 换分组字段要把已有曲线清掉才看得出效果（曲线 key 变了）
    await cdp.evaluate(sessionId, `(() => {
      const M = window.MQTTMonitor;
      const c = M.state.cards.find(x => x.topic === ${JSON.stringify(TOPIC)});
      c.series = []; c.seriesMap = new Map(); c.seenItems = new Map();
      M.markDirty(c);
      return 1;
    })()`);
    const dev2 = await connectDevice();
    await publishRounds(dev2, 3);
    await sleep(1200);
    const D = await cdp.evaluate(sessionId, CARD_JS(TOPIC));
    check('★ 改成按 address 分组后，测点名变成了地址',
      (D?.series || []).length === 3 && (D?.series || []).every((s) => /^DB1\.DBW\d+$/.test(s.label)),
      (D?.series || []).map((s) => s.label).join(' , '));
    check('groupBy 落盘了',
      JSON.parse(fs.readFileSync(cfgPath, 'utf8')).cards.find((c) => c.topic === TOPIC)?.groupBy === 'address');
    // 改回自动
    await cdp.evaluate(sessionId, `(() => {
      const M = window.MQTTMonitor;
      const c = M.state.cards.find(x => x.topic === ${JSON.stringify(TOPIC)});
      c.groupBy = ''; c.series = []; c.seriesMap = new Map(); c.seenItems = new Map();
      M.markDirty(c); M.saveCards();
      return 1;
    })()`);

    console.log('');
    console.log('▸ 阶段 E  测点太多：只画 6 条，但要让人知道还有几条、且能勾出来');
    const E = await cdp.evaluate(sessionId, CARD_JS(TOPIC_MANY));
    check('8 个测点的主题画了 6 条（MAX_SERIES 生效）', E?.series.length === 6,
      `series=${E?.series.length} seen=${(E?.seenItems || []).length}`);
    check('★ 但 8 个测点全都被记下来了（没丢信息）', (E?.seenItems || []).length === 8,
      (E?.seenItems || []).join(' , '));
    check('★ 图例上提示还有 2 条没显示（"+2"）', E?.overflow === 2 && /\+2/.test(E?.legend || ''),
      `overflow=${E?.overflow} legend=${E?.legend}`);
    await cdp.evaluate(sessionId, openModalJs(TOPIC_MANY));
    await sleep(400);
    const P2 = await cdp.evaluate(sessionId, PICKER_JS);
    check('★ 勾选列表里 8 个测点都能选到（包括没画的 2 个）',
      (P2?.rows || []).filter((r) => r.isItem).length === 8,
      (P2?.rows || []).filter((r) => r.isItem).map((r) => r.name).join(' , '));
    // 把没画出来的两个勾上 —— 这是"能选择画哪些"的关键用途
    const pickedMany = await cdp.evaluate(sessionId,
      pickJs([`__item__:测点7`, `__item__:测点8`]));
    await sleep(900);
    const E2 = await cdp.evaluate(sessionId, CARD_JS(TOPIC_MANY));
    check('★ 勾上原本没画的测点后，它们真的画出来了',
      (E2?.series || []).some((s) => s.label === '测点7') && (E2?.series || []).some((s) => s.label === '测点8'),
      (E2?.series || []).map((s) => s.label).join(' , '));
    check('此时只画勾的这 2 条', E2?.series.length === 2, `series=${E2?.series.length}`);
    // 这两个测点之前根本没被画过，页面上没有它们的数据 ——
    // 必须立刻从落盘库补回来，否则用户勾完要干等设备下一次上报（现场 60~80 秒）
    check('★ 勾出来的新曲线立刻从落盘库补上了历史（不是空曲线）',
      (E2?.series || []).every((s) => s.n >= 1),
      (E2?.series || []).map((s) => `${s.label}=${s.n}`).join(' '));
    check('勾上之后 "+N" 提示消失', !/\+/.test(E2?.legend || ''), E2?.legend);

    console.log('');
    console.log('▸ 阶段 F  刷新页面：历史回填也要按测点分组');
    await cdp.evaluate(sessionId, `(() => {
      const M = window.MQTTMonitor;
      const c = M.state.cards.find(x => x.topic === ${JSON.stringify(TOPIC_MANY)});
      c.items = []; M.markDirty(c); M.saveCards();
      return 1;
    })()`);
    await sleep(800);
    await cdp.send('Page.navigate', { url: `${BASE}/` }, sessionId);
    for (let i = 0; i < 40; i++) {
      await sleep(400);
      const ok = await cdp.evaluate(sessionId, '!!(window.MQTTMonitor && window.MQTTMonitor.state.cards.length)')
        .catch(() => false);
      if (ok) break;
    }
    await sleep(2500);
    const F = await cdp.evaluate(sessionId, CARD_JS(TOPIC));
    check('刷新后卡片仍在', !!F, F ? `series=${F.series.length}` : 'null');
    check('★ 刷新后仍是按测点分组的 3 条曲线', F?.series.length === 3,
      (F?.series || []).map((s) => s.label).join(' , '));
    check('★ 刷新后每条曲线的历史都从库里回填了（不是只剩实时那一条）',
      (F?.series || []).every((s) => s.n >= 10),
      (F?.series || []).map((s) => `${s.label}=${s.n}`).join(' '));
    check('刷新后互不串数据仍然成立',
      (F?.series || []).every((s) => {
        const p = POINTS.find((x) => x.tag === s.label);
        return p && s.min >= p.base && s.max < p.base + N_ROUND;
      }),
      (F?.series || []).map((s) => `${s.label}:${s.min}~${s.max}`).join(' '));

    console.log('');
    console.log('▸ 截图');
    const shot = await cdp.send('Page.captureScreenshot', { format: 'png' }, sessionId);
    fs.writeFileSync(shotPath, Buffer.from(shot.data, 'base64'));
    check('截图已保存', fs.existsSync(shotPath) && fs.statSync(shotPath).size > 5000,
      `${(fs.statSync(shotPath).size / 1024).toFixed(0)} KB → tools/_ui_items.png`);

    check('无 JS 未捕获异常', cdp.exceptions.length === 0, cdp.exceptions.slice(0, 2).join(' | ') || '无');
    const errs = cdp.consoles.filter((c) => c.type === 'error');
    check('无 console.error', errs.length === 0, errs.slice(0, 2).map((e) => e.text).join(' | ') || '无');

    try { await new Promise((r) => dev.end(false, {}, r)); } catch { /* ignore */ }
    try { await new Promise((r) => dev2.end(false, {}, r)); } catch { /* ignore */ }
  } finally {
    try { cdp?.close(); } catch { /* ignore */ }
    killTree(edge);
    killTree(srv.child);
    await sleep(1200);
    try { fs.rmSync(tmp, { recursive: true, force: true }); } catch { /* ignore */ }
  }

  console.log('');
  console.log('════════════════════════════════════════');
  console.log(`  结果：${pass} 项通过，${fail} 项失败`);
  console.log('════════════════════════════════════════');
  console.log('');
  process.exit(fail ? 1 : 0);
})().catch((err) => {
  console.error('');
  console.error(`跑挂了：${err.message}`);
  process.exit(1);
});

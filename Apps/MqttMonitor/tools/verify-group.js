'use strict';

/**
 * 「一台设备一张卡（分组卡片）」的前端验证（真实浏览器，无头 Edge + CDP）
 *
 * 场景：同一个 PLC 的点位常常分开发在不同主题上 ——
 *   plc/DB1.DBW290 = 电池电量 、plc/DB1.DBW292 = 电池电流
 * 默认一张卡片只管一个主题，于是同一台设备的电压电流被拆成两张卡、时间轴对不齐。
 * 分组卡片把「主题前缀」当成一台设备：plc/* 下的所有主题进一张卡，每个主题一条曲线。
 *
 * 这个脚本要证明的几件事（都不是"看着像对"能确认的）：
 *   1. 前缀匹配的**边界**：plc 不能把 plc2/… 也收进来；
 *   2. 曲线 key 里带着**主题**（__topic__:plc/DB1.DBW290|…），两条曲线不能串数据；
 *   3. 刷新页面后每个成员主题的历史**分别**回填（一条主题的 backfill 不能顶掉别的）；
 *   4. 每个主题可以**单独切换**画哪个 tag/字段，切完能立刻从库里补出历史（不是空曲线）；
 *   5. 超过上限时老实说「+N」，不假装画全了。
 *
 * 全程用临时目录，不动正式实例的 config.json / data。
 * 用法：node tools/verify-group.js
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
const WEB_PORT = 8097;
const BROKER_PORT = 18837;
const CDP_PORT = 9339;
const BASE = `http://127.0.0.1:${WEB_PORT}`;
const VIEW = { width: 1500, height: 900 };

const G = 'plc';            // 分组前缀
const T_V = G + '/DB1.DBW290';   // 电池电量
const T_A = G + '/DB1.DBW292';   // 电池电流
const T_DECOY = 'plc2/DB1.DBW290'; // 不得被 plc 收走

const N = 12;               // 每轮实时条数
const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'mqttmon-group-'));
const cfgPath = path.join(tmp, 'config.json');
const dataDir = path.join(tmp, 'data');
const profileDir = path.join(tmp, 'edgeprofile');
const shotPath = path.join(ROOT, 'tools', '_ui_group.png');
const endShotPath = path.join(ROOT, 'tools', '_ui_group_end.png');
const pickerShotPath = path.join(ROOT, 'tools', '_ui_group_picker.png');
const offShotPath = path.join(ROOT, 'tools', '_ui_group_offboard.png');
const staleShotPath = path.join(ROOT, 'tools', '_ui_group_stale.png');

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

/** 设备侧按「真实网关报文」的形态发：tag 是名称，address 是地址，value 是值 */
function connectDevice() {
  return new Promise((resolve, reject) => {
    const client = mqtt.connect(`mqtt://127.0.0.1:${BROKER_PORT}`, {
      clientId: `group-dev-${Date.now()}-${Math.random().toString(16).slice(2, 6)}`,
      clean: true, reconnectPeriod: 0, connectTimeout: 5000,
    });
    const t = setTimeout(() => reject(new Error('连接 broker 超时')), 6000);
    client.once('connect', () => { clearTimeout(t); resolve(client); });
    client.once('error', (e) => { clearTimeout(t); reject(e); });
  });
}

const pub = (client, topic, payload) =>
  new Promise((res) => client.publish(topic, payload, { qos: 0 }, res));

/** 电池电量：100 起、每次 +10（好认） */
const payloadV = (i) => JSON.stringify({
  tag: '电池电量', address: 'DB1.DBW290', description: '', dataType: 'Int16',
  value: String(100 + i * 10), ts: new Date().toISOString(),
});
/** 电池电流：5000 起、每次 +7（刻意和电量量纲差很远，串数据一眼能看出来） */
const payloadA = (i) => JSON.stringify({
  tag: '电池电流', address: 'DB1.DBW292', description: '', dataType: 'Int16',
  value: String(5000 + i * 7), ts: new Date().toISOString(),
});

/** 极简 CDP 客户端 */
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
//  一、纯函数断言：把前缀匹配与 key 解析抠出来单跑
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
  const names = ['groupRoot', 'inGroupOf', 'isTopicKey', 'topicKeyOf', 'fieldKeyOf', 'topicKey'];
  const body = `const TOPIC_PREFIX = '__topic__:';\nconst TOPIC_SEP = '|';\n`
    + `${names.map(grab).join('\n')}\nreturn { ${names.join(', ')} };`;
  return new Function(body)();
}

function unitTests() {
  console.log('▸ 纯函数断言（直接跑 public/app.js 里的实现）');
  let fns;
  try {
    fns = extractPureFns();
  } catch (e) {
    check('能从 app.js 抠出前缀匹配与 key 解析', false, e.message);
    return;
  }
  check('能从 app.js 抠出前缀匹配与 key 解析', true);
  const { groupRoot, inGroupOf, topicKeyOf, fieldKeyOf, topicKey, isTopicKey } = fns;

  // 前缀归一：用户会写成 plc / plc/ / plc/* / plc/#
  check('groupRoot 归一前缀（plc / plc/ / plc/* / plc/# 等价）',
    ['plc', 'plc/', 'plc/*', 'plc/#', ' plc/ '].every((s) => groupRoot(s) === 'plc'),
    ['plc', 'plc/', 'plc/*', 'plc/#'].map((s) => `${s}→${groupRoot(s)}`).join(' '));

  // ★ 边界：前缀必须按「/」切，否则 plc 会抢走 plc2/…
  check('★ plc 不收 plc2/xxx', inGroupOf('plc', 'plc2/DB1.DBW290') === false);
  check('★ plc 不收字面量 plc（本身没有子主题）', inGroupOf('plc', 'plc') === false);
  check('plc 收 plc/DB1.DBW290', inGroupOf('plc', 'plc/DB1.DBW290') === true);
  check('plc 收更深一层 plc/a/b', inGroupOf('plc', 'plc/a/b') === true);
  check('归一后的前缀照样能匹配（plc/# → plc/xxx）', inGroupOf('plc/#', 'plc/x') === true);
  check('空前缀不会匹配一切', inGroupOf('', 'plc/x') === false && inGroupOf('/', 'plc/x') === false);

  // key 往返：__topic__:主题|字段
  const k1 = topicKey('plc/DB1.DBW290', '__item__:电池电量');
  check('topicKey 拼出 __topic__:主题|字段', k1 === '__topic__:plc/DB1.DBW290|__item__:电池电量', k1);
  check('topicKey 能拆回主题', topicKeyOf(k1) === 'plc/DB1.DBW290', topicKeyOf(k1));
  check('topicKey 能拆回字段', fieldKeyOf(k1) === '__item__:电池电量', fieldKeyOf(k1));
  check('主题里带 / 也不会拆错', topicKeyOf(topicKey('a/b/c', 'value')) === 'a/b/c'
    && fieldKeyOf(topicKey('a/b/c', 'value')) === 'value');
  check('字段缺省兜底成 __v', topicKey('plc/x', '') === '__topic__:plc/x|__v', topicKey('plc/x', ''));
  check('__topic__ 前缀能被认出来', isTopicKey(k1) === true && isTopicKey('__item__:x') === false
    && isTopicKey('value') === false);
  // 主题 key 不能和测点 key 撞
  check('__topic__: 与 __item__: 互不误判',
    !isTopicKey('__item__:x') && topicKeyOf('__item__:x') === '');
}

// ============================================================
//  二、页面内探针
// ============================================================
const REPORT_JS = (topic) => `(() => {
  const M = window.MQTTMonitor;
  const card = M.state.cards.find(c => c.topic === ${JSON.stringify(topic)});
  if (!card) return null;
  return {
    topic: card.topic, group: !!card.group, name: card.name,
    isGroupCard: card.group === true,
    seriesCount: card.series.length,
    overflow: card.overflow,
    picks: JSON.parse(JSON.stringify(card.picks || {})),
    picked: (card.picked || []).slice(),
    removed: (card.removed || []).slice(),
    fields: (card.fields || []).slice(),
    // 卡片大字（主曲线的当前值）—— 验"未勾的主题不许把值刷上来"
    text: card.text, num: card.num,
    topicSub: card.el ? card.el.topic.textContent : null,
    topicTitle: card.el ? card.el.topic.title : null,
    legend: card.el ? card.el.legend.textContent.replace(/\\s+/g, ' ').trim() : null,
    // 图例一行一个主题：名字 / 值 / 是不是"只有值没有曲线"
    legendRows: card.el ? [...card.el.legend.querySelectorAll('.lg-item')].map(n => ({
      name: (n.querySelector('.lg-name') || {}).textContent,
      val: (n.querySelector('.lg-val') || {}).textContent,
      noCurve: n.classList.contains('is-nocurve'),
      empty: n.classList.contains('is-empty'),
    })) : [],
    legendMore: card.el ? (card.el.legend.querySelector('.lg-more') || {}).textContent : null,
    chart: card._chart || null,
    series: card.series.map(s => ({
      key: s.key, label: s.label, n: s.data.length,
      // 当前展示值：曲线有点、但 num/text 还是空的话，卡片大字和图例就会挂「—」
      num: s.num, text: s.text,
      tsLen: s.ts ? s.ts.length : -1,
      min: s.data.length ? Math.min(...s.data.slice(0, 400)) : null,
      max: s.data.length ? Math.max(...s.data.slice(0, 400)) : null,
      first: s.data[0], last: s.data[s.data.length - 1],
    })),
    // 整张卡上所有曲线里有多少条带 __topic__ 前缀（分组卡应该全部都是）
    topicSeries: card.series.filter(s => String(s.key).indexOf('__topic__:') === 0).length,
    members: (card.members || []).slice(),
    seenTopics: card.seenByTopic ? [...card.seenByTopic.keys()] : [],
  };
})()`;

const CARDS_JS = `(() => {
  const M = window.MQTTMonitor;
  return {
    n: M.state.cards.length,
    topics: M.state.cards.map(c => c.topic),
    groups: M.state.cards.filter(c => c.group).map(c => c.topic),
  };
})()`;

const GROUP_TOPICS_JS = `(() => [...window.MQTTMonitor.state.topics.keys()])()`;

/** 弹窗现状：分组开关、每个主题的下拉、选项 */
const MODAL_JS = `(() => {
  const box = document.getElementById('mFieldPick');
  return {
    open: document.getElementById('cardModal').classList.contains('show'),
    groupChecked: document.getElementById('mGroup').checked,
    topicValue: document.getElementById('mTopic').value,
    topicPlaceholder: document.getElementById('mTopic').placeholder,
    hint: document.getElementById('mFieldHint').textContent,
    noteText: document.getElementById('mPickNote').textContent.replace(/\\s+/g, ' ').trim(),
    sels: [...box.querySelectorAll('select[data-topic]')].map(s => ({
      topic: s.dataset.topic, value: s.value,
      options: [...s.options].map(o => ({ v: o.value, t: o.textContent })),
    })),
    // 每个主题的「画这条曲线吗」勾选框（value 无意义，看 checked）
    topics: [...box.querySelectorAll('input.fp-topic')].map(n => ({ topic: n.dataset.topic, on: n.checked })),
    checks: [...box.querySelectorAll('input[type=checkbox]:not(.fp-topic)')].map(n => n.value),
    // 「已从卡片删除」那一栏
    removedHead: (box.querySelector('.fp-removed-head') || {}).textContent || '',
    removedRows: [...box.querySelectorAll('.fp-removed .fp-topic-name')].map(n => n.textContent),
    restore: [...box.querySelectorAll('.fp-restore')].map(b => b.dataset.topic),
    empty: !!box.querySelector('.fp-empty'),
  };
})()`;

const SIDEBAR_JS = (topic) => `(() => {
  const items = [...document.querySelectorAll('#topicList .topic-item')];
  const el = items.find(i => i.querySelector('.t-name').textContent === ${JSON.stringify(topic)});
  if (!el) return null;
  return {
    cls: el.className,
    inGroup: el.classList.contains('in-group'),
    offGroup: el.classList.contains('off-group'),
    onBoard: el.classList.contains('on-board'),
    btn: el.querySelector('.t-add').textContent,
    btnTitle: el.querySelector('.t-add').title,
  };
})()`;

/** 点侧栏那一行的 ✓ / + （取消上板 / 加回来） */
const SIDEBAR_CLICK_JS = (topic) => `(() => {
  const items = [...document.querySelectorAll('#topicList .topic-item')];
  const el = items.find(i => i.querySelector('.t-name').textContent === ${JSON.stringify(topic)});
  if (!el) return null;
  el.querySelector('.t-add').click();
  return 1;
})()`;

const OPEN_MODAL_JS = (topic) => `(() => {
  const c = document.querySelector('.card[data-topic=' + JSON.stringify(${JSON.stringify(topic)}) + ']');
  if (!c) return 'nocard';
  const g = c.querySelector('.t-add');
  if (!g) return 'nobtn';
  g.click();
  return 'ok';
})()`;

// ============================================================
//  三、主流程
// ============================================================
async function main() {
  console.log('');
  console.log('══ 「一台设备一张卡（分组卡片）」前端验证（无头 Edge + CDP）══');
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

    // 关掉自动上板：本脚本要验的是"分组卡怎么收主题"，不希望每条主题先冒出自己的一张卡。
    // 顺手写进 localStorage —— 后面要刷新页面，boot 是从 localStorage 读这个开关的。
    await cdp.evaluate(sessionId, `(() => {
      window.MQTTMonitor.state.autoAdd = false;
      localStorage.setItem('mqtt-monitor-autoadd', '0');
      // 自动合并同理先关掉：前面几个阶段要手工控制"什么时候合并"，
      // 让它在建卡时顺手并掉会让断言变得不可复现（阶段 I 专门验它）。
      window.MQTTMonitor.state.autoGroup = false;
      localStorage.setItem('mqtt-monitor-autogroup', '0');
      return 1;
    })()`);

    // ── 阶段 A：前缀匹配与路由 ──
    console.log('');
    console.log('▸ 阶段 A  一台设备一张卡：前缀下的主题各一条曲线');
    const dev = await connectDevice();
    for (let i = 0; i < N; i++) await pub(dev, T_V, payloadV(i));
    await sleep(600);

    let cards = await cdp.evaluate(sessionId, CARDS_JS);
    check('自动上板已关：这批报文没建出卡片', cards.n === 0, JSON.stringify(cards.topics));

    // 建一张分组卡（等价于：打开任意卡片的设置 → 勾「按前缀分组」→ 主题填 plc → 保存）
    const created = await cdp.evaluate(sessionId, `(() => {
      const c = window.MQTTMonitor.createCard({ topic: ${JSON.stringify(G)}, group: true, name: 'PLC 总览' });
      return { topic: c.topic, group: !!c.group };
    })()`);
    check('分组卡建出来了，主题存的是前缀', created.topic === G && created.group === true,
      JSON.stringify(created));

    // 建好之后再发：实时链路要把这些主题**路由进这张卡**，而不是各建一张
    for (let i = 0; i < N; i++) {
      await pub(dev, T_V, payloadV(N + i));
      await pub(dev, T_A, payloadA(i));
    }
    for (let i = 0; i < 6; i++) await pub(dev, T_DECOY, payloadV(i));
    await sleep(1200);

    cards = await cdp.evaluate(sessionId, CARDS_JS);
    check('★ 前缀下的主题没有各建一张卡（分组卡收着，不重复建单主题卡）',
      cards.n === 1 && cards.topics[0] === G, `cards=${JSON.stringify(cards.topics)}`);
    // 路由本身：plc/… 归 plc 卡；plc2/… 谁都不归（没建 plc2 的分组卡，自动上板也关着）
    // —— 这一条才是"plc 没抢走 plc2"的正面证据，不是靠"plc2 建了卡"反推。
    const route = await cdp.evaluate(sessionId, `(() => {
      const f = (t) => { const c = window.MQTTMonitor.groupCardFor(t); return c ? c.topic : null; };
      return { v: f(${JSON.stringify(T_V)}), a: f(${JSON.stringify(T_A)}), decoy: f(${JSON.stringify(T_DECOY)}) };
    })()`);
    check('★ plc/… 路由到 plc 卡，plc2/… 没被 plc 抢走',
      route.v === G && route.a === G && route.decoy === null, JSON.stringify(route));

    let R = await cdp.evaluate(sessionId, REPORT_JS(G));
    check('卡片是分组卡', R && R.isGroupCard === true);
    check('★ 两个主题 → 两条曲线', R.seriesCount === 2, `series=${R.seriesCount}`);
    check('★ 曲线 key 里带着主题（不是裸的字段路径）',
      R.series.every((s) => s.key.startsWith('__topic__:'))
      && R.series.some((s) => s.key.startsWith(`__topic__:${T_V}|`))
      && R.series.some((s) => s.key.startsWith(`__topic__:${T_A}|`)),
      R.series.map((s) => s.key).join('  '));
    check('★ plc2 的报文没有混进来',
      !R.series.some((s) => s.key.includes('plc2')), R.series.map((s) => s.key).join('  '));
    check('曲线标签用测点名（电池电量 / 电池电流），不是主题名',
      JSON.stringify(R.series.map((s) => s.label).sort()) === JSON.stringify(['电池电量', '电池电流'].sort()),
      JSON.stringify(R.series.map((s) => s.label)));
    const sv = R.series.find((s) => s.key.startsWith(`__topic__:${T_V}|`));
    const sa = R.series.find((s) => s.key.startsWith(`__topic__:${T_A}|`));
    check('两条曲线各有数据', sv.n >= N && sa.n >= N, `电量=${sv.n} 电流=${sa.n}`);
    check('★ 两条曲线没串数据（量纲差 50 倍，一眼看得出）',
      sv.min >= 100 && sv.max <= 400 && sa.min >= 5000 && sa.max <= 5200,
      `电量 ${sv.min}~${sv.max} · 电流 ${sa.min}~${sa.max}`);
    check('ts 与 data 一一对应', sv.tsLen === sv.n && sa.tsLen === sa.n);
    check('卡片副标题写明是前缀分组', /^plc\/\*/.test(R.topicSub) && /2 个点位/.test(R.topicSub), R.topicSub);
    check('图例列出两个测点名', /电池电量/.test(R.legend) && /电池电流/.test(R.legend), R.legend);
    check('没有多余的「+N」（两个成员都画出来了）', R.overflow === 0, `overflow=${R.overflow}`);

    const side = await cdp.evaluate(sessionId, SIDEBAR_JS(T_V));
    check('侧栏把该主题标成「已在分组卡里」', side && side.inGroup === true && side.onBoard === false,
      JSON.stringify(side));
    check('侧栏那个 ✓ 的提示说明了是被哪张分组卡收着', /分组卡/.test(side.btnTitle), side.btnTitle);

    // ── 阶段 B：每个主题单独切换画哪个 tag/字段 ──
    console.log('');
    console.log('▸ 阶段 B  每个主题可以单独切换画哪个 tag / 字段');
    const opened = await cdp.evaluate(sessionId, OPEN_MODAL_JS(G));
    await sleep(350);
    let M = await cdp.evaluate(sessionId, MODAL_JS);
    check('卡片设置弹窗打开了', opened === 'ok' && M.open === true, opened);
    check('「按前缀分组」是勾上的', M.groupChecked === true);
    check('主题框里是前缀', M.topicValue === G, M.topicValue);
    check('★ 每个主题一行下拉', M.sels.length === 2 && M.sels.every((s) => s.options.length >= 2),
      JSON.stringify(M.sels.map((s) => `${s.topic}:${s.options.length} 项`)));
    check('下拉第一项是「自动」且当前选中',
      M.sels.every((s) => s.value === '' && /^自动/.test(s.options[0].t)),
      JSON.stringify(M.sels.map((s) => s.options[0].t)));
    check('自动项写明了会画哪个字段',
      M.sels.every((s) => /（.+）/.test(s.options[0].t)), JSON.stringify(M.sels.map((s) => s.options[0].t)));
    check('下拉里能选到这个主题的测点',
      M.sels.some((s) => s.options.some((o) => o.v === '__item__:电池电量')),
      JSON.stringify(M.sels.map((s) => s.options.map((o) => o.v))));
    check('★ 每个主题一个「画不画」勾选框，默认都不勾（= 自动）',
      M.topics.length === 2 && M.topics.every((t) => t.on === false),
      JSON.stringify(M.topics));
    check('说明文案换成了分组版的（勾=画曲线，不勾=只显示实时值）',
      /勾上要画曲线的主题/.test(M.noteText) && /不勾的也留在卡片上显示实时值/.test(M.noteText),
      M.noteText.slice(0, 80));
    check('底部提示说清「都显示实时值 + 自动画几条 + 可以勾选挑」',
      /2 个主题在卡片上（都显示实时值）/.test(M.hint)
      && /当前自动：画前 2 条曲线/.test(M.hint)
      && /勾选可以只画其中几条/.test(M.hint), M.hint);

    // 把"电池电量"那条切到 value 字段
    await cdp.evaluate(sessionId, `(() => {
      const s = [...document.querySelectorAll('#mFieldPick select[data-topic]')]
        .find(x => x.dataset.topic === ${JSON.stringify(T_V)});
      s.value = 'value';
      s.dispatchEvent(new Event('change'));
      return s.value;
    })()`);
    await cdp.evaluate(sessionId, `document.getElementById('mSave').click()`);
    await sleep(1500);
    R = await cdp.evaluate(sessionId, REPORT_JS(G));
    check('切换后仍是两条曲线', R.seriesCount === 2, `series=${R.seriesCount}`);
    check('★ 被切的那条曲线 key 换成了指定字段，另一条不受影响',
      R.series.some((s) => s.key === `__topic__:${T_V}|value`)
      && R.series.some((s) => s.key.startsWith(`__topic__:${T_A}|__item__:`)),
      R.series.map((s) => s.key).join('  '));
    check('切过来的曲线标签跟着变成主题末段', (() => {
      const s = R.series.find((x) => x.key === `__topic__:${T_V}|value`);
      return s && s.label === 'DB1.DBW290';
    })(), JSON.stringify(R.series.map((s) => `${s.key}=${s.label}`)));
    check('★ 切换后的新曲线立刻从落盘库补上了历史（不是空曲线）', (() => {
      const s = R.series.find((x) => x.key === `__topic__:${T_V}|value`);
      return s && s.n >= N && s.tsLen === s.n;
    })(), JSON.stringify(R.series.map((s) => `${s.key}:${s.n}`)));
    check('切换后另一个主题的曲线没被清掉', (() => {
      const s = R.series.find((x) => x.key.startsWith(`__topic__:${T_A}|`));
      return s && s.n >= N;
    })());
    check('picks 落到了卡片上', R.picks[T_V] === 'value', JSON.stringify(R.picks));
    // ★ 新曲线只有回填数据、还没有实时报文打到它头上（设备 60~80 秒一条），
    //   所以「当前值」必须由回填自己补上，否则大字/图例一直挂「—」，看着像没数据。
    check('★ 切过来的曲线立刻有当前值（不是只有点、值那栏写着「—」）', (() => {
      const s = R.series.find((x) => x.key === `__topic__:${T_V}|value`);
      return s && s.n >= N && s.num !== null && s.text !== '—';
    })(), JSON.stringify(R.series.map((s) => `${s.key}: n=${s.n} num=${s.num} text=${s.text}`)));
    check('★ 卡片大字也跟着这条新曲线（不靠下一帧实时报文）',
      R.num !== null && R.text !== '—', `text=${JSON.stringify(R.text)} num=${R.num}`);

    // 切回自动
    await cdp.evaluate(sessionId, OPEN_MODAL_JS(G));
    await sleep(300);
    await cdp.evaluate(sessionId, `(() => {
      const s = [...document.querySelectorAll('#mFieldPick select[data-topic]')]
        .find(x => x.dataset.topic === ${JSON.stringify(T_V)});
      s.value = '';
      s.dispatchEvent(new Event('change'));
      return 1;
    })()`);
    await cdp.evaluate(sessionId, `document.getElementById('mSave').click()`);
    await sleep(1500);
    R = await cdp.evaluate(sessionId, REPORT_JS(G));
    check('★ 切回「自动」后 key 回到测点，标签也回来了',
      R.series.some((s) => s.key === `__topic__:${T_V}|__item__:电池电量`)
      && R.series.some((s) => s.label === '电池电量'),
      JSON.stringify(R.series.map((s) => `${s.key}=${s.label}`)));
    check('切回自动后 picks 被清空', Object.keys(R.picks).length === 0, JSON.stringify(R.picks));
    check('★ 切回自动后的那条曲线也有当前值（换回来不会把值弄丢）', (() => {
      const s = R.series.find((x) => x.key === `__topic__:${T_V}|__item__:电池电量`);
      return s && s.n >= N && s.num !== null && s.text !== '—';
    })(), JSON.stringify(R.series.map((s) => `${s.key}: num=${s.num} text=${s.text}`)));
    check('切回自动后曲线仍有历史', (() => {
      const s = R.series.find((x) => x.key === `__topic__:${T_V}|__item__:电池电量`);
      return s && s.n >= N;
    })(), JSON.stringify(R.series.map((s) => `${s.key}:${s.n}`)));

    // ── 阶段 B2：勾选要显示哪几个主题 ──
    console.log('');
    console.log('▸ 阶段 B2  勾选要显示哪几个主题（没勾的不占名额）');
    await cdp.evaluate(sessionId, OPEN_MODAL_JS(G));
    await sleep(300);
    await cdp.evaluate(sessionId, `(() => {
      const cb = [...document.querySelectorAll('#mFieldPick input.fp-topic')]
        .find((n) => n.dataset.topic === ${JSON.stringify(T_V)});
      cb.checked = true;
      cb.dispatchEvent(new Event('change'));
      return 1;
    })()`);
    await sleep(150);
    let MB = await cdp.evaluate(sessionId, MODAL_JS);
    check('勾上后底部提示变成「已勾 1 / 2 个主题画曲线，其余只显示实时值」',
      /已勾 1 \/ 2 个主题画曲线/.test(MB.hint) && /其余 1 个只显示实时值/.test(MB.hint), MB.hint);
    await cdp.evaluate(sessionId, `document.getElementById('mSave').click()`);
    await sleep(1600);
    R = await cdp.evaluate(sessionId, REPORT_JS(G));
    check('★ 只画勾上的那一条曲线（另一个主题不占曲线名额）',
      R.seriesCount === 1 && String(R.series[0] && R.series[0].key).indexOf(`__topic__:${T_V}|`) === 0,
      `${R.seriesCount} 条: ${R.series.map((s) => s.key).join('  ')}`);
    check('勾选落到了卡片的 picked 上',
      JSON.stringify(R.picked) === JSON.stringify([T_V]), JSON.stringify(R.picked));
    check('★ 勾选框的值（主题名）没被当成字段路径混进 fields',
      (R.fields || []).length === 0, JSON.stringify(R.fields));
    check('副标题写清「几个点位 + 画了几条曲线」',
      /2 个点位/.test(R.topicSub) && /曲线 1 条/.test(R.topicSub), R.topicSub);
    check('★ 没勾的主题照样在图例里显示实时值（空心点、不画线）', (() => {
      const row = R.legendRows.find((r) => r.name === '电池电流');
      return R.legendRows.length === 2 && !!row && row.noCurve === true && row.val === '5077';
    })(), JSON.stringify(R.legendRows));
    check('★ 没勾的主题不占曲线名额，所以没有多余的「+N」',
      R.overflow === 0 && !R.legendMore, `overflow=${R.overflow} more=${JSON.stringify(R.legendMore)}`);
    check('被勾的主题曲线有数据（不是空曲线）', R.series[0].n >= N, `n=${R.series[0].n}`);

    await cdp.evaluate(sessionId, OPEN_MODAL_JS(G));
    await sleep(300);
    MB = await cdp.evaluate(sessionId, MODAL_JS);
    check('重新打开弹窗时勾选状态还在', (() => {
      const on = MB.topics.filter((t) => t.on).map((t) => t.topic);
      return on.length === 1 && on[0] === T_V;
    })(), JSON.stringify(MB.topics));

    // 顺手把「选择面板」拍下来（勾了一个、另一个压暗），这是这个功能唯一的可视化证据
    await cdp.send('Page.captureScreenshot', {}, sessionId).then((r) => {
      fs.writeFileSync(pickerShotPath, Buffer.from(r.data, 'base64'));
    });
    check('选择面板截图已保存',
      fs.existsSync(pickerShotPath) && fs.statSync(pickerShotPath).size > 10000,
      `${Math.round(fs.statSync(pickerShotPath).size / 1024)} KB → tools/_ui_group_picker.png`);

    await cdp.evaluate(sessionId, `document.getElementById('mFieldClear').click()`);
    await sleep(150);
    MB = await cdp.evaluate(sessionId, MODAL_JS);
    check('点「全部取消」后勾选框全空、提示回到自动',
      MB.topics.every((t) => !t.on) && /当前自动/.test(MB.hint), MB.hint);
    await cdp.evaluate(sessionId, `document.getElementById('mSave').click()`);
    await sleep(1600);
    R = await cdp.evaluate(sessionId, REPORT_JS(G));
    check('★ 取消全部勾选 = 回到自动，两条曲线都回来（不用等设备下一条）',
      R.seriesCount === 2 && R.picked.length === 0,
      `series=${R.seriesCount} picked=${JSON.stringify(R.picked)}`);
    check('回到自动后副标题不再写「x/2」', !/\d\/\d/.test(R.topicSub), R.topicSub);

    // ── 阶段 B3：未勾的主题「值」也不上卡片；主题可从卡片删除 ──
    console.log('');
    console.log('▸ 阶段 B3  未勾的主题不显示实时值 + 主题可从卡片删除');
    // 只勾电量，然后让**没勾的电流**发一个很好认的新值（9999）
    await cdp.evaluate(sessionId, OPEN_MODAL_JS(G));
    await sleep(300);
    await cdp.evaluate(sessionId, `(() => {
      const cb = [...document.querySelectorAll('#mFieldPick input.fp-topic')]
        .find((n) => n.dataset.topic === ${JSON.stringify(T_V)});
      cb.checked = true;
      cb.dispatchEvent(new Event('change'));
      return 1;
    })()`);
    await cdp.evaluate(sessionId, `document.getElementById('mSave').click()`);
    await sleep(1600);
    await pub(dev, T_A, JSON.stringify({ tag: '电池电流', address: 'DB1.DBW292', value: '9999' }));
    await sleep(900);
    R = await cdp.evaluate(sessionId, REPORT_JS(G));
    check('★ 没勾的主题不画曲线（它上报了也不建线）',
      R.seriesCount === 1 && String(R.series[0] && R.series[0].key).indexOf(`__topic__:${T_V}|`) === 0,
      `${R.seriesCount} 条: ${R.series.map((s) => s.key).join('  ')}`);
    check('★ 没勾的主题新值照样实时显示在图例里（灰点、无曲线）', (() => {
      const row = R.legendRows.find((r) => r.name === '电池电流');
      return !!row && row.noCurve === true && row.val === '9999';
    })(), JSON.stringify(R.legendRows));
    check('★ 没勾的主题不抢卡片大字（大字还是勾了曲线的那条）',
      R.num !== 9999 && String(R.text).indexOf('9999') < 0, `text=${JSON.stringify(R.text)} num=${R.num}`);

    // 把没勾的那个主题从卡片上删掉（勾选框右边的 ✕）
    await cdp.evaluate(sessionId, OPEN_MODAL_JS(G));
    await sleep(300);
    await cdp.evaluate(sessionId, `(() => {
      const row = [...document.querySelectorAll('#mFieldPick .fp-group.is-topic')]
        .find((h) => h.querySelector('input.fp-topic').dataset.topic === ${JSON.stringify(T_A)});
      row.querySelector('.fp-del').click();
      return 1;
    })()`);
    await sleep(800);
    let MD = await cdp.evaluate(sessionId, MODAL_JS);
    check('删除后弹窗里不再把它列为卡片上的主题',
      MD.topics.every((t) => t.topic !== T_A), JSON.stringify(MD.topics));
    check('弹窗底部出现「已从卡片删除」并提供恢复入口',
      /已从卡片删除（1）/.test(MD.removedHead)
      && JSON.stringify(MD.removedRows) === JSON.stringify([T_A])
      && JSON.stringify(MD.restore) === JSON.stringify([T_A]),
      `${MD.removedHead} rows=${JSON.stringify(MD.removedRows)}`);
    R = await cdp.evaluate(sessionId, REPORT_JS(G));
    check('卡片上不再有被删主题的曲线', R.seriesCount === 1 && R.removed.indexOf(T_A) >= 0,
      `series=${R.seriesCount} removed=${JSON.stringify(R.removed)}`);
    const route2 = await cdp.evaluate(sessionId, `(() => {
      const c = window.MQTTMonitor.groupCardFor(${JSON.stringify(T_A)});
      return c ? c.topic : null;
    })()`);
    check('★ 删过的主题不再归这张卡管（从路由上就断了，不是只靠"不勾"）',
      route2 === null, String(route2));
    const side4 = await cdp.evaluate(sessionId, SIDEBAR_JS(T_A));
    check('侧栏里它不再是「已在分组卡里」（撤下来了）',
      side4 && side4.inGroup === false, JSON.stringify(side4));

    await pub(dev, T_A, JSON.stringify({ tag: '电池电流', address: 'DB1.DBW292', value: '8888' }));
    await sleep(900);
    R = await cdp.evaluate(sessionId, REPORT_JS(G));
    check('★ 删过之后再上报也不会被拉回卡片',
      R.seriesCount === 1 && R.removed.indexOf(T_A) >= 0,
      `series=${R.seriesCount} text=${JSON.stringify(R.text)}`);

    // ── 侧栏里的「取消上板 / 加回来」（不用打开卡片设置就能操作）──
    const sbOff = await cdp.evaluate(sessionId, SIDEBAR_JS(T_A));
    check('★ 侧栏把它显示成"已撤下"的样子（虚线 +，提示能加回分组卡）',
      sbOff && sbOff.offGroup === true && sbOff.btn === '+' && /加回分组卡/.test(sbOff.btnTitle),
      JSON.stringify(sbOff));
    const sbIn = await cdp.evaluate(sessionId, SIDEBAR_JS(T_V));
    check('★ 还在卡片上的主题则提示"点一下撤下来"',
      sbIn && sbIn.inGroup === true && sbIn.btn === '✓' && /撤下来/.test(sbIn.btnTitle),
      JSON.stringify(sbIn));
    await cdp.evaluate(sessionId, SIDEBAR_CLICK_JS(T_A));
    await sleep(1800);
    R = await cdp.evaluate(sessionId, REPORT_JS(G));
    const cardsAfterAddBack = await cdp.evaluate(sessionId, CARDS_JS);
    check('★ 侧栏点 + 直接加回分组卡（不会再冒一张单主题卡）',
      R.removed.length === 0 && R.seriesCount === 2
      && cardsAfterAddBack.groups.length === 1 && cardsAfterAddBack.topics.indexOf(T_A) < 0,
      `removed=${JSON.stringify(R.removed)} series=${R.seriesCount} cards=${JSON.stringify(cardsAfterAddBack.topics)}`);
    await cdp.evaluate(sessionId, SIDEBAR_CLICK_JS(T_A));
    await sleep(700);
    R = await cdp.evaluate(sessionId, REPORT_JS(G));
    check('★ 侧栏点 ✓ 就是取消上板（值、曲线一起从卡片上撤掉）',
      R.removed.indexOf(T_A) >= 0 && R.seriesCount === 1
      && !R.legendRows.some((r) => r.val === '8888'),
      `removed=${JSON.stringify(R.removed)} series=${R.seriesCount} rows=${JSON.stringify(R.legendRows)}`);

    // 把「撤下来」的样子拍下来：侧栏那一行是空心点 + 虚线 +，卡片上只剩电量的曲线
    await cdp.send('Page.captureScreenshot', {}, sessionId).then((r) => {
      fs.writeFileSync(offShotPath, Buffer.from(r.data, 'base64'));
    });
    check('取消上板的截图已保存',
      fs.existsSync(offShotPath) && fs.statSync(offShotPath).size > 10000,
      `${Math.round(fs.statSync(offShotPath).size / 1024)} KB → tools/_ui_group_offboard.png`);

    await cdp.evaluate(sessionId, `(() => {
      [...document.querySelectorAll('#mFieldPick .fp-restore')]
        .find((b) => b.dataset.topic === ${JSON.stringify(T_A)}).click();
      return 1;
    })()`);
    await sleep(1800);
    R = await cdp.evaluate(sessionId, REPORT_JS(G));
    check('★ 点「恢复」后它回到卡片，曲线也从库里补回来了',
      R.removed.length === 0
      && R.series.some((s) => String(s.key).indexOf(`__topic__:${T_A}|`) === 0)
      && R.series.length === 2,
      `removed=${JSON.stringify(R.removed)} series=${R.series.map((s) => s.key).join('  ')}`);
    MD = await cdp.evaluate(sessionId, MODAL_JS);
    check('恢复后弹窗底部那一栏也收起来了', MD.removedRows.length === 0, MD.removedHead);

    // 收尾：清掉勾选回到自动模式（后面几个阶段要按"全部成员都画"来断言）
    await cdp.evaluate(sessionId, `document.getElementById('mFieldClear').click()`);
    await sleep(150);
    await cdp.evaluate(sessionId, `document.getElementById('mSave').click()`);
    await sleep(1600);
    R = await cdp.evaluate(sessionId, REPORT_JS(G));
    check('收尾回到自动模式（picked 清空、两个主题都画）',
      R.picked.length === 0 && R.seriesCount === 2,
      `picked=${JSON.stringify(R.picked)} series=${R.seriesCount}`);

    // ── 阶段 C：超上限老实说「+N」 ──
    console.log('');
    console.log('▸ 阶段 C  前缀下主题多于上限时，老实说「+N」');
    const extra = ['T1', 'T2', 'T3', 'T4', 'T5'].map((s) => `${G}/${s}`);
    // ★ 必须走 broker 发，不能只往页面里灌 —— 阶段 D 要刷新页面，
    //   而刷新后成员清单只剩"内存里最后一条 + 落盘库里查得到的"。
    //   直接 ingestMessages 的话题服务端从没见过，刷新后凭空消失，
    //   会把"回填没生效"误报成 bug。
    for (const t of extra) {
      for (let i = 0; i < 5; i++) {
        await pub(dev, t, JSON.stringify({ tag: t, address: t, value: String(10 + i) }));
      }
    }
    await sleep(900);
    R = await cdp.evaluate(sessionId, REPORT_JS(G));
    check('一卡最多画 6 条曲线（上限生效，没有糊成一团）', R.seriesCount === 6, `series=${R.seriesCount}`);
    check('★ 画不下的成员用「+N」老实标出来', R.overflow === 1 && R.legendMore === '+1',
      `overflow=${R.overflow} more=${JSON.stringify(R.legendMore)}`);
    check('被画出来的都是前缀内的主题',
      R.series.every((s) => s.key.startsWith(`__topic__:${G}/`)),
      R.series.map((s) => s.key).join('  '));
    check('全部曲线都带 __topic__ 前缀', R.topicSeries === R.seriesCount);

    // 一台设备一张卡，最容易撞上的视觉坑就是"图例比卡片还高"：
    // 6 条曲线时图例要占两行，卡片一挤就出现"半截字"（第二行只露出下半个）。
    const L = await cdp.evaluate(sessionId, `(() => {
      const card = document.querySelector('.card[data-topic=' + JSON.stringify(${JSON.stringify(G)}) + ']');
      if (!card) return null;
      const c = card.getBoundingClientRect();
      const lg = card.querySelector('.card-legend');
      const l = lg.getBoundingClientRect();
      const lh = parseFloat(getComputedStyle(lg).lineHeight);
      const more = lg.querySelector('.lg-more');
      const foot = card.querySelector('.card-foot').getBoundingClientRect();
      const m = more ? more.getBoundingClientRect() : null;
      return {
        items: lg.querySelectorAll('.lg-item').length,
        rows: l.height / lh, lh, lgH: l.height,
        clipped: l.bottom > c.bottom + 0.5,
        moreText: more ? more.textContent : '',
        moreClipped: !!m && (m.bottom > c.bottom + 0.5 || m.height < 6),
        footOk: foot.height > 0 && foot.bottom <= c.bottom + 0.5,
      };
    })()`);
    check('图例没被卡片裁掉（六条曲线要占两行）', !!L && !L.clipped && L.items === 6,
      L ? `items=${L.items} 图例高 ${L.lgH.toFixed(1)}px` : 'null');
    check('★ 图例高度是整行倍数（不能出现"半截字"）',
      !!L && Math.abs(L.rows - Math.round(L.rows)) < 0.02,
      L ? `${L.lgH.toFixed(2)}px ÷ 行高 ${L.lh}px = ${L.rows.toFixed(2)} 行` : 'null');
    check('「+N」徽章完整可见（不许自己掉进被裁的那一行）',
      !!L && L.moreText === '+1' && !L.moreClipped, L ? JSON.stringify(L.moreText) : 'null');
    check('卡片脚部小字也还在（图例没把下面的挤没）', !!L && L.footOk);

    // 勾选也受「一张卡最多 MAX_SERIES 条」限制：第 7 个勾不上
    await cdp.evaluate(sessionId, OPEN_MODAL_JS(G));
    await sleep(300);
    const capRes = await cdp.evaluate(sessionId, `(() => {
      const cbs = [...document.querySelectorAll('#mFieldPick input.fp-topic')];
      let rejected = false;
      for (const cb of cbs) {
        if (cb.checked) continue;
        cb.checked = true;
        cb.dispatchEvent(new Event('change'));
        if (!cb.checked) rejected = true;   // 被上限挡下：勾选框自己弹回去了
      }
      return { total: cbs.length, on: cbs.filter((c) => c.checked).length, rejected };
    })()`);
    check('★ 主题勾选同样受「一张卡最多 6 条」限制（第 7 个勾不上）',
      capRes.total === 7 && capRes.on === 6 && capRes.rejected === true, JSON.stringify(capRes));
    await cdp.evaluate(sessionId, `document.getElementById('mCancel').click()`);
    await sleep(300);
    const afterCancel = await cdp.evaluate(sessionId, REPORT_JS(G));
    check('点「取消」不会把刚才乱勾的存进卡片（picked 仍为空）',
      afterCancel.picked.length === 0 && afterCancel.seriesCount === 6,
      `picked=${JSON.stringify(afterCancel.picked)} series=${afterCancel.seriesCount}`);

    // 趁分组卡还在（6 条曲线 + 「+1」），先把主截图拍下来 ——
    // 后缀阶段会把这张卡改成单主题卡，那时候再拍就看不到分组卡的样子了。
    await cdp.send('Page.captureScreenshot', {}, sessionId).then((r) => {
      fs.writeFileSync(shotPath, Buffer.from(r.data, 'base64'));
    });
    check('分组卡截图已保存', fs.existsSync(shotPath) && fs.statSync(shotPath).size > 10000,
      `${Math.round(fs.statSync(shotPath).size / 1024)} KB → tools/_ui_group.png`);

    // ── 阶段 D：刷新页面 —— 每个成员主题分别回填历史 ──
    console.log('');
    console.log('▸ 阶段 D  刷新页面：每个成员主题都要单独从库里回填');
    await cdp.evaluate(sessionId, `(() => { localStorage.setItem('mqtt-monitor-autoadd','0'); return 1; })()`);
    const beforeReload = R;
    await cdp.send('Page.navigate', { url: BASE }, sessionId);
    let ok = false;
    for (let i = 0; i < 60; i++) {
      await sleep(250);
      if (await cdp.evaluate(sessionId, `!!(window.MQTTMonitor && window.MQTTMonitor.state.cards.length)`)) { ok = true; break; }
    }
    check('刷新后页面重新起来了', ok);
    await sleep(2500);

    const R2 = await cdp.evaluate(sessionId, REPORT_JS(G));
    check('刷新后分组卡还在', !!R2 && R2.isGroupCard === true, R2 ? R2.topic : 'null');
    check('刷新后仍是每个主题一条曲线（不是并成一条）',
      R2.seriesCount === beforeReload.seriesCount, `${beforeReload.seriesCount} → ${R2.seriesCount}`);
    const v2 = (R2.series.find((s) => s.key.startsWith(`__topic__:${T_V}|`)) || {});
    const a2 = (R2.series.find((s) => s.key.startsWith(`__topic__:${T_A}|`)) || {});
    check('★ 电量那条从库里回填了（不是只剩 0~1 个点）', v2.n >= N, `n=${v2.n}`);
    check('★ 电流那条也回填了（一条主题的补数据没有顶掉另一条）', a2.n >= N, `n=${a2.n}`);
    // 电流上界要放到 9999：阶段 B3 故意给「没勾的电流」发过一个 9999，那条值已经进了库
    check('★ 回填后两条仍然各归各的（没有串）',
      v2.min >= 100 && v2.max <= 400 && a2.min >= 5000 && a2.max <= 9999,
      `电量 ${v2.min}~${v2.max} · 电流 ${a2.min}~${a2.max}`);
    check('回填的数据是按时间排好的', v2.tsLen === v2.n && a2.tsLen === a2.n);
    check('刷新后点位数还是 6（-2 个超上限的）', R2.seriesCount === 6, `series=${R2.seriesCount}`);
    check('刷新后「+N」提示还在', R2.overflow >= 1, `overflow=${R2.overflow}`);

    // ── 阶段 E：分组开关能取消，切回单主题卡 ──
    console.log('');
    console.log('▸ 阶段 E  取消分组 → 回到单主题卡');
    await cdp.evaluate(sessionId, OPEN_MODAL_JS(G));
    await sleep(300);
    await cdp.evaluate(sessionId, `(() => {
      const g = document.getElementById('mGroup');
      g.checked = false;
      g.dispatchEvent(new Event('change'));
      document.getElementById('mTopic').value = ${JSON.stringify(T_A)};
      return 1;
    })()`);
    await sleep(200);
    const M2 = await cdp.evaluate(sessionId, MODAL_JS);
    check('取消分组后回到单主题的勾选面板（不再是下拉）', M2.sels.length === 0,
      JSON.stringify({ sels: M2.sels.length, checks: M2.checks.length }));
    await cdp.evaluate(sessionId, `document.getElementById('mSave').click()`);
    // 取消分组后卡片要自己去库里把这一个主题的历史捞回来（不能干等设备下一条报文）
    await sleep(2200);
    const R3 = await cdp.evaluate(sessionId, REPORT_JS(T_A));
    check('★ 变成单主题卡，topic 是完整主题名', !!R3 && R3.isGroupCard === false && R3.topic === T_A,
      R3 ? `${R3.topic} group=${R3.group}` : 'null');
    check('单主题卡的曲线 key 回到"不带主题"的形态',
      R3.seriesCount >= 1 && R3.series.every((s) => !s.key.startsWith('__topic__:')),
      R3.series.map((s) => s.key).join('  '));
    const side2 = await cdp.evaluate(sessionId, SIDEBAR_JS(T_A));
    check('侧栏不再把它标成"在分组卡里"', side2 && side2.inGroup === false, JSON.stringify(side2));

    // ── 阶段 F：前缀边界（反向） ──
    console.log('');
    console.log('▸ 阶段 F  反向边界：plc2 的卡不收 plc/…');
    await cdp.evaluate(sessionId, `(() => {
      // 主题框里填通配符也是常事，createCard 必须把它归一成裸前缀
      window.MQTTMonitor.createCard({ topic: 'plc2/*', group: true, name: 'plc2 组' });
      return 1;
    })()`);
    await sleep(400);
    await cdp.evaluate(sessionId, `(() => {
      const base = Date.now();
      window.MQTTMonitor.ingestMessages([
        { topic: ${JSON.stringify(T_DECOY)}, ts: base, payload: '{"tag":"甲","value":"1"}',
          num: 1, json: { tag: '甲', value: '1' }, binary: false, qos: 0, retain: false, size: 20 },
        { topic: 'plc2/DB1.DBW999', ts: base, payload: '{"tag":"乙","value":"2"}',
          num: 2, json: { tag: '乙', value: '2' }, binary: false, qos: 0, retain: false, size: 20 },
        { topic: 'plc/DB1.DBW290', ts: base, payload: '{"tag":"丙","value":"3"}',
          num: 3, json: { tag: '丙', value: '3' }, binary: false, qos: 0, retain: false, size: 20 },
      ]);
      return 1;
    })()`);
    await sleep(600);
    const R2g = await cdp.evaluate(sessionId, REPORT_JS('plc2'));
    check('plc2 组卡建出来了', !!R2g && R2g.isGroupCard === true);
    check('★ 填 `plc2/*` 也被归一成裸前缀 plc2（否则一条曲线都匹配不上）',
      R2g.topic === 'plc2', R2g.topic);
    check('★ plc2 组只收 plc2/… （没收 plc/…）',
      R2g.series.length >= 1 && R2g.series.every((s) => s.key.startsWith('__topic__:plc2/')),
      R2g.series.map((s) => s.key).join('  '));

    // ── 阶段 I：同前缀自动合并 ──
    console.log('');
    console.log('▸ 阶段 G  同前缀自动合并（存量卡片自己并成一张）');
    // 现场最典型的形态：卡片是**收到报文才上板**的，plc/DB1.DBW290 先占了一张卡，
    // 等 plc/DB1.DBW292 也上报时就变成两张 —— 这里就照着造这个状态。
    await cdp.evaluate(sessionId, `(() => {
      const M = window.MQTTMonitor;
      for (const c of M.state.cards.slice()) M.removeCard(c);
      M.state.autoGroup = false;
      M.createCard({ topic: 'dev/DBW1', name: 'dev/DBW1' });
      M.createCard({ topic: 'dev/DBW2', name: 'dev/DBW2' });
      return M.state.cards.length;
    })()`);
    for (let i = 0; i < 5; i++) {
      await pub(dev, 'dev/DBW1', JSON.stringify({ tag: '甲', address: 'DBW1', value: String(1 + i) }));
      await pub(dev, 'dev/DBW2', JSON.stringify({ tag: '乙', address: 'DBW2', value: String(100 + i) }));
    }
    await sleep(900);
    let A = await cdp.evaluate(sessionId, CARDS_JS);
    check('关着开关：同前缀的两张单主题卡保持原样，不合并',
      A.n === 2 && A.topics.includes('dev/DBW1') && A.topics.includes('dev/DBW2'),
      JSON.stringify(A.topics));

    const mergedN = await cdp.evaluate(sessionId, `(async () => {
      window.MQTTMonitor.state.autoGroup = true;
      return window.MQTTMonitor.reconcileAutoGroups();
    })()`);
    await sleep(900);
    A = await cdp.evaluate(sessionId, CARDS_JS);
    check('★ 打开开关：两张卡自动合并成一张', mergedN === 2 && A.n === 1 && A.topics[0] === 'dev',
      `合并了 ${mergedN} 个主题，剩下 cards=${JSON.stringify(A.topics)}`);

    const RI = await cdp.evaluate(sessionId, REPORT_JS('dev'));
    check('合并出来的是一张分组卡（主题存前缀 dev）',
      !!RI && RI.isGroupCard === true && RI.topic === 'dev',
      RI ? `${RI.topic} group=${RI.group}` : 'null');
    check('★ 每个主题各一条曲线，key 里带着主题',
      RI.seriesCount === 2
      && RI.series.map((s) => s.key).sort().join(' ')
        === ['__topic__:dev/DBW1|__item__:甲', '__topic__:dev/DBW2|__item__:乙'].sort().join(' '),
      RI.series.map((s) => s.key).join('  '));
    check('★ 合并后两条曲线各归各的（1~5 vs 100~104，没串）', (() => {
      const a = RI.series.find((s) => s.key.startsWith('__topic__:dev/DBW1|')) || {};
      const b = RI.series.find((s) => s.key.startsWith('__topic__:dev/DBW2|')) || {};
      return a.min >= 1 && a.max <= 5 && b.min >= 100 && b.max <= 104;
    })(), JSON.stringify(RI.series.map((s) => `${s.key}:${s.min}~${s.max}`)));
    check('★ 合并后曲线不是空的（先去库里把各自的历史捞回来）',
      RI.series.every((s) => s.n >= 5), JSON.stringify(RI.series.map((s) => s.n)));
    check('合并后的副标题写明是前缀分组', /^dev\/\*/.test(RI.topicSub) && /2 个点位/.test(RI.topicSub),
      RI.topicSub);

    const side3 = await cdp.evaluate(sessionId, SIDEBAR_JS('dev/DBW1'));
    check('侧栏把被合并的主题标成「已在分组卡里」', side3 && side3.inGroup === true,
      JSON.stringify(side3));

    // 再来一台同前缀的新主题：不能再冒一张单卡，应当直接进这张卡
    await pub(dev, 'dev/DBW3', JSON.stringify({ tag: '丙', address: 'DBW3', value: '7' }));
    await sleep(800);
    A = await cdp.evaluate(sessionId, CARDS_JS);
    const RI3 = await cdp.evaluate(sessionId, REPORT_JS('dev'));
    check('★ 同前缀的新主题直接进这张卡，不再冒新的单卡',
      A.n === 1 && RI3.seriesCount === 3, `cards=${JSON.stringify(A.topics)} series=${RI3.seriesCount}`);

    // 开关接线：真的点侧栏那个复选框，而不是只改内存里的变量
    const sw = await cdp.evaluate(sessionId, `(() => {
      const el = document.getElementById('fAutoGroup');
      el.checked = false;
      el.dispatchEvent(new Event('change'));
      const off = localStorage.getItem('mqtt-monitor-autogroup');
      el.checked = true;
      el.dispatchEvent(new Event('change'));
      return { off, on: localStorage.getItem('mqtt-monitor-autogroup'), checked: el.checked };
    })()`);
    check('侧栏「同前缀自动合并」开关真的落到了 localStorage',
      sw.off === '0' && sw.on === '1' && sw.checked === true, JSON.stringify(sw));

    const offN = await cdp.evaluate(sessionId, `(async () => {
      const M = window.MQTTMonitor;
      M.state.autoGroup = false;
      for (const c of M.state.cards.slice()) M.removeCard(c);
      M.createCard({ topic: 'other/X' });
      M.createCard({ topic: 'other/Y' });
      return M.reconcileAutoGroups();     // 关着开关时调用它必须是彻底的空操作
    })()`);
    await sleep(300);
    const A2 = await cdp.evaluate(sessionId, CARDS_JS);
    check('★ 关掉开关后不再自动合并（同前缀两张卡各自保留）',
      offN === 0 && A2.n === 2, `merged=${offN} cards=${JSON.stringify(A2.topics)}`);

    // ── 阶段 G：截图 ──
    console.log('');
    console.log('▸ 阶段 H  收尾截图（取消分组 / 侧栏状态）');
    await cdp.send('Page.captureScreenshot', {}, sessionId).then((r) => {
      fs.writeFileSync(endShotPath, Buffer.from(r.data, 'base64'));
    });
    check('收尾截图已保存', fs.existsSync(endShotPath) && fs.statSync(endShotPath).size > 10000,
      `${Math.round(fs.statSync(endShotPath).size / 1024)} KB → tools/_ui_group_end.png`);

    // ── 阶段 H2：卡片超时无数据要有提示 ──
    // 用干净的隔离前缀，别把前面 plc 组的断言搅了。
    // 关键场景：分组卡里**只有一个点位**停发 —— 卡片级判定（只看最新一条）看不见，
    // 以前整张卡照显「正常」，那个点位拿 10 分钟前的旧值一直摆着，现场就是这么漏的。
    console.log('');
    console.log('▸ 阶段 H2  卡片超时无数据：分组卡里单个点位掉线也要提示');
    const SG = 'stale';
    const ST_A = 'stale/DB1.DBW400';   // 会"掉线"的那一个
    const ST_B = 'stale/DB1.DBW402';
    const ST_C = 'stale/DB1.DBW404';
    const TEN_MIN = 10 * 60 * 1000;

    /** 造一条报文：ts 自己给，好把某个点位"按回过去" */
    const mkRec = (topic, name, val, ts) => `{ topic: ${JSON.stringify(topic)}, ts: ${ts}, `
      + `payload: ${JSON.stringify(JSON.stringify({ tag: name, address: 'x', value: String(val), dataType: 'Int16' }))}, `
      + `num: ${val}, json: { tag: ${JSON.stringify(name)}, address: 'x', value: ${JSON.stringify(String(val))}, dataType: 'Int16' }, `
      + 'binary: false, qos: 0, retain: false, size: 40 }';

    const feedStale = (items) => cdp.evaluate(sessionId, `(() => {
      const base = Date.now();
      window.MQTTMonitor.ingestMessages([${items.map(([t, n, v, back]) => mkRec(t, n, v, `base - ${back}`)).join(',')}]);
      return 1;
    })()`);

    /** 读一次"无数据提示"的全貌（卡片标签 / 图例行 / 顶栏汇总） */
    const readStale = () => cdp.evaluate(sessionId, `(() => {
      const M = window.MQTTMonitor;
      const card = M.state.cards.find(c => c.topic === ${JSON.stringify(SG)});
      if (!card || !card.el) return null;
      const alertEl = document.getElementById('statAlert');
      const sum = M.staleSummary();
      return {
        stateText: card.el.state.textContent,
        stateCls: card.el.state.className,
        stateTitle: card.el.state.title,
        rootCls: card.el.root.className,
        bigText: card.el.value.textContent,     // 卡片大字
        text: card.text, num: card.num,
        cardThr: M.staleThreshold(card),        // 卡片级阈值（估不出节拍时必须是 null）
        chart: card._chart ? { yRef: card._chart.yRef, yMin: card._chart.yRefMin, yMax: card._chart.yRefMax } : null,
        members: M.groupMembers(card),
        staleTopics: M.staleTopics(card),
        rows: [...card.el.legend.querySelectorAll('.lg-item')].map(n => ({
          name: (n.querySelector('.lg-name') || {}).textContent,
          val: (n.querySelector('.lg-val') || {}).textContent,
          stale: n.classList.contains('is-stale'),
          title: n.getAttribute('title'),
        })),
        sumCards: sum.cards.map(c => c.topic),
        sumPartial: sum.partial.map(p => ({ topic: p.card.topic, topics: p.topics })),
        alertHidden: alertEl.hidden,
        alertText: alertEl.textContent,
        alertCls: alertEl.className,
        alertTitle: alertEl.title,
        toasts: [...document.querySelectorAll('#toastWrap .toast')].map(n => n.textContent),
      };
    })()`);

    /** 重置成一张干净的空组卡（每次子场景都从零开始，免得上一轮的曲线 ts 污染节拍估算） */
    const resetStaleCard = async () => {
      await cdp.evaluate(sessionId, `(() => {
        const M = window.MQTTMonitor;
        M.state.autoGroup = false;                  // 别让"自动合并"插手这张卡
        for (const c of M.state.cards.slice()) M.removeCard(c);
        M.createCard({ topic: ${JSON.stringify(SG)}, group: true, name: 'stale 组' });
        return M.state.cards.map(c => c.topic);
      })()`);
      await sleep(400);
    };

    // ★ 每个点位都要灌 3 条、间隔正常（默认 5 秒左右）：
    //   reportIntervalMs 取"点数最多那条曲线"的间隔中位数 —— 一个点位只灌一条会造出
    //   十几分钟的假间隔，阈值被抬到 5 分钟上限，"掉了 10 分钟"反而不算掉线（探针自坑）。
    const spark = (topic, name, v0, lastBack, step = 5000) => [
      [topic, name, v0, lastBack + step * 2],
      [topic, name, v0 + 1, lastBack + step],
      [topic, name, v0 + 2, lastBack],
    ];

    let RS = null;

    // ── 场景 0：节拍估不出来时**不许**判掉线 ──
    // 这是线上实测踩出来的误报：页面刚打开、历史还没回填完的那几十秒里卡片手上只有
    // 一两条报文，估不出上报节拍；旧实现拿 15 秒兜底当阈值，于是现场 60 秒节拍的设备
    // 每次上报后 15 秒就变「无数据」—— 8 张卡里有 4 张在闪。正确做法是"判不了就不判"。
    console.log('');
    console.log('▸ 场景 0  还估不出上报节拍时，不许把"判不了"当成"掉线"');
    await resetStaleCard();
    await feedStale([
      [ST_A, '掉线点', 11, TEN_MIN],     // 只有 1 条、而且很久以前 —— 但没有任何间隔可估
      [ST_B, '在线点', 22, TEN_MIN],
      [ST_C, '在线点3', 33, TEN_MIN],
    ]);
    await sleep(600);
    RS = await readStale();
    check('★ 一个间隔都估不出来 → 阈值是 null（判不了），不是拿 15 秒兜底',
      !!RS && RS.cardThr === null, `thr=${RS && RS.cardThr}`);
    check('★ 于是不判掉线：三个点位都不标掉线、卡片也不标无数据（宁可晚报也别误报）',
      !!RS && RS.staleTopics.length === 0 && RS.stateText === '正常'
        && !/(^| )is-stale( |$)/.test(RS.stateCls),
      `stale=${JSON.stringify(RS && RS.staleTopics)} state="${RS && RS.stateText}"`);

    // ── 场景 1b：同一张卡里快慢点位混着 —— 慢的那个不许被快节拍带跑 ──
    // `reportIntervalMs` 只取**点数最多**那条曲线，混合节拍时会偏向快的那条；
    // 若慢点位的阈值借了它，60 秒安静就会被判成掉线（这就是逐成员判定要避免的事）。
    console.log('');
    console.log('▸ 场景 1b  一张卡里既有 5 秒发一次的、也有 90 秒发一次的');
    await resetStaleCard();
    // 快点位给 6 条、慢点位给 3 条 —— 这样"点数最多那条曲线"一定是快点位的，
    // 卡片级节拍必然拿到 5 秒；慢点位若借了它，60 秒安静就会被误判成掉线。
    const slowPts = [];
    for (let i = 2; i >= 0; i -= 1) slowPts.push([ST_B, '慢点位', 20 + i, 60000 + i * 90000]);   // 90 秒一条
    const fastPts = [];
    for (let i = 5; i >= 0; i -= 1) fastPts.push([ST_A, '快点位', 10 + i, 2000 + i * 5000]);    // 5 秒一条
    // 顺序也要紧：慢的在前、快的在后（真实世界里快点位一直在发，卡片 lastTs 自然是最新的那条）
    await feedStale([...slowPts, ...fastPts]);
    await sleep(2400);
    RS = await readStale();
    const MIX = await cdp.evaluate(sessionId, `(() => {
      const M = window.MQTTMonitor;
      const c = M.state.cards.find((x) => x.topic === ${JSON.stringify(SG)});
      if (!c) return null;
      return {
        cardIv: M.reportIntervalMs(c),
        thrSlow: M.topicThreshold(c, ${JSON.stringify(ST_B)}),
        thrFast: M.topicThreshold(c, ${JSON.stringify(ST_A)}),
      };
    })()`);
    check('★ 卡片级节拍拿到的是快点位的 5 秒（点数最多那条曲线）',
      !!MIX && MIX.cardIv === 5000, `cardIv=${MIX && MIX.cardIv}`);
    check('  快点位 5 秒一条 → 阈值就是下限 15 秒',
      !!MIX && MIX.thrFast === 15000, `thrFast=${MIX && MIX.thrFast}`);
    check('★ 慢点位仍按它自己的 90 秒算到 270 秒（借卡片级只会给 15 秒）',
      !!MIX && MIX.thrSlow === 270000, `thrSlow=${MIX && MIX.thrSlow}`);
    check('★ 慢点位安静 60 秒 → 不判掉线（借卡片级节拍就会误报，这正是逐成员判定要避免的）',
      !!RS && RS.staleTopics.length === 0 && RS.stateText === '正常',
      `stale=${JSON.stringify(RS && RS.staleTopics)} state="${RS && RS.stateText}"`);

    // ── 场景 1：只有 A 掉线（卡片自己还在正常收数） ──
    await resetStaleCard();
    await feedStale([
      ...spark(ST_A, '掉线点', 11, TEN_MIN),
      ...spark(ST_B, '在线点', 22, 1000),
      ...spark(ST_C, '在线点3', 33, 500),
    ]);
    await sleep(2400);                              // 等一轮 2 秒兜底扫描（顶栏汇总与边沿提示都在那里跑）
    RS = await readStale();
    check('★ 分组卡里只有那个停发的点位被标成掉线（卡片级判定看不见的盲点）',
      !!RS && RS.staleTopics.length === 1 && RS.staleTopics[0] === ST_A,
      `members=${JSON.stringify(RS && RS.members)} stale=${JSON.stringify(RS && RS.staleTopics)}`);
    check('★ 卡片状态标签写明「无数据 1/3」，不再谎报正常',
      !!RS && /^无数据 1\/3$/.test(RS.stateText), `"${RS && RS.stateText}"`);
    check('★ 卡片标 is-stale，且"部分点位掉线"单独用 is-part 区分（红字，卡片自己还在收数）',
      !!RS && /(^| )is-stale( |$)/.test(RS.stateCls) && /(^| )is-part( |$)/.test(RS.stateCls),
      `cls="${RS && RS.stateCls}"`);
    check('★ 掉线点位的图例行撤成「—」（不许拿旧值继续摆着）+ 悬停说明停了多久',
      !!RS && (() => {
        const row = RS.rows.find((r) => r.name === '掉线点');
        return row && row.stale === true && row.val === '—' && /没有新数据/.test(row.title || '');
      })(), JSON.stringify(RS && RS.rows));
    check('★ 还在发的两个点位照常见值与曲线（没被误伤）',
      !!RS && RS.rows.filter((r) => !r.stale && r.val !== '—').length === 2,
      JSON.stringify(RS && RS.rows));
    // 大字最显眼，掉线点位的旧值摆在这儿看着就像"这张卡还活着"
    check('★ 卡片大字不再拿掉线点位的旧值（13）当当前值，改挑一条还在发的（24）',
      !!RS && RS.num === 24 && RS.bigText === '24',
      `big="${RS && RS.bigText}" num=${RS && RS.num}`);
    check('★ Y 轴刻度也跟着走：不再停在掉线那条曲线的量程上（否则大字与刻度对不上）',
      !!RS && !!RS.chart && /DB1\.DBW40[24]/.test(String(RS.chart.yRef)) && !/DBW400/.test(String(RS.chart.yRef)),
      JSON.stringify(RS && RS.chart));
    check('★ 顶栏冒出汇总「1 张卡有 1 个点位掉线」，且此时不算整卡静默（不标红）',
      !!RS && RS.alertHidden === false && /1 张卡有 1 个点位掉线/.test(RS.alertText)
        && !/is-danger/.test(RS.alertCls),
      `hidden=${RS && RS.alertHidden} text="${RS && RS.alertText}" cls="${RS && RS.alertCls}"`);
    check('★ 汇总的悬停明细点得出是哪几个点位',
      !!RS && /点击跳到第一张/.test(RS.alertTitle || ''), `title="${RS && RS.alertTitle}"`);
    check('★ 掉线是"边沿提示一次"：冒过一条 toast（不常驻刷屏）',
      !!RS && RS.toasts.some((t) => /掉线了/.test(t)), JSON.stringify(RS && RS.toasts));
    check('  汇总说得出"部分掉线"而不是只报整卡（避免漏掉这类）',
      !!RS && RS.sumPartial.length === 1 && RS.sumPartial[0].topics.length === 1
        && RS.sumCards.length === 0,
      JSON.stringify({ cards: RS && RS.sumCards, partial: RS && RS.sumPartial }));

    await cdp.send('Page.captureScreenshot', {}, sessionId).then((r) => {
      fs.writeFileSync(staleShotPath, Buffer.from(r.data, 'base64'));
    });
    check('  掉线提示截图已保存（要亲眼看一眼：值是不是真的显示成「—」）',
      fs.existsSync(staleShotPath) && fs.statSync(staleShotPath).size > 10000,
      `${Math.round(fs.statSync(staleShotPath).size / 1024)} KB → tools/_ui_group_stale.png`);

    // ── 场景 2：三个点位全停发 → 整卡静默 ──
    await resetStaleCard();
    await feedStale([
      ...spark(ST_A, '掉线点', 11, TEN_MIN + 20000),
      ...spark(ST_B, '在线点', 22, TEN_MIN + 10000),
      ...spark(ST_C, '在线点3', 33, TEN_MIN),
    ]);
    await sleep(2400);
    RS = await readStale();
    check('★ 三个点位全停发 → 标签变「无数据 3/3」',
      !!RS && /^无数据 3\/3$/.test(RS.stateText), `"${RS && RS.stateText}"`);
    check('★ 整卡静默时不再用 is-part（那是"卡片还在收数"的标记）',
      !!RS && !/(^| )is-part( |$)/.test(RS.stateCls), `cls="${RS && RS.stateCls}"`);
    check('★ 整卡静默时大字老实显示「—」（不留旧值装活）',
      !!RS && RS.bigText === '—' && RS.num === null,
      `big="${RS && RS.bigText}" num=${RS && RS.num}`);
    check('★ 卡片整体也标上 is-stale（左边条转灰，满屏卡片里沉下去）',
      !!RS && /(^| )is-stale( |$)/.test(RS.rootCls), `cls="${RS && RS.rootCls}"`);
    check('★ 顶栏汇总升级成「1 张卡片无数据」并标红',
      !!RS && /1 张卡片无数据/.test(RS.alertText) && /is-danger/.test(RS.alertCls),
      `text="${RS && RS.alertText}" cls="${RS && RS.alertCls}"`);
    check('★ 整卡静默也边沿提示一次（说的是"整卡"）',
      !!RS && RS.toasts.some((t) => /整卡已无新数据/.test(t)), JSON.stringify(RS && RS.toasts));

    // ── 场景 3：数据回来了，提示要自己收干净 ──
    await feedStale([
      ...spark(ST_A, '掉线点', 44, 1000),
      ...spark(ST_B, '在线点', 55, 900),
      ...spark(ST_C, '在线点3', 66, 800),
    ]);
    await sleep(2400);
    RS = await readStale();
    check('★ 数据回来了：标签回到「正常」',
      !!RS && RS.stateText === '正常', `"${RS && RS.stateText}"`);
    check('★ 恢复后没残余的 is-stale / is-part',
      !!RS && !/(^| )is-stale( |$)/.test(RS.stateCls) && !/(^| )is-part( |$)/.test(RS.stateCls),
      `state="${RS && RS.stateCls}" root="${RS && RS.rootCls}"`);
    check('★ 恢复后顶栏汇总自动收起（不留常驻面板）',
      !!RS && RS.alertHidden === true && RS.staleTopics.length === 0,
      `hidden=${RS && RS.alertHidden} stale=${JSON.stringify(RS && RS.staleTopics)}`);
    check('  恢复也边沿提示一次（说了"恢复收数"）',
      !!RS && RS.toasts.some((t) => /恢复收数/.test(t)), JSON.stringify(RS && RS.toasts));

    // ── 阶段 I：运行时异常 ──
    console.log('');
    console.log('▸ 阶段 I  运行时异常');
    const exc = cdp.exceptions.filter((e) => !/ResizeObserver loop/.test(e));
    check('页面没有抛未捕获异常', exc.length === 0, exc.slice(0, 3).join(' | '));
    const errs = cdp.consoles.filter((c) => c.type === 'error');
    check('没有 console.error', errs.length === 0, errs.slice(0, 2).map((e) => e.text).join(' | '));

    const unused = await cdp.evaluate(sessionId, GROUP_TOPICS_JS);
    console.log(`    （平台已知主题：${unused.join(', ')}）`);
    await pub(dev, T_V, payloadV(999)).catch(() => {});
    await sleep(200);
    await new Promise((r) => dev.end(false, {}, r));
  } catch (err) {
    fail++;
    exitCode = 1;
    console.log('');
    console.log(`  FAIL  脚本执行中断：${err.message}`);
    if (process.env.GROUP_VERBOSE) console.log(err.stack);
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
  console.log(`       ${pickerShotPath}`);
  console.log(`       ${offShotPath}`);
  console.log(`       ${staleShotPath}`);
  console.log(`       ${endShotPath}`);
  console.log('──────────────────────────────────────────');
  if (fail > 0) exitCode = 1;
  process.exit(exitCode);
}

main();

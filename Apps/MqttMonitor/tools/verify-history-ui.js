'use strict';

/**
 * 「历史数据」面板的前端验证（真实浏览器，无头 Edge + CDP）
 *
 * 目的：后端接口全过了不代表界面能用 —— 改过 index.html / app.js / style.css 之后，
 * 一个 JS 语法错就能让整页白屏。这里在真实 Edge 里打开页面，
 * 断言面板存在、统计填充、按钮可点、改保留天数真的写进配置，并顺手截一张图看视觉。
 *
 * 全程用临时目录，不动正式实例的 config.json / data。
 * 用法：node tools/verify-history-ui.js
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
const WEB_PORT = 8092;
const BROKER_PORT = 18832;
const CDP_PORT = 9334;
const BASE = `http://127.0.0.1:${WEB_PORT}`;
const VIEW = { width: 1500, height: 900 };

const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'mqttmon-ui-'));
const cfgPath = path.join(tmp, 'config.json');
// 两张卡各带一个主题（要断言"清空历史后看板卡片保留"）：关掉同前缀自动合并，
// 否则 plc/DB1.DBW230 和 plc/DB1.DBW240 会被并成一张（见 verify-group.js）。
fs.writeFileSync(cfgPath, JSON.stringify({ ui: { autoGroup: false } }), 'utf8');
const dataDir = path.join(tmp, 'data');
const profileDir = path.join(tmp, 'edgeprofile');
const shotPath = path.join(ROOT, 'tools', '_ui_history.png');

let pass = 0;
let fail = 0;
function check(name, ok, detail) {
  ok ? pass++ : fail++;
  console.log(`${ok ? '  PASS' : '  FAIL'}  ${name}${detail ? `   [${detail}]` : ''}`);
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

async function publishFromDevice(topic, n, startVal) {
  const client = mqtt.connect(`mqtt://127.0.0.1:${BROKER_PORT}`, {
    clientId: `ui-device-${Date.now()}-${Math.random().toString(16).slice(2, 6)}`,
    clean: true, reconnectPeriod: 0, connectTimeout: 5000,
  });
  await new Promise((resolve, reject) => {
    const t = setTimeout(() => reject(new Error('连接 broker 超时')), 6000);
    client.once('connect', () => { clearTimeout(t); resolve(); });
    client.once('error', (e) => { clearTimeout(t); reject(e); });
  });
  for (let i = 0; i < n; i++) {
    await new Promise((res) => client.publish(topic, JSON.stringify({
      tag: 'DB1.DBW230', address: 'DB1.DBW230', description: '小车状态',
      dataType: 'Int16', value: String(startVal + i * 3), ts: new Date().toISOString(),
    }), { qos: 0 }, res));
    await sleep(30);
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
      // 给探针用：在页面里求值并把结果带回来
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
 * Edge 会 fork 出一堆子进程，只 kills 主进程的话它们仍然活着并占着 profile 目录，
 * 后续 rmSync 就会卡在文件锁上 —— 表现为脚本跑完不退出（被外层超时 SIGTERM）。
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

async function main() {
  console.log('');
  console.log('══ 「历史数据」面板前端验证（无头 Edge + CDP）══');
  console.log(`   临时目录 ${tmp}`);
  console.log('');

  if (!fs.existsSync(EDGE)) { console.log(`  未找到 Edge：${EDGE}`); fail++; return; }

  // ── 起临时平台并灌点数据（面板要有内容才看得出对不对）──
  console.log('▸ 启动临时平台');
  const app = startPlatform();
  await fetchOk(`${BASE}/api/state`);
  await publishFromDevice('plc/DB1.DBW230', 6, 100);
  await publishFromDevice('plc/DB1.DBW240', 2, 3);
  await sleep(400);
  const st = (await (await fetch(`${BASE}/api/history/stats`)).json()).data;
  check('临时平台已落盘 8 条', st?.rows === 8, `rows=${st?.rows}`);

  // ── 起无头 Edge ──
  console.log('');
  console.log('▸ 启动无头 Edge');
  fs.mkdirSync(profileDir, { recursive: true });
  const edge = spawn(EDGE, [
    '--headless=new', '--disable-gpu', '--no-sandbox', '--hide-scrollbars', '--no-first-run',
    `--remote-debugging-port=${CDP_PORT}`, `--user-data-dir=${profileDir}`,
    `--window-size=${VIEW.width},${VIEW.height}`, 'about:blank',
  ], { stdio: 'ignore', windowsHide: true });

  let cdp;
  try {
    const verRes = await fetchOk(`http://127.0.0.1:${CDP_PORT}/json/version`);
    const ver = await verRes.json();
    cdp = await cdpConnect(ver.webSocketDebuggerUrl);
    check('CDP 已连上浏览器', true, ver.Browser);

    const { targetId } = await cdp.send('Target.createTarget', { url: 'about:blank' });
    const { sessionId } = await cdp.send('Target.attachToTarget', { targetId, flatten: true });
    await cdp.send('Page.enable', null, sessionId);
    await cdp.send('Runtime.enable', null, sessionId);
    await cdp.send('Emulation.setDeviceMetricsOverride', {
      width: VIEW.width, height: VIEW.height, deviceScaleFactor: 1, mobile: false,
    }, sessionId);

    await cdp.send('Page.navigate', { url: `http://127.0.0.1:${WEB_PORT}/` }, sessionId);
    await sleep(3500);   // 真实等待：等 WebSocket 首帧 + fetch 全部落地

    // ── 断言 1：页面骨架与控制台干净 ──
    console.log('');
    console.log('▸ 断言：面板与控制台');
    const base = await cdp.evaluate(sessionId, `(() => {
      const q = (s) => document.querySelector(s);
      const el = (id) => document.getElementById(id);
      return {
        title: document.title,
        readyState: document.readyState,
        hasDebugHandle: !!(window.MQTTMonitor && window.MQTTMonitor.state),
        panelExists: !!q('.panel[data-name="history"]'),
        badge: (el('histBadge') || {}).textContent,
        dbPath: (el('fHistPath') || {}).value,
        keepDays: (el('fKeepDays') || {}).value,
        meta: (el('histMeta') || {}).textContent,
        buttons: ['btnKeepDays','btnHistExport','btnHistClear'].map((i) => !!el(i)),
        stateHistory: window.MQTTMonitor.state.history || null,
      };
    })()`);

    check('页面加载完成', base.readyState === 'complete', base.readyState);
    check('前端调试句柄就绪（说明 app.js 无致命错误）', base.hasDebugHandle);
    check('「历史数据」面板存在', base.panelExists);
    check('三个按钮都在', base.buttons.every(Boolean), JSON.stringify(base.buttons));

    // ── 断言 2：/api/state 的 historyState 已渲染到界面 ──
    check('徽标显示落盘条数', /8\s*条/.test(base.badge || ''), `badge="${base.badge}"`);
    check('库文件路径已填入', /history\.db$/.test(base.dbPath || ''), base.dbPath);
    check('保留天数回填为 7', base.keepDays === '7', `keepDays=${base.keepDays}`);
    check('统计行含占用与保留天数', /占用/.test(base.meta || '') && /保留 7 天/.test(base.meta || ''),
      (base.meta || '').slice(0, 70));

    // ── 断言 3：展开面板会重新拉一次统计 ──
    console.log('');
    console.log('▸ 断言：展开面板触发刷新');
    // 先把界面上的统计痕迹清掉，再点展开 —— 这样才是真的在验证「点击→拉取→填充」
    await cdp.evaluate(sessionId, `(() => {
      const p = document.querySelector('.panel[data-name="history"]');
      p.dataset.open = 'false';
      document.getElementById('histBadge').textContent = '—';
      window.MQTTMonitor.state.history = null;
      p.querySelector('[data-toggle]').click();
      return true;
    })()`);
    await sleep(1200);
    const opened = await cdp.evaluate(sessionId, `(() => {
      const p = document.querySelector('.panel[data-name="history"]');
      const rect = p.getBoundingClientRect();
      const body = p.querySelector('.panel-body');
      return {
        open: p.dataset.open,
        visible: rect.height > 60 && rect.width > 40,
        rowsFetched: (window.MQTTMonitor.state.history || {}).rows,
        badge: document.getElementById('histBadge').textContent,
        bodyVisible: body.offsetHeight > 40,
        bodyText: body.innerText.replace(/\\s+/g, ' ').trim(),
      };
    })()`);
    check('点击标题后面板展开', opened.open === 'true', `open=${opened.open}`);
    check('面板内容可见（有实际高度）', opened.visible && opened.bodyVisible,
      `visible=${opened.visible}, bodyHeight>40=${opened.bodyVisible}`);
    check('展开后重新拉到统计并回填', opened.rowsFetched === 8 && /8\s*条/.test(opened.badge || ''),
      `rows=${opened.rowsFetched}, badge="${opened.badge}"`);
    check('面板文案完整', /保存天数/.test(opened.bodyText) && /导出 CSV/.test(opened.bodyText)
      && /清空历史/.test(opened.bodyText));

    // ── 断言 4：改保留天数 → 真的写进配置文件 ──
    console.log('');
    console.log('▸ 断言：保存保留天数');
    await cdp.evaluate(sessionId, `(async () => {
      const inp = document.getElementById('fKeepDays');
      inp.value = '3';
      document.getElementById('btnKeepDays').click();
      await new Promise((r) => setTimeout(r, 300));
      const p = document.querySelector('.panel[data-name="history"]');
      p.scrollIntoView({ block: 'center' });
      return true;
    })()`);
    await sleep(1200);
    const afterSave = await cdp.evaluate(sessionId, `(() => {
      const el = (id) => document.getElementById(id);
      return {
        keepDays: (window.MQTTMonitor.state.history || {}).keepDays,
        meta: el('histMeta').textContent,
        toast: (document.getElementById('toast') || {}).textContent || '',
        visible: document.querySelector('.panel[data-name="history"]').getBoundingClientRect().height > 60,
      };
    })()`);
    check('前端状态已更新为 3 天', afterSave.keepDays === 3, `keepDays=${afterSave.keepDays}`);
    check('统计行显示「保留 3 天」', /保留 3 天/.test(afterSave.meta || ''), (afterSave.meta || '').slice(0, 60));
    const savedCfg = JSON.parse(fs.readFileSync(cfgPath, 'utf8'));
    check('已写入配置文件', savedCfg.history?.keepDays === 3, `config.history.keepDays=${savedCfg.history?.keepDays}`);

    // ── 截图看一眼视觉 ──
    console.log('');
    console.log('▸ 截图');
    const shotOverview = path.join(ROOT, 'tools', '_ui_overview.png');
    // 第一张：全部滚动容器归零，看整体版面
    await cdp.evaluate(sessionId, `(async () => {
      document.documentElement.scrollTop = 0;
      document.body.scrollTop = 0;
      for (const el of document.querySelectorAll('*')) if (el.scrollTop) el.scrollTop = 0;
      await new Promise((r) => setTimeout(r, 350));
      return true;
    })()`);
    const shot0 = await cdp.send('Page.captureScreenshot', { format: 'png' }, sessionId);
    fs.writeFileSync(shotOverview, Buffer.from(shot0.data, 'base64'));
    // 第二张：把历史面板带进视野
    await cdp.evaluate(sessionId, `(async () => {
      document.querySelector('.panel[data-name="history"]').scrollIntoView({ block: 'center' });
      await new Promise((r) => setTimeout(r, 350));
      return true;
    })()`);
    const shot = await cdp.send('Page.captureScreenshot', { format: 'png' }, sessionId);
    fs.writeFileSync(shotPath, Buffer.from(shot.data, 'base64'));
    check('已生成截图', fs.existsSync(shotPath) && fs.existsSync(shotOverview),
      `全景 ${(fs.statSync(shotOverview).size / 1024).toFixed(0)} KB / 面板 ${(fs.statSync(shotPath).size / 1024).toFixed(0)} KB`);

    // ── 断言 5：控制台无异常 ──
    console.log('');
    console.log('▸ 断言：控制台');
    const errs = cdp.consoles.filter((c) => c.type === 'error');
    check('无未捕获 JS 异常', cdp.exceptions.length === 0,
      cdp.exceptions.length ? cdp.exceptions.join(' | ').slice(0, 300) : '0 条');
    check('无 console.error', errs.length === 0,
      errs.length ? errs.map((e) => e.text).join(' | ').slice(0, 300) : '0 条');
  } finally {
    try { cdp?.close(); } catch { /* ignore */ }
    killTree(edge);
    killTree(app.child);
    await sleep(900);
    // 临时目录在系统 tmp 下，晚一点被系统回收也无所谓；这里不阻塞退出，
    // 免得 Edge 还没完全释放文件锁时 rmSync 卡住整个脚本
    fs.rm(profileDir, { recursive: true, force: true, maxRetries: 2, retryDelay: 200 }, () => {});
    fs.rm(tmp, { recursive: true, force: true, maxRetries: 2, retryDelay: 200 }, () => {});
  }
}

main()
  .catch((err) => { console.error('\n验证异常：', err); fail++; })
  .finally(() => {
    console.log('');
    console.log('════════════════════════════════════════');
    console.log(`  结果：${pass} 项通过，${fail} 项失败`);
    console.log('════════════════════════════════════════');
    console.log('');
    process.exit(fail ? 1 : 0);
  });

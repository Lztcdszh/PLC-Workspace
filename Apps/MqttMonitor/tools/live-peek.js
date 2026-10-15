/**
 * 只读看线上看板现状 —— 合成数据的自检证明不了"真数据长什么样"，这个补上。
 *   - 用独立的 user-data-dir 起一个无头 Edge 打开 http://127.0.0.1:8090
 *   - **拦掉 `/api/cards`**：这是页面唯一会写用户 config 的接口（「自动上板」会用它），
 *     本脚本绝不碰现场配置
 *   - 等回填落定后只读页面状态 + 截图，退出时清干净
 *
 * 用法：
 *   node tools/live-peek.js [截图路径]            # 默认 tools/_ui_live.png
 *   PEEK_WAIT_MS=16000 node tools/live-peek.js    # 故意只等十几秒，复现"刚打开还没回填完"的时序
 */
const { spawn } = require('child_process');
const fs = require('fs');
const os = require('os');
const path = require('path');
const WebSocket = require('ws');

const EDGE = 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';
const CDP_PORT = 9341;
const URL = 'http://127.0.0.1:8090/';
const SHOT = process.argv[2] || path.join(__dirname, '_ui_live.png');

const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'mqttmon-peek-'));
const profile = path.join(tmp, 'edge');
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

let edge = null;
let cdp = null;

function killTree(child) {
  if (!child) return;
  try {
    spawn('taskkill', ['/F', '/T', '/PID', String(child.pid)], { stdio: 'ignore' });
  } catch { /* ignore */ }
}

function cdpConnect(wsUrl) {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(wsUrl, { maxPayload: 256 * 1024 * 1024 });
    let msgId = 0;
    const pending = new Map();
    const exceptions = [];
    const consoles = [];
    const blocked = [];
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
        exceptions.push(m.params?.exceptionDetails?.exception?.description || m.params?.exceptionDetails?.text || '?');
      } else if (m.method === 'Runtime.consoleAPICalled') {
        consoles.push({ type: m.params?.type, text: (m.params?.args || []).map((a) => a.value ?? a.description ?? '').join(' ') });
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
        if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description || '求值失败');
        return r.result?.value;
      };
      resolve({ send, evaluate, exceptions, consoles, blocked, close: () => ws.close() });
    });
  });
}

async function main() {
  edge = spawn(EDGE, [
    '--headless=new',
    `--remote-debugging-port=${CDP_PORT}`,
    `--user-data-dir=${profile}`,
    '--no-first-run', '--no-default-browser-check', '--disable-gpu',
    '--window-size=1500,900',
    'about:blank',
  ], { stdio: 'ignore' });

  let wsUrl = null;
  for (let i = 0; i < 40 && !wsUrl; i += 1) {
    await sleep(300);
    try {
      const r = await fetch(`http://127.0.0.1:${CDP_PORT}/json/version`);
      wsUrl = (await r.json()).webSocketDebuggerUrl;
    } catch { /* 还没起来 */ }
  }
  if (!wsUrl) throw new Error('Edge 没起来（CDP 连不上）');

  cdp = await cdpConnect(wsUrl);
  const { targetId } = await cdp.send('Target.createTarget', { url: 'about:blank' });
  const { sessionId } = await cdp.send('Target.attachToTarget', { targetId, flatten: true });

  await cdp.send('Page.enable', {}, sessionId);
  await cdp.send('Runtime.enable', {}, sessionId);

  // ★ 红线：不许写现场 config。页面只有 `/api/cards` 这一个写配置的接口。
  await cdp.send('Network.enable', {}, sessionId);
  await cdp.send('Network.setBlockedURLs', { urls: ['*/api/cards*'] }, sessionId);

  await cdp.send('Page.navigate', { url: URL }, sessionId);

  // 现场窗口是 7 天，8 张卡各 4 个主题的历史回填要一会儿 —— 等它落定再读，
  // 否则读到的全是"刚打开、还没补历史"的中间态（大字全是「—」、也不会判离线）。
  const settle = `(() => {
    const M = window.MQTTMonitor;
    const cards = M.state.cards;
    if (!cards.length) return { done: false, waiting: '还没有卡片' };
    // 每张卡至少有一条曲线拿到了数据 = 回填落定（还没落的列出来）
    const waiting = cards.filter((c) => !(c.series || []).some((s) => s.data && s.data.length)).map((c) => c.topic);
    return { done: waiting.length === 0, waiting: waiting.join(',') };
  })()`;
  const t0 = Date.now();
  const budget = Number(process.env.PEEK_WAIT_MS || 75000);   // 想复现"刚打开、还没回填完"就调小它
  let last = null;
  while (Date.now() - t0 < budget) {
    await sleep(2500);
    last = await cdp.evaluate(sessionId, settle);
    if (last.done) break;
  }
  console.log(`回填等待 ${Math.round((Date.now() - t0) / 1000)} 秒  ${last && last.done ? '(已落定)' : `(超时，仍在等：${last && last.waiting})`}`);
  await sleep(2600);   // 再留一轮 2 秒兜底扫描（离线判定与顶栏汇总在那里跑）

  const S = await cdp.evaluate(sessionId, `(() => {
    const M = window.MQTTMonitor;
    const alertEl = document.getElementById('statAlert');
    const sum = M.staleSummary();
    return {
      cards: M.state.cards.map((c) => ({
        topic: c.topic, group: !!c.group,
        members: c.group ? M.groupMembers(c).length : 0,
        stale: M.isStale(c),
        gone: c.group ? M.staleTopics(c).map((t) => t + '（' + Math.round(M.topicSilenceMs(c, t) / 1000) + 's）') : [],
        stateText: c.el ? c.el.state.textContent : null,
        stateCls: c.el ? c.el.state.className : null,
        big: c.el ? c.el.value.textContent : null,
        legend: c.el ? [...c.el.legend.querySelectorAll('.lg-item')].map((n) => {
          const q = (s) => (n.querySelector(s) || {}).textContent;
          return q('.lg-name') + '=' + q('.lg-val') + (n.classList.contains('is-stale') ? '(掉线)' : '');
        }) : [],
        chart: c._chart ? { yRef: c._chart.yRef, min: c._chart.yRefMin, max: c._chart.yRefMax } : null,
        cardIv: M.reportIntervalMs(c),
        cardThr: M.staleThreshold(c),
        // ⚠️ 这段是 Node 的模板字符串：页面里要写的插值会被 Node 抢先求值，所以用拼接
        series: (c.series || []).map(function (s) { return String(s.key).slice(0, 46) + '=' + s.data.length; }),
        perTopic: c.group ? [...(c.seenByTopic || new Map()).entries()].map(([t, e]) => ({
          t,
          n: e.count,
          gaps: (e.gaps || []).slice(-8),
          thr: M.topicSilenceMs(c, t) > 0 ? undefined : undefined,
          quiet: Math.round(M.topicSilenceMs(c, t) / 1000),
        })) : [],
      })),
      sumCards: sum.cards.map((c) => c.topic),
      sumPartial: sum.partial.map((p) => p.card.topic + ':' + p.topics.length),
      alertHidden: alertEl.hidden, alertText: alertEl.textContent, alertCls: alertEl.className,
      toasts: [...document.querySelectorAll('#toastWrap .toast')].map((n) => n.textContent),
      topics: M.state.topics.size,
    };
  })()`);

  console.log('主题数:', S.topics);
  console.log('顶栏汇总:', S.alertHidden ? '(隐藏)' : `${S.alertText}  [${S.alertCls}]`);
  console.log('toast:', JSON.stringify(S.toasts));
  console.log('');
  for (const c of S.cards) {
    console.log(`${c.group ? '[组]' : '[单]'} ${c.topic}  状态="${c.stateText}" 大字=${c.big} 成员=${c.members}`
      + (c.stale ? '  ★整卡无数据' : '')
      + (c.gone.length ? `  掉线点位=${JSON.stringify(c.gone)}` : ''));
    if (c.legend.length) console.log('      图例: ' + c.legend.join(' | '));
    console.log(`      卡片节拍=${c.cardIv} ms  卡片阈值=${c.cardThr} ms`);
    if (c.series.length) console.log('      曲线点数: ' + c.series.join(' | '));
    for (const p of c.perTopic) {
      console.log(`        主题 ${p.t}  ${p.n} 条  安静 ${p.quiet}s  最近间隔=${JSON.stringify(p.gaps)}`);
    }
    if (c.chart && c.chart.yRef) console.log(`      Y轴归属: ${c.chart.yRef}  ${JSON.stringify([c.chart.min, c.chart.max])}`);
  }

  const shot = await cdp.send('Page.captureScreenshot', {}, sessionId);
  fs.writeFileSync(SHOT, Buffer.from(shot.data, 'base64'));
  console.log('');
  console.log(`截图 ${SHOT}  (${Math.round(fs.statSync(SHOT).size / 1024)} KB)`);

  const exc = cdp.exceptions.filter((e) => !/ResizeObserver/.test(e));
  console.log('页面异常:', exc.length ? exc.slice(0, 3) : '无');
  console.log('console.error:', cdp.consoles.filter((c) => c.type === 'error').map((c) => c.text).slice(0, 3).join(' | ') || '无');
  console.log('（本脚本拦掉了 /api/cards，未改动现场配置）');
}

main()
  .catch((e) => { console.log('出错了：', e.message); process.exitCode = 1; })
  .finally(async () => {
    try { cdp && cdp.close(); } catch { /* ignore */ }
    killTree(edge);
    await sleep(600);
    try { fs.rmSync(tmp, { recursive: true, force: true }); } catch { /* ignore */ }
  });

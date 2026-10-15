'use strict';

/**
 * MQTT 实时数据监视平台 —— 服务端入口
 *
 * 平台自带 MQTT Broker（默认监听 1883），设备把 MQTT 地址指过来即可被监视；
 * 同时也能作为客户端连接外部 broker。两种模式并存，随时切换。
 *
 * 用法：
 *   node server.js                    启动平台（Web 端口 8090，Broker 端口 1883）
 *   node server.js --port 9000        指定 Web 端口
 *   node server.js --broker-port 1884 指定内置 Broker 端口
 *   node server.js --no-broker        不启动内置 Broker（仅作为客户端连外部 broker）
 *   node server.js --no-sim           内置 Broker 不带数据模拟器
 *   node server.js --no-open          启动后不自动打开浏览器
 *   node server.js --demo             演示模式：内置 Broker + 模拟器 + 自动接入
 *
 * 首次运行（没有 config.json）会自动开内置 Broker、开模拟器并接入，
 * 保证打开页面就有数据可看；之后按 config.json 里保存的配置运行。
 */

const os = require('os');
const fs = require('fs');
const path = require('path');
const { exec } = require('child_process');

const { MqttBridge } = require('./src/mqttBridge');
const { WsHub } = require('./src/wsHub');
const { EmbeddedBroker } = require('./src/embeddedBroker');
const { createServer } = require('./src/staticServer');
const { HistoryStore } = require('./src/historyStore');
const configStore = require('./src/configStore');

// ---------------- 命令行参数 ----------------
const argv = process.argv.slice(2);
const getArg = (name, fallback) => {
  const idx = argv.indexOf(`--${name}`);
  return idx >= 0 && argv[idx + 1] && !argv[idx + 1].startsWith('--') ? argv[idx + 1] : fallback;
};
const hasFlag = (name) => argv.includes(`--${name}`);

const PORT = Number(getArg('port', process.env.PORT || 8090));
const HOST = getArg('host', '0.0.0.0');
const BROKER_PORT = getArg('broker-port', null);
const FORCE_DEMO = hasFlag('demo');
const NO_BROKER = hasFlag('no-broker');
const NO_SIM = hasFlag('no-sim');
const NO_OPEN = hasFlag('no-open');
const VERBOSE = hasFlag('verbose');

// ---------------- 装配 ----------------
const bridge = new MqttBridge();
const hub = new WsHub();
const broker = new EmbeddedBroker();
const startedAt = Date.now();

const config = configStore;                 // API 层要用模块本身（config.load / config.save）
const startupConfig = configStore.load();   // 启动时的配置快照
const isFirstRun = !fs.existsSync(configStore.CONFIG_PATH);

// 历史落盘：每条消息追加进 SQLite，重启后曲线还能回看
const history = new HistoryStore({
  enabled: startupConfig.history?.enabled !== false,
  keepDays: startupConfig.history?.keepDays,
  maxRows: startupConfig.history?.maxRows,
  dir: startupConfig.history?.dir || undefined,
});

// 把消息按 100ms 合并成批再推给浏览器：高频场景下页面更流畅
let pending = [];
let flushTimer = null;
const FLUSH_MS = 100;
const FLUSH_MAX = 400;

function flushMessages() {
  flushTimer = null;
  if (pending.length === 0) return;
  hub.broadcast('messages', { items: pending });
  pending = [];
}

bridge.on('message', (record) => {
  history.record(record);          // 先落盘（入队），再考虑推前端
  pending.push(record);
  if (VERBOSE) console.log(`[mqtt] ${record.topic} = ${record.payload}`);
  if (pending.length >= FLUSH_MAX) {
    clearTimeout(flushTimer);
    flushMessages();
    return;
  }
  if (!flushTimer) flushTimer = setTimeout(flushMessages, FLUSH_MS);
});

bridge.on('status', () => hub.broadcast('status', bridge.getSnapshot()));
bridge.on('stats', (stats) => hub.broadcast('stats', stats));

// 内置 broker 的台账变化（设备接入/断开/踢人）推给页面
broker.onChange = (snap) => hub.broadcast('broker', snap);

const server = createServer({ bridge, hub, config, broker, history, startedAt });
hub.attach(server);

// 浏览器页面打开/刷新时，补发连接状态、主题清单、broker 台账，
// 并回放各主题最近一条消息 —— 这样刚打开或设备暂停上报时，看板也不会是空的
hub.setInitialProvider(() => [
  { type: 'status', ...bridge.getSnapshot() },
  { type: 'topics', topics: bridge.getTopics() },
  { type: 'broker', ...broker.getSnapshot() },
  { type: 'messages', items: bridge.getRecentMessages(1, 80), replay: true },
]);

// broker 客户端收发计数是持续变化的，每 3 秒补推一次台账，保证页面数字鲜活
setInterval(() => {
  if (hub.clients.size > 0) hub.broadcast('broker', broker.getSnapshot());
}, 3000).unref?.();

server.listen(PORT, HOST, async () => {
  const urls = localUrls(PORT);

  console.log('');
  console.log('  ╔══════════════════════════════════════════════════════╗');
  console.log('  ║        MQTT 实时数据监视平台  已启动                 ║');
  console.log('  ╚══════════════════════════════════════════════════════╝');
  console.log('');
  console.log(`  本机访问   http://127.0.0.1:${PORT}`);
  for (const u of urls) console.log(`  局域网访问 ${u}   ← 手机/其它电脑可用`);
  console.log('');
  console.log(`  配置文件   ${configStore.CONFIG_PATH}`);
  console.log('');

  // ---------------- 历史落盘 ----------------
  if (history.open()) {
    const st = history.stats();
    console.log('  历史数据   已落盘（SQLite）');
    console.log(`      库文件   ${st.dbPath}`);
    console.log(`      已有     ${st.rows.toLocaleString()} 条 / ${st.topics} 个主题` +
      (st.bytes ? ` / 占用 ${(st.bytes / 1048576).toFixed(1)} MB` : ''));
    if (st.oldest) {
      console.log(`      可回看   从 ${new Date(st.oldest).toLocaleString('zh-CN', { hour12: false })} 起`);
    }
    console.log(`      保留     ${st.keepDays} 天（可在界面「历史数据」面板里改）`);
    console.log('');
  } else {
    console.log(`  历史数据   未落盘：${history.reason}`);
    console.log('              重启平台会清空历史；如需保留请升级 Node 到 22.5+ 或检查配置。');
    console.log('');
  }

  // ---------------- 内置 Broker ----------------
  // 平台自带的 MQTT 服务端接入点：设备直接把地址指过来就能被监视。
  // 默认开启，用 --no-broker 关掉，改去连外部 broker。
  const wantBroker = !NO_BROKER && (isFirstRun || FORCE_DEMO || startupConfig.broker?.enabled !== false);
  let brokerPort = 0;

  if (wantBroker) {
    const wantPort = Number(BROKER_PORT || startupConfig.broker?.port || 1883);
    try {
      const { port } = await broker.start({
        port: wantPort,
        username: startupConfig.broker?.username || '',
        password: startupConfig.broker?.password || '',
      });
      brokerPort = port;
      configStore.save({ broker: { enabled: true, port } });

      if (port !== wantPort) {
        console.log(`  提示：端口 ${wantPort} 被占用，Broker 已自动改用 ${port}`);
        console.log('');
      }
      console.log('  设备接入地址（把设备的 MQTT 服务器指向这里）：');
      for (const addr of broker.getAddresses()) console.log(`      tcp://${addr}`);
      if (startupConfig.broker?.username) console.log(`      账号：${startupConfig.broker.username}（已启用鉴权）`);
      console.log('');

      // 数据模拟器：还没接真实设备时，让页面有数据可看
      const wantSim = !NO_SIM && (isFirstRun || FORCE_DEMO || startupConfig.broker?.simEnabled !== false);
      if (wantSim) {
        broker.simStart({ intervalMs: startupConfig.broker?.simIntervalMs || 1000 });
        configStore.save({ broker: { simEnabled: true } });
      }
    } catch (err) {
      console.error(`  [警告] 内置 Broker 启动失败：${err.message}`);
      console.error('        平台仍可使用，可改为连接外部 broker。');
      console.error('');
    }
  }

  // ---------------- 平台自身的订阅连接 ----------------
  const cfg = configStore.load();
  const isLocalHost = ['127.0.0.1', 'localhost', '::1'].includes(cfg.connection?.host);

  if ((isFirstRun || FORCE_DEMO) && brokerPort) {
    // 首次运行：直接接上内置 broker，开箱就有数据
    const conn = {
      ...cfg.connection, protocol: 'mqtt', host: '127.0.0.1', port: brokerPort,
      username: cfg.broker.username || '', password: cfg.broker.password || '',
    };
    configStore.save({ connection: conn });
    bridge.connect(conn, cfg.subscriptions);
    if (isFirstRun) {
      console.log('  提示：检测到首次运行，已启动内置 Broker 并自动接入。');
      console.log('        接真实设备时，把设备的 MQTT 地址指向上面列出的地址即可；');
      console.log('        若设备连的是别的 broker，在左侧「连接设置」里改地址。');
      console.log('');
    }
  } else if (brokerPort && isLocalHost && cfg.broker?.autoConnect !== false) {
    // 上次就是接在内置 broker 上的：沿用（端口可能因为占用变过）
    const conn = { ...cfg.connection, port: brokerPort };
    configStore.save({ connection: conn });
    bridge.connect(conn, cfg.subscriptions);
  } else if (cfg.connection?.host) {
    console.log(`  按已保存配置连接 ${cfg.connection.host}:${cfg.connection.port} …`);
    bridge.connect(cfg.connection, cfg.subscriptions || []);
  }

  if (!NO_OPEN) openBrowser(`http://127.0.0.1:${PORT}`);
});

// ---------------- 工具函数 ----------------

/** 列出可用的局域网地址，方便手机扫码式访问 */
function localUrls(port) {
  const out = [];
  const ifaces = os.networkInterfaces();
  for (const name of Object.keys(ifaces)) {
    for (const info of ifaces[name] || []) {
      if (info.family === 'IPv4' && !info.internal) {
        out.push(`http://${info.address}:${port}`);
      }
    }
  }
  return out;
}

function openBrowser(url) {
  const cmd = process.platform === 'win32' ? `start "" "${url}"`
    : process.platform === 'darwin' ? `open "${url}"`
      : `xdg-open "${url}"`;
  exec(cmd, (err) => {
    if (err) console.log(`  （未能自动打开浏览器，请手动访问 ${url}）`);
  });
}

// ---------------- 退出清理 ----------------
let closing = false;
async function shutdown(signal) {
  if (closing) return;
  closing = true;
  console.log(`\n[平台] 收到 ${signal}，正在关闭 ...`);
  bridge.destroy();
  hub.close();
  try { history.close(); } catch (err) { console.error('[history] 关闭失败:', err.message); }
  try { await broker.stop(); } catch { /* ignore */ }
  server.close(() => process.exit(0));
  setTimeout(() => process.exit(0), 1500);
}

process.on('SIGINT', () => shutdown('SIGINT'));
process.on('SIGTERM', () => shutdown('SIGTERM'));

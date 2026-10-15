'use strict';

/**
 * 虚拟设备 —— 用来验证平台链路的小工具
 *
 * 它扮演一台现场设备：连到 MQTT broker，周期性上报几个测点。
 * 既可以用来验证平台的内置 Broker 是否正常，也可以作为真实设备上报程序的模板。
 *
 * 用法：
 *   node tools/device-sim.js                                  默认连 127.0.0.1:1883
 *   node tools/device-sim.js --host 192.168.7.80 --port 1883
 *   node tools/device-sim.js --id 1号炉 --interval 2000
 *   node tools/device-sim.js --host x --port 1883 --username u --password p
 *   node tools/device-sim.js --count 5                       模拟 5 台设备
 *
 * 参数：
 *   --host       broker 地址（默认 127.0.0.1）
 *   --port       broker 端口（默认 1883）
 *   --id         设备名/ClientId 前缀（默认 device）
 *   --count      模拟设备台数（默认 1）
 *   --interval   上报周期毫秒（默认 1000）
 *   --username   账号
 *   --password   密码
 *   --prefix     主题前缀（默认 factory）
 */

const mqtt = require('mqtt');

// ---------------- 参数 ----------------
const argv = process.argv.slice(2);
function getArg(name, fallback) {
  const i = argv.indexOf('--' + name);
  return i >= 0 && argv[i + 1] && !argv[i + 1].startsWith('--') ? argv[i + 1] : fallback;
}

const HOST = getArg('host', '127.0.0.1');
const PORT = Number(getArg('port', 1883));
const ID = getArg('id', 'device');
const COUNT = Math.max(1, Number(getArg('count', 1)));
const INTERVAL = Math.max(100, Number(getArg('interval', 1000)));
const USERNAME = getArg('username', '');
const PASSWORD = getArg('password', '');
const PREFIX = getArg('prefix', 'factory');

// ---------------- 测点模型 ----------------
const POINTS = [
  { key: 'temp', base: 55, amp: 3, digits: 1 },
  { key: 'pressure', base: 0.8, amp: 0.06, digits: 3 },
  { key: 'current', base: 32, amp: 2.5, digits: 2 },
  { key: 'vibration', base: 2.1, amp: 0.5, digits: 2 },
];

console.log(`[虚拟设备] 目标 mqtt://${HOST}:${PORT}，${COUNT} 台设备，上报周期 ${INTERVAL}ms`);

const clients = [];
let tick = 0;

for (let i = 1; i <= COUNT; i += 1) {
  const deviceId = COUNT === 1 ? ID : `${ID}-${String(i).padStart(2, '0')}`;
  const opt = {
    clientId: deviceId,
    keepalive: 30,
    reconnectPeriod: 3000,
    clean: true,
  };
  if (USERNAME) opt.username = USERNAME;
  if (PASSWORD) opt.password = PASSWORD;

  const client = mqtt.connect(`mqtt://${HOST}:${PORT}`, opt);
  const base = `${PREFIX}/${deviceId}`;

  client.on('connect', () => {
    console.log(`[虚拟设备] ${deviceId} 已接入 broker（主题前缀 ${base}/）`);
  });
  client.on('error', (err) => {
    console.error(`[虚拟设备] ${deviceId} 出错：${err.message}`);
  });
  client.on('reconnect', () => {
    console.log(`[虚拟设备] ${deviceId} 正在重连…`);
  });

  client.__deviceId = deviceId;
  client.__base = base;
  client.__phase = i * 1.7;
  clients.push(client);
}

setInterval(() => {
  tick += 1;
  for (const client of clients) {
    if (!client.connected) continue;
    const base = client.__base;
    const phase = client.__phase + tick / 8;

    for (const p of POINTS) {
      const val = p.base + Math.sin(phase) * p.amp + (Math.random() - 0.5) * p.amp * 0.4;
      client.publish(`${base}/${p.key}`, val.toFixed(p.digits), { qos: 0 });
    }

    // 每 5 个周期发一次结构化状态报文
    if (tick % 5 === 0) {
      client.publish(`${base}/status`, JSON.stringify({
        device: client.__deviceId,
        run: true,
        mode: 'auto',
        speed: Math.round(1450 + Math.sin(phase) * 40),
        alarm: 0,
        ts: new Date().toISOString(),
      }), { qos: 0 });
    }
  }
}, INTERVAL);

// Ctrl+C 时正常断开
process.on('SIGINT', () => {
  console.log('\n[虚拟设备] 正在断开…');
  for (const c of clients) {
    try { c.end(true); } catch { /* ignore */ }
  }
  process.exit(0);
});

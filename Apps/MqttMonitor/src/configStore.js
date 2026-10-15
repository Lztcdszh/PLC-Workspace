'use strict';

/**
 * 配置持久化：连接参数 / 看板卡片布局 / 告警阈值 / 订阅主题
 * 落盘到项目根目录 config.json，重启后自动恢复。
 */

const fs = require('fs');
const path = require('path');

/**
 * 配置文件路径。
 * 可用环境变量 MQTT_MONITOR_CONFIG 指到别处 —— 这样能在不影响正式配置的前提下
 * 起一个临时实例（换端口、换数据库）做验证。
 */
const CONFIG_PATH = process.env.MQTT_MONITOR_CONFIG
  ? path.resolve(process.env.MQTT_MONITOR_CONFIG)
  : path.join(__dirname, '..', 'config.json');

/** 出厂默认配置 */
const DEFAULTS = {
  // ---- MQTT 连接 ----
  connection: {
    protocol: 'mqtt',      // mqtt | mqtts | ws | wss
    host: '127.0.0.1',
    port: 1883,
    clientId: '',          // 留空则自动生成
    username: '',
    password: '',
    keepalive: 60,
    cleanSession: true,
    rejectUnauthorized: false, // mqtts 自签证书场景
  },
  // ---- 订阅 ----
  subscriptions: [
    { topic: '#', qos: 0 },
  ],
  // ---- 内置 Broker（平台自带的 MQTT 服务端接入点）----
  broker: {
    enabled: true,
    port: 1883,
    username: '',      // 留空 = 允许匿名接入
    password: '',
    autoConnect: true, // 平台自身是否自动订阅本地 broker
    simEnabled: true,  // 数据模拟器（没接真实设备时让页面有数据）
    simIntervalMs: 1000,
  },
  // ---- 看板卡片 ----
  cards: [],
  // ---- 历史数据落盘（SQLite，重启不丢）----
  history: {
    enabled: true,     // 关掉则退回纯内存（重启即清空）
    keepDays: 7,       // 保留天数，超期自动清理
    maxRows: 2000000,  // 行数上限，兜底防止爆盘
    dir: '',           // 留空 = 项目下的 data/ 目录
  },
  // ---- 其它 ----
  ui: {
    maxLogRows: 300,
    // 已废弃（只为兼容老配置保留）：曲线显示多久由 chartWindowMin 决定，
    // 每条曲线最多画多少个点改成逐张卡片设 card.points（0/空 = 不限）。
    chartPoints: 0,
    // 曲线 X 轴时间范围（分钟）：看板全局默认值，0 = 全部不限。
    // 单张卡片可用 card.windowMin 覆盖它（null / 缺省 = 跟随这个全局值）。
    chartWindowMin: 0,
    // 同前缀自动合并：同一台设备的点位常发在不同主题上（plc/DB1.DBW290 电量、
    // plc/DB1.DBW292 电流），开启后这些主题自动收进一张卡。
    // 界面上的复选框写 localStorage（用户的显式选择优先），这里只是"默认值/无头自检的开关"。
    autoGroup: true,
    theme: 'light',
  },
};

/** 深合并：以默认值为骨架，用已存配置覆盖 */
function merge(base, override) {
  if (override === null || override === undefined) return base;
  if (Array.isArray(base) || Array.isArray(override)) return override;
  if (typeof base !== 'object' || typeof override !== 'object') return override;
  const out = { ...base };
  for (const key of Object.keys(override)) {
    out[key] = key in base ? merge(base[key], override[key]) : override[key];
  }
  return out;
}

let cache = null;

function load() {
  if (cache) return cache;
  try {
    const raw = fs.readFileSync(CONFIG_PATH, 'utf8');
    cache = merge(DEFAULTS, JSON.parse(raw));
  } catch {
    cache = JSON.parse(JSON.stringify(DEFAULTS));
  }
  return cache;
}

function save(patch) {
  const current = load();
  cache = patch ? merge(current, patch) : current;
  try {
    fs.writeFileSync(CONFIG_PATH, JSON.stringify(cache, null, 2), 'utf8');
  } catch (err) {
    console.error('[config] 写入失败:', err.message);
  }
  return cache;
}

/** 根据配置拼出 MQTT 连接 URL */
function buildUrl(conn) {
  const { protocol, host, port, username, password } = conn;
  const auth = username
    ? `${encodeURIComponent(username)}:${encodeURIComponent(password || '')}@`
    : '';
  return `${protocol}://${auth}${host}:${port}`;
}

module.exports = { load, save, buildUrl, DEFAULTS, CONFIG_PATH };

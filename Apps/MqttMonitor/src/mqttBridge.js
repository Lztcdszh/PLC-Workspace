'use strict';

/**
 * MQTT 桥接层：包装 mqtt.js 客户端，向上层（HTTP API / WebSocket）提供
 * 统一的连接、订阅、发布、历史与统计能力。
 *
 * 支持协议：mqtt / mqtts（TCP、TLS）、ws / wss（WebSocket）。
 * 断线由 mqtt.js 内置机制自动重连，本层只负责把状态变化播报出去。
 */

const mqtt = require('mqtt');
const { EventEmitter } = require('events');
const { buildUrl } = require('./configStore');

/**
 * 单主题内存环形缓冲深度 / 最多保留多少个主题。
 *
 * 2000 而不是 200：这个缓冲是「内存里能立刻画出来的数据」，
 * 它直接决定曲线「全部」这个选项最多能看到多久。
 * 按 60 秒上报一次算，200 条只有 3.3 小时，2000 条约 33 小时；
 * 每条只是一个对象（几个数字 + 一段载荷字符串），2000 条的常驻开销可以忽略。
 * 想看更久的历史走落盘库（按窗口从 SQLite 回补），不靠堆内存。
 */
const HISTORY_PER_TOPIC = 2000;
const MAX_TOPICS = 500;

/**
 * 从 JSON 报文里挑一个「主数值」，写进落盘库的 num 列（导出 CSV / 统计用）。
 *
 * 为什么不抽：设备上报的是 {"tag":"电池电量","dataType":"Int16","value":"0",…}，
 * 老代码只把「裸数字」报文认成数值，JSON 报文 num 一律 null ——
 * 结果导出的 CSV「数值」列整列空白，曲线本身不受影响（前端自己从 json 里挖字段）。
 */
const NUM_FIELD_PRIORITY = ['value', 'val', 'v', 'num', 'pv', 'reading', 'data'];
// 时间戳长得像数值，兜底那步必须把它们排除掉，否则 {"ts":1790584694860} 会被当成测量值
const TS_LIKE_KEY = /(^|_)(ts|time|timestamp|datetime|date|at|stamp)$/i;   // ts / created_at / update_time
const TS_LIKE_CAMEL = /(Ts|Time|Timestamp|DateTime|Date|At)$/;              // createdAt / updatedAt
function isTsLikeKey(k) { return TS_LIKE_KEY.test(k) || TS_LIKE_CAMEL.test(k); }

function toNumOrNull(v) {
  if (typeof v === 'number') return Number.isFinite(v) ? v : null;
  if (typeof v === 'boolean') return v ? 1 : 0;
  if (typeof v === 'string' && v.trim() !== '') {
    const n = Number(v.trim());
    return Number.isFinite(n) ? n : null;
  }
  return null;
}

function pickNumericField(json) {
  if (!json || typeof json !== 'object' || Array.isArray(json)) return null;
  const keys = Object.keys(json);

  // 1) 先按工控设备常见字段名找（大小写不敏感）
  for (const want of NUM_FIELD_PRIORITY) {
    for (const k of keys) {
      if (k.toLowerCase() !== want) continue;
      const n = toNumOrNull(json[k]);
      if (n !== null) return n;
    }
  }

  // 2) 兜底：整个对象只有唯一一个数值字段（且不是时间戳）就用它，
  //    这样才能兼容 {"temp": 23.5} 这种自定schema。
  let hit = null;
  for (const k of keys) {
    if (isTsLikeKey(k)) continue;
    const n = toNumOrNull(json[k]);
    if (n === null) continue;
    if (hit !== null) return null;   // 有多个数值字段，猜不出来，放弃
    hit = n;
  }
  return hit;
}

class MqttBridge extends EventEmitter {
  constructor() {
    super();
    this.client = null;
    this.conn = null;
    this.subs = [];
    this.history = new Map();   // topic -> [{ts, payload, num, json}]
    this.topicStats = new Map(); // topic -> {count, lastTs, lastPayload, lastNum}
    this.status = 'disconnected'; // disconnected | connecting | connected | reconnecting | error
    this.lastError = '';
    this.connectedAt = null;
    this.totalMessages = 0;
    this.bytesReceived = 0;

    // 速率统计（5 秒滑动窗）
    this._rateWindow = [];
    this._rateTimer = setInterval(() => this._emitRate(), 1000);
    this._rateTimer.unref?.();
  }

  get isConnected() {
    return this.status === 'connected';
  }

  /** 连接（若已连接先断开） */
  connect(conn, subs) {
    this.disconnect();
    this.conn = conn;
    this.subs = Array.isArray(subs) ? subs : [];
    this.status = 'connecting';
    this.lastError = '';
    this.emit('status');

    const url = buildUrl(conn);
    console.log(`[mqtt] 正在连接 ${this._safeUrl(url)} ...`);

    const options = {
      clientId: conn.clientId || `mqtt-monitor-${Math.random().toString(16).slice(2, 10)}`,
      keepalive: Number(conn.keepalive) || 60,
      clean: conn.cleanSession !== false,
      reconnectPeriod: 3000,
      connectTimeout: 8000,
      resubscribe: true,
      rejectUnauthorized: conn.rejectUnauthorized === true,
      protocolVersion: 4, // MQTT 3.1.1，兼容性最好
    };
    if (conn.username) options.username = conn.username;
    if (conn.password) options.password = conn.password;

    let client;
    try {
      client = mqtt.connect(url, options);
    } catch (err) {
      this.status = 'error';
      this.lastError = err.message;
      this.emit('status');
      return;
    }
    this.client = client;

    client.on('connect', () => {
      this.status = 'connected';
      this.connectedAt = Date.now();
      this.lastError = '';
      console.log(`[mqtt] 已连接 ${this._safeUrl(url)}`);

      if (this.subs.length > 0) {
        for (const s of this.subs) {
          client.subscribe(s.topic, { qos: Number(s.qos) || 0 }, (err) => {
            if (err) console.error(`[mqtt] 订阅失败 ${s.topic}: ${err.message}`);
            else console.log(`[mqtt] 已订阅 ${s.topic} (QoS ${s.qos ?? 0})`);
          });
        }
      }
      this.emit('status');
    });

    client.on('reconnect', () => {
      this.status = 'reconnecting';
      this.emit('status');
    });

    client.on('close', () => {
      if (this.status !== 'error') {
        this.status = 'disconnected';
        this.connectedAt = null;
        this.emit('status');
      }
    });

    client.on('offline', () => {
      this.status = 'disconnected';
      this.emit('status');
    });

    client.on('error', (err) => {
      this.lastError = err?.message || String(err);
      this.status = 'error';
      console.error(`[mqtt] 错误: ${this.lastError}`);
      this.emit('status');
    });

    client.on('message', (topic, payload, packet) => this._onMessage(topic, payload, packet));
  }

  disconnect() {
    if (this.client) {
      const c = this.client;
      this.client = null;
      try { c.end(true); } catch { /* ignore */ }
    }
    this.status = 'disconnected';
    this.connectedAt = null;
    this.emit('status');
  }

  /** 动态新增订阅（不需要重连） */
  subscribe(topic, qos = 0) {
    if (!this.subs.some((s) => s.topic === topic)) {
      this.subs.push({ topic, qos: Number(qos) || 0 });
    }
    if (this.client && this.isConnected) {
      this.client.subscribe(topic, { qos: Number(qos) || 0 }, (err) => {
        if (err) console.error(`[mqtt] 订阅失败 ${topic}: ${err.message}`);
        else console.log(`[mqtt] 已订阅 ${topic}`);
      });
    }
  }

  unsubscribe(topic) {
    this.subs = this.subs.filter((s) => s.topic !== topic);
    if (this.client && this.isConnected) {
      this.client.unsubscribe(topic, () => { /* ignore */ });
    }
  }

  /** 发布（用于平台内的下发测试） */
  publish(topic, payload, qos = 0, retain = false) {
    return new Promise((resolve, reject) => {
      if (!this.client || !this.isConnected) {
        return reject(new Error('当前未连接 broker，无法下发'));
      }
      this.client.publish(topic, String(payload), { qos: Number(qos) || 0, retain: !!retain }, (err) => {
        if (err) reject(err); else resolve(true);
      });
    });
  }

  /** 取某主题的历史（前端刷新页面后回填曲线用） */
  getHistory(topic, limit = 100) {
    const arr = this.history.get(topic) || [];
    return arr.slice(-limit);
  }

  /**
   * 某主题内存缓冲的条数与时间跨度。
   * @param {number} since 给了就额外算一下「从这个时间起还有多少条」（inWindow）。
   * 前端靠它判断"我选的这个窗口，缓冲到底够不够长"。
   */
  topicSpan(topic, since = 0) {
    const arr = this.history.get(topic) || [];
    if (!arr.length) return null;
    const s = Number(since) || 0;
    let i0 = 0;
    if (s > 0) {
      while (i0 < arr.length - 1 && arr[i0].ts < s) i0 += 1;
    }
    return {
      count: arr.length,
      firstTs: arr[0].ts,
      lastTs: arr[arr.length - 1].ts,
      inWindow: s > 0 ? arr.length - i0 : arr.length,
    };
  }

  /**
   * 取各主题最近若干条消息（按时间升序）。
   * 用于页面刚打开/刷新时回放一次，让看板立刻有内容 ——
   * 否则在设备暂停上报或刚刷新页面时，看板会一直空着等新数据。
   */
  getRecentMessages(perTopic = 1, maxTopics = 80) {
    const out = [];
    const entries = [...this.history.entries()]
      .sort((a, b) => (b[1][b[1].length - 1]?.ts || 0) - (a[1][a[1].length - 1]?.ts || 0))
      .slice(0, maxTopics);
    for (const [, arr] of entries) {
      out.push(...arr.slice(-perTopic));
    }
    out.sort((a, b) => a.ts - b.ts);
    return out;
  }

  getTopics() {
    const list = [];
    for (const [topic, st] of this.topicStats) {
      list.push({
        topic,
        count: st.count,
        lastTs: st.lastTs,
        lastPayload: st.lastPayload,
        lastNum: st.lastNum,
        historyLen: (this.history.get(topic) || []).length,
      });
    }
    list.sort((a, b) => b.lastTs - a.lastTs);
    return list;
  }

  reset() {
    this.history.clear();
    this.topicStats.clear();
    this.totalMessages = 0;
    this.bytesReceived = 0;
    this._rateWindow = [];
  }

  getSnapshot() {
    return {
      status: this.status,
      lastError: this.lastError,
      connectedAt: this.connectedAt,
      uptimeMs: this.connectedAt ? Date.now() - this.connectedAt : 0,
      clientId: this.client?.options?.clientId || '',
      subscriptions: this.subs,
      totalMessages: this.totalMessages,
      bytesReceived: this.bytesReceived,
      topicCount: this.topicStats.size,
      rate: this._currentRate(),
    };
  }

  // ---------------- 内部 ----------------

  _onMessage(topic, payload, packet) {
    const ts = Date.now();
    const size = payload?.length || 0;
    this.totalMessages += 1;
    this.bytesReceived += size;
    this._rateWindow.push(ts);

    // payload 解析：优先 UTF-8 文本，二进制载荷走 hex
    let text = '';
    let isBinary = false;
    try {
      text = payload.toString('utf8');
      // 含替换字符说明不是合法 UTF-8
      if (text.includes('\uFFFD')) { isBinary = true; text = payload.toString('hex'); }
    } catch {
      isBinary = true;
      text = payload.toString('hex');
    }

    // 结构化解析
    let json = null;
    let num = null;
    if (!isBinary) {
      const trimmed = text.trim();
      if (trimmed.startsWith('{') || trimmed.startsWith('[')) {
        try { json = JSON.parse(trimmed); } catch { /* 保留原文 */ }
      }
      if (json === null) {
        const n = Number(trimmed);
        if (trimmed !== '' && Number.isFinite(n)) num = n;
      } else {
        // JSON 报文也要抽一个数值出来（库里那列 num 给导出 CSV / 统计用）。
        // 不抽的话设备这类 {"tag":…,"dataType":"Int16","value":"0",…} 报文
        // 落到库里 num 永远是 null，导出的「数值」列整列空白。
        num = pickNumericField(json);
      }
    }

    const record = {
      topic,
      ts,
      payload: text.length > 4096 ? text.slice(0, 4096) + '…' : text,
      num,
      json,
      binary: isBinary,
      qos: packet?.qos ?? 0,
      retain: !!packet?.retain,
      size,
    };

    // 历史环形缓冲
    let arr = this.history.get(topic);
    if (!arr) {
      arr = [];
      this.history.set(topic, arr);
      this._evictTopicsIfNeeded();
    }
    arr.push(record);
    if (arr.length > HISTORY_PER_TOPIC) arr.splice(0, arr.length - HISTORY_PER_TOPIC);

    // 主题统计
    const st = this.topicStats.get(topic) || { count: 0, lastTs: 0 };
    st.count += 1;
    st.lastTs = ts;
    st.lastPayload = record.payload;
    st.lastNum = num;
    this.topicStats.set(topic, st);

    this.emit('message', record);
  }

  /** 主题过多时，淘汰最久未活动的主题，防止内存无限增长 */
  _evictTopicsIfNeeded() {
    if (this.topicStats.size <= MAX_TOPICS) return;
    const entries = [...this.topicStats.entries()].sort((a, b) => a[1].lastTs - b[1].lastTs);
    const dropCount = this.topicStats.size - MAX_TOPICS;
    for (let i = 0; i < dropCount; i += 1) {
      const topic = entries[i][0];
      this.topicStats.delete(topic);
      this.history.delete(topic);
    }
    console.log(`[mqtt] 主题数超限，已淘汰 ${dropCount} 个最久未活动的主题`);
  }

  _currentRate() {
    const cutoff = Date.now() - 5000;
    const recent = this._rateWindow.filter((t) => t >= cutoff).length;
    return Number((recent / 5).toFixed(1));
  }

  _emitRate() {
    this._rateWindow = this._rateWindow.filter((t) => t >= Date.now() - 5000);
    this.emit('stats', {
      rate: this._currentRate(),
      totalMessages: this.totalMessages,
      bytesReceived: this.bytesReceived,
      topicCount: this.topicStats.size,
      uptimeMs: this.connectedAt ? Date.now() - this.connectedAt : 0,
    });
  }

  /** 日志里不打印明文密码 */
  _safeUrl(url) {
    return url.replace(/\/\/([^:]+):[^@]*@/, '//$1:***@');
  }

  destroy() {
    clearInterval(this._rateTimer);
    this.disconnect();
    this.removeAllListeners();
  }
}

module.exports = { MqttBridge, pickNumericField };

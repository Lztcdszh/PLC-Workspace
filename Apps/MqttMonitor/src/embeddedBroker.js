'use strict';

/**
 * 内置 MQTT Broker —— 平台自带的服务端接入点
 *
 * 定位：设备/网关把 MQTT 地址指向本机端口（默认 1883）即可接入，
 *       平台同时作为订阅方把数据汇总到看板，不需要另外部署 EMQX / mosquitto。
 *
 * 能力：
 *   - 监听端口、可选账号密码鉴权
 *   - 客户端台账（ClientId / IP / 协议版本 / 订阅 / 收发统计 / 在线时长）
 *   - 主动踢掉指定客户端
 *   - 可选数据模拟器：往 broker 里注入工业场站数据，用于没接设备时试跑
 *
 * 数据流是完整 MQTT 链路（设备 -> broker -> 订阅方 -> 看板），不是绕过 broker 直塞前端。
 */

const net = require('net');
const os = require('os');
const { Aedes } = require('aedes');

/** 模拟器信号模型 */
const SIGNALS = [
  { topic: 'plant/line1/temp', name: '1号线 炉温', unit: '℃', base: 62, amp: 2.4, noise: 0.5, digits: 1, drift: 0.03, high: 70, low: 50 },
  { topic: 'plant/line1/pressure', name: '1号线 压力', unit: 'MPa', base: 0.85, amp: 0.05, noise: 0.012, digits: 3, drift: 0.001, high: 0.95, low: 0.6 },
  { topic: 'plant/line1/flow', name: '1号线 流量', unit: 'm³/h', base: 120, amp: 8, noise: 2.2, digits: 1, drift: 0.05, high: 140, low: 90 },
  { topic: 'plant/line1/current', name: '1号线 主电机电流', unit: 'A', base: 45, amp: 3.5, noise: 1.1, digits: 2, drift: 0.04, high: 52, low: 30 },
  { topic: 'plant/line1/vibration', name: '1号线 振动', unit: 'mm/s', base: 2.4, amp: 0.6, noise: 0.18, digits: 2, drift: 0.01, high: 4.5, low: 0 },
  { topic: 'plant/line2/temp', name: '2号线 炉温', unit: '℃', base: 58, amp: 2.0, noise: 0.5, digits: 1, drift: 0.03, high: 66, low: 48 },
  { topic: 'plant/line2/pressure', name: '2号线 压力', unit: 'MPa', base: 0.72, amp: 0.04, noise: 0.01, digits: 3, drift: 0.001, high: 0.85, low: 0.5 },
  { topic: 'plant/line2/flow', name: '2号线 流量', unit: 'm³/h', base: 96, amp: 6, noise: 2.0, digits: 1, drift: 0.05, high: 115, low: 70 },
  { topic: 'plant/line2/current', name: '2号线 主电机电流', unit: 'A', base: 38, amp: 3.0, noise: 1.0, digits: 2, drift: 0.04, high: 45, low: 25 },
  { topic: 'plant/line2/vibration', name: '2号线 振动', unit: 'mm/s', base: 1.8, amp: 0.5, noise: 0.15, digits: 2, drift: 0.01, high: 3.5, low: 0 },
];

const COUNTERS = [
  { topic: 'plant/line1/count', name: '1号线 累计产量', unit: '件', step: 3 },
  { topic: 'plant/line2/count', name: '2号线 累计产量', unit: '件', step: 2 },
];

class EmbeddedBroker {
  constructor() {
    this.broker = null;
    this.server = null;
    this.port = 0;
    this.running = false;
    this.listenError = '';

    // 客户端台账：id -> {id, ip, username, version, clean, connectedAt, msgIn, bytesIn, lastTopic, lastTs, client}
    this.clients = new Map();

    this.totals = { clientsSeen: 0, messagesIn: 0, bytesIn: 0 };

    this.auth = { username: '', password: '' };

    this.sim = { running: false, intervalMs: 1000, timer: null, tick: 0, walk: new Map(), counters: new Map() };

    /** 状态变化回调（供上层广播到前端） */
    this.onChange = null;
    /** 数据回调：broker 收到客户端发布时触发 */
    this.onClientMessage = null;
  }

  // ------------------------------------------------------------
  //  启停
  // ------------------------------------------------------------
  async start(opts = {}) {
    if (this.running) return { port: this.port };

    const wantPort = Number(opts.port) || 1883;
    if (opts.username !== undefined || opts.password !== undefined) {
      this.auth = { username: opts.username || '', password: opts.password || '' };
    }

    this.broker = await Aedes.createBroker();
    this._hookEvents();

    this.server = net.createServer(this.broker.handle);
    this.port = await this._listen(wantPort, wantPort === 1883 ? 18830 : wantPort + 1);
    this.running = true;
    this.listenError = '';
    console.log(`[broker] 内置 Broker 已监听 0.0.0.0:${this.port}${this.auth.username ? '（已启用账号鉴权）' : '（允许匿名接入）'}`);
    this._notify();
    return { port: this.port };
  }

  async stop() {
    this.simStop();
    for (const rec of this.clients.values()) {
      try { rec.client?.close?.(); } catch { /* ignore */ }
    }
    this.clients.clear();
    this.running = false;

    await new Promise((resolve) => {
      if (!this.server) return resolve();
      this.server.close(() => resolve());
    });
    this.server = null;

    await new Promise((resolve) => {
      if (!this.broker) return resolve();
      try { this.broker.close(() => resolve()); } catch { resolve(); }
    });
    this.broker = null;
    this.port = 0;
    console.log('[broker] 内置 Broker 已停止');
    this._notify();
    return { running: false };
  }

  _listen(port, fallback) {
    return new Promise((resolve, reject) => {
      const tryListen = (p, isFallback) => {
        const onError = (err) => {
          if (err.code === 'EADDRINUSE' && !isFallback) {
            console.warn(`[broker] 端口 ${p} 被占用，改用 ${fallback}`);
            tryListen(fallback, true);
          } else {
            this.listenError = err.message;
            reject(err);
          }
        };
        this.server.once('error', onError);
        this.server.listen(p, '0.0.0.0', () => {
          this.server.removeListener('error', onError);
          resolve(p);
        });
      };
      tryListen(port, false);
    });
  }

  // ------------------------------------------------------------
  //  事件挂钩
  // ------------------------------------------------------------
  _hookEvents() {
    const b = this.broker;

    // 可选鉴权：配置了账号则校验，否则放行（同时把用户名记到台账上）
    b.authenticate = (client, username, password, done) => {
      const u = username || '';
      client.__username = u;
      if (!this.auth.username) return done(null, true);
      const pwd = password ? password.toString('utf8') : '';
      const ok = u === this.auth.username && pwd === this.auth.password;
      if (!ok) {
        console.warn(`[broker] 拒绝接入：${client.id}（账号或密码不正确）`);
        return done(null, false);
      }
      done(null, true);
    };

    b.on('client', (client) => {
      const remote = client.conn?.remoteAddress || '';
      this.clients.set(client.id, {
        client,
        id: client.id,
        ip: remote.replace('::ffff:', ''),
        username: client.__username || '',
        version: client.version,
        clean: !!client.clean,
        connectedAt: Date.now(),
        msgIn: 0,
        bytesIn: 0,
        lastTopic: '',
        lastTs: 0,
      });
      this.totals.clientsSeen += 1;
      console.log(`[broker] 设备接入 ${client.id} (${remote.replace('::ffff:', '')})`);
      this._notify();
    });

    // 注意：aedes 1.x 的事件名是 clientDisconnect（不是 clientDisconnected）
    b.on('clientDisconnect', (client) => {
      if (client && this.clients.has(client.id)) {
        console.log(`[broker] 设备断开 ${client.id}`);
        this.clients.delete(client.id);
        this._notify();
      }
    });

    // 客户端发布的报文：计入台账，并交给上层（如果它想旁路处理）
    b.on('publish', (packet, client) => {
      const size = packet.payload?.length || 0;
      this.totals.messagesIn += 1;
      this.totals.bytesIn += size;

      if (client) {
        const rec = this.clients.get(client.id);
        if (rec) {
          rec.msgIn += 1;
          rec.bytesIn += size;
          rec.lastTopic = packet.topic;
          rec.lastTs = Date.now();
        }
        if (this.onClientMessage) {
          try {
            this.onClientMessage({
              clientId: client.id,
              ip: this.clients.get(client.id)?.ip || '',
              topic: packet.topic,
              payload: packet.payload,
              qos: packet.qos,
              retain: !!packet.retain,
            });
          } catch { /* ignore */ }
        }
      }
    });

    b.on('clientError', () => { /* 客户端异常断开，属正常噪音 */ });
  }

  _notify() {
    if (this.onChange) {
      try { this.onChange(this.getSnapshot()); } catch { /* ignore */ }
    }
  }

  // ------------------------------------------------------------
  //  快照
  // ------------------------------------------------------------
  /** aedes 把客户端订阅放在 client.subscriptions，形态是 {topic: {qos}} */
  _readSubscriptions(client) {
    const s = client?.subscriptions;
    if (!s || typeof s !== 'object') return [];
    try {
      return Object.entries(s).map(([topic, v]) => ({ topic, qos: v?.qos ?? 0 }));
    } catch {
      return [];
    }
  }

  getSnapshot() {
    const now = Date.now();
    const clients = [];
    for (const rec of this.clients.values()) {
      clients.push({
        id: rec.id,
        ip: rec.ip,
        username: rec.username,
        version: rec.version,
        clean: rec.clean,
        connectedAt: rec.connectedAt,
        uptimeMs: now - rec.connectedAt,
        msgIn: rec.msgIn,
        bytesIn: rec.bytesIn,
        lastTopic: rec.lastTopic,
        lastTs: rec.lastTs,
        subscriptions: this._readSubscriptions(rec.client),
      });
    }
    clients.sort((a, b) => b.connectedAt - a.connectedAt);

    return {
      running: this.running,
      port: this.port,
      listenError: this.listenError,
      authEnabled: !!this.auth.username,
      username: this.auth.username,
      clients,
      clientCount: clients.length,
      totals: { ...this.totals },
      addresses: this.getAddresses(),
      sim: { running: this.sim.running, intervalMs: this.sim.intervalMs },
    };
  }

  /** 给设备端用的接入地址（局域网 IP:端口） */
  getAddresses() {
    if (!this.port) return [];
    const out = [`127.0.0.1:${this.port}`];
    const ifaces = os.networkInterfaces();
    for (const name of Object.keys(ifaces)) {
      for (const info of ifaces[name] || []) {
        if (info.family === 'IPv4' && !info.internal) out.push(`${info.address}:${this.port}`);
      }
    }
    return out;
  }

  /** 主动断开某个客户端 */
  disconnectClient(clientId) {
    const rec = this.clients.get(clientId);
    if (!rec) return false;
    try { rec.client?.close?.(); } catch { /* ignore */ }
    this.clients.delete(clientId);
    this._notify();
    console.log(`[broker] 已主动断开 ${clientId}`);
    return true;
  }

  // ------------------------------------------------------------
  //  数据模拟器（可选）
  // ------------------------------------------------------------
  simStart(opts = {}) {
    if (!this.running) throw new Error('内置 Broker 未运行，请先启动 Broker');
    const intervalMs = Math.max(100, Number(opts.intervalMs) || this.sim.intervalMs || 1000);
    this.simStop();
    this.sim.intervalMs = intervalMs;
    this.sim.running = true;
    this.sim.timer = setInterval(() => this._emit(), intervalMs);
    console.log(`[broker] 数据模拟器已开启，周期 ${intervalMs}ms`);
    this._notify();
    return { running: true, intervalMs };
  }

  simStop() {
    clearInterval(this.sim.timer);
    this.sim.timer = null;
    const was = this.sim.running;
    this.sim.running = false;
    if (was) this._notify();
    return { running: false };
  }

  _emit() {
    if (!this.broker || !this.running) return;
    this.sim.tick += 1;
    const t = this.sim.tick;

    for (const sig of SIGNALS) {
      this._publish(sig.topic, this._nextValue(sig).toFixed(sig.digits));
    }
    for (const c of COUNTERS) {
      const cur = (this.sim.counters.get(c.topic) || 0) + c.step + Math.floor(Math.random() * 2);
      this.sim.counters.set(c.topic, cur);
      this._publish(c.topic, String(cur));
    }

    if (t % 5 === 0) {
      const phase = t % 40;
      for (const line of ['line1', 'line2']) {
        const bad = (line === 'line1' && phase >= 30) ? 1 : 0;
        this._publish(`plant/${line}/status`, JSON.stringify({
          run: bad === 0,
          mode: bad === 0 ? 'auto' : 'fault',
          speed: Math.round(1380 + Math.sin(t / 7) * 60),
          alarm: bad,
          oee: Number((78 + Math.sin(t / 11) * 9).toFixed(1)),
        }));
      }
    }

    if (t % 20 === 0) {
      this._publish('plant/site/alarm', JSON.stringify({
        code: 'W' + String(100 + (t % 5)),
        level: t % 40 === 0 ? 'major' : 'minor',
        text: t % 40 === 0 ? '1号线炉温偏高，请检查冷却水路' : '2号线振动接近上限',
        ts: new Date().toISOString(),
      }));
    }
  }

  _nextValue(sig) {
    let walk = this.sim.walk.get(sig.topic) ?? 0;
    walk += (Math.random() - 0.5) * sig.noise;
    walk *= 0.97;
    this.sim.walk.set(sig.topic, walk);

    const wave = Math.sin(this.sim.tick / 9) * sig.amp;
    const spike = (this.sim.tick % 47 === 12) ? sig.amp * 2.6 : 0;
    const drift = sig.base * sig.drift * Math.sin(this.sim.tick / 60);
    return sig.base + wave + walk + spike + drift + (Math.random() - 0.5) * sig.noise;
  }

  _publish(topic, payload) {
    try {
      this.broker.publish(
        { cmd: 'publish', topic, payload: Buffer.from(String(payload), 'utf8'), qos: 0, retain: false },
        () => { /* ignore */ },
      );
    } catch (err) {
      console.error('[broker] 模拟数据发布失败:', err.message);
    }
  }

  /** 模拟器会发布哪些测点（前端可用于一键生成看板卡片） */
  static get catalog() {
    return [
      ...SIGNALS.map((s) => ({ topic: s.topic, name: s.name, unit: s.unit, high: s.high, low: s.low })),
      ...COUNTERS.map((c) => ({ topic: c.topic, name: c.name, unit: c.unit })),
    ];
  }
}

module.exports = { EmbeddedBroker };

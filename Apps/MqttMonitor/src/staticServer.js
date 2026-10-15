'use strict';

/**
 * HTTP 层：静态资源服务 + REST API。
 * WebSocket 升级也挂在同一个 server 上，所以整个平台只占一个端口。
 */

const http = require('http');
const fs = require('fs');
const path = require('path');

const PUBLIC_DIR = path.join(__dirname, '..', 'public');

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.ico': 'image/x-icon',
  '.woff2': 'font/woff2',
};

/** 读取并解析 JSON 请求体，带大小上限 */
function readBody(req, limit = 512 * 1024) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    let size = 0;
    req.on('data', (chunk) => {
      size += chunk.length;
      if (size > limit) {
        reject(new Error('请求体过大'));
        req.destroy();
        return;
      }
      chunks.push(chunk);
    });
    req.on('end', () => {
      const raw = Buffer.concat(chunks).toString('utf8');
      if (!raw) return resolve({});
      try { resolve(JSON.parse(raw)); } catch (err) { reject(new Error('请求体不是合法 JSON')); }
    });
    req.on('error', reject);
  });
}

function sendJson(res, status, payload) {
  const body = JSON.stringify(payload);
  res.writeHead(status, {
    'Content-Type': 'application/json; charset=utf-8',
    'Cache-Control': 'no-store',
    'Content-Length': Buffer.byteLength(body),
  });
  res.end(body);
}

/** 静态文件，带目录穿越防护 */
function serveStatic(req, res, urlPath) {
  const rel = decodeURIComponent(urlPath === '/' ? '/index.html' : urlPath);
  const target = path.join(PUBLIC_DIR, rel);
  const normalized = path.normalize(target);
  if (!normalized.startsWith(PUBLIC_DIR)) {
    res.writeHead(403); res.end('Forbidden'); return;
  }
  fs.readFile(normalized, (err, data) => {
    if (err) {
      res.writeHead(404, { 'Content-Type': 'text/plain; charset=utf-8' });
      res.end('404 Not Found');
      return;
    }
    const ext = path.extname(normalized).toLowerCase();
    res.writeHead(200, {
      'Content-Type': MIME[ext] || 'application/octet-stream',
      'Cache-Control': 'no-cache',
    });
    res.end(data);
  });
}

/**
 * 创建 HTTP 服务
 * @param {object} ctx 上下文：{ bridge, hub, config, demo }
 */
function createServer(ctx) {
  const { bridge, hub, config } = ctx;

  const server = http.createServer(async (req, res) => {
    const url = new URL(req.url, `http://${req.headers.host || 'localhost'}`);
    const pathname = url.pathname;

    if (!pathname.startsWith('/api/')) {
      serveStatic(req, res, pathname);
      return;
    }

    try {
      const body = req.method === 'POST' ? await readBody(req) : {};
      const result = await routeApi(pathname, req.method, body, url, ctx);
      if (result === undefined) {
        sendJson(res, 404, { ok: false, error: `未知接口 ${pathname}` });
      } else if (result && result.__raw) {
        // 需要直接吐文件流的接口（CSV 导出等），不走 JSON 包装
        const buf = Buffer.isBuffer(result.body) ? result.body : Buffer.from(result.body, 'utf8');
        const headers = {
          'Content-Type': result.contentType || 'application/octet-stream',
          'Cache-Control': 'no-store',
          'Content-Length': buf.length,
        };
        if (result.filename) {
          headers['Content-Disposition'] =
            `attachment; filename="${result.filename}"; filename*=UTF-8''${encodeURIComponent(result.filename)}`;
        }
        res.writeHead(200, headers);
        res.end(buf);
      } else {
        sendJson(res, 200, { ok: true, data: result });
      }
    } catch (err) {
      sendJson(res, 400, { ok: false, error: err.message });
    }
  });

  return server;
}

/** 接口路由表 */
async function routeApi(pathname, method, body, url, ctx) {
  const { bridge, config, broker, history } = ctx;

  switch (pathname) {
    // 页面初始化：一次性拿到配置、状态、主题清单、broker 台账
    case '/api/state': {
      const cfg = config.load();
      return {
        connection: cfg.connection,
        subscriptions: cfg.subscriptions,
        cards: cfg.cards,
        ui: cfg.ui,
        brokerConfig: cfg.broker,
        brokerState: broker.getSnapshot(),
        historyState: history ? history.stats() : null,
        catalog: require('./embeddedBroker').EmbeddedBroker.catalog,
        snapshot: bridge.getSnapshot(),
        topics: bridge.getTopics(),
      };
    }

    // 连接 broker（可以是外部 broker，也可以是本机内置 broker）
    case '/api/connect': {
      const conn = body.connection || {};
      const subs = Array.isArray(body.subscriptions) ? body.subscriptions : config.load().subscriptions;
      const merged = config.save({ connection: conn, subscriptions: subs });
      bridge.connect(merged.connection, merged.subscriptions);
      return bridge.getSnapshot();
    }

    case '/api/disconnect':
      bridge.disconnect();
      return bridge.getSnapshot();

    case '/api/subscribe': {
      if (!body.topic) throw new Error('缺少 topic');
      bridge.subscribe(body.topic, body.qos);
      const subs = bridge.subs.map((s) => ({ ...s }));
      config.save({ subscriptions: subs });
      return subs;
    }

    case '/api/unsubscribe': {
      if (!body.topic) throw new Error('缺少 topic');
      bridge.unsubscribe(body.topic);
      const subs = bridge.subs.map((s) => ({ ...s }));
      config.save({ subscriptions: subs });
      return subs;
    }

    case '/api/publish':
      if (!body.topic) throw new Error('缺少 topic');
      await bridge.publish(body.topic, body.payload ?? '', body.qos, body.retain);
      return { published: true, topic: body.topic };

    case '/api/topics':
      return bridge.getTopics();

    // 某主题的历史（前端刷新页面后回填曲线用）
    // 优先读落盘库（能回溯到保留天数内的数据），库不可用或库里没有才退回内存
    //
    // windowMin：给了就由服务端算窗口起点（锚在"库里该主题最新的数据时间"，
    // 不是服务器的当前时间 —— 设备停发时窗口才不会一直往前滑、把数据全甩出去）。
    // 这样前端只要说「我要最近 1 小时」，服务端直接把那一小时的数据给它。
    //
    // sample=1：limit 从「最多多少条」变成「最多多少个点」——
    // 区间里数据比 limit 多时**整段均匀抽样**，而不是只给最新的一段。
    // 不这样的话，1 小时里有几万条数据时曲线还是只画出最后几分钟。
    case '/api/history': {
      const topic = url.searchParams.get('topic');
      if (!topic) throw new Error('缺少 topic 参数');
      let limit = Number(url.searchParams.get('limit')) || 100;
      let since = Number(url.searchParams.get('since')) || 0;
      const windowMin = Math.max(0, Number(url.searchParams.get('windowMin')) || 0);
      const sample = url.searchParams.get('sample') === '1';
      if (history?.available) {
        if (windowMin > 0) {
          const span = history.topicSpan(topic);
          if (span) since = span.lastTs - windowMin * 60000;
        }
        const rows = history.query(topic, { limit, since, sample });
        if (rows.length > 0) return rows;
      }
      return bridge.getHistory(topic, limit);
    }

    /**
     * 某主题「库里有多少数据 / 缓冲能覆盖多久 / 选定窗口内有多少点」。
     * 曲线显示不全时，界面要能给出确切的数字，而不是让用户对着一条短曲线猜。
     */
    case '/api/history/span': {
      const topic = url.searchParams.get('topic');
      if (!topic) throw new Error('缺少 topic 参数');
      const windowMin = Math.max(0, Number(url.searchParams.get('windowMin')) || 0);
      const db = history?.available ? history.topicSpan(topic) : null;
      const mem = bridge.topicSpan(topic);

      // 两边的窗口起点分开算：库里的锚点用库的最新时间，内存的锚点用内存的最新时间
      const memSince = (windowMin > 0 && mem) ? mem.lastTs - windowMin * 60000 : 0;
      const memWithWin = bridge.topicSpan(topic, memSince);

      return {
        topic,
        windowMin,
        db,                                                  // { count, firstTs, lastTs } 或 null
        mem: memWithWin,                                     // 同上，外加 inWindow
        dbSince: (windowMin > 0 && db) ? db.lastTs - windowMin * 60000 : null,
        dbInWindow: (windowMin > 0 && db)
          ? history.countSince(topic, db.lastTs - windowMin * 60000)
          : null,
      };
    }

    // ---------------- 历史数据（落盘） ----------------

    case '/api/history/stats':
      return history ? history.stats() : { enabled: false, available: false, reason: '未启用落盘' };

    case '/api/history/topics':
      return history ? history.topics(Number(url.searchParams.get('limit')) || 100) : [];

    case '/api/history/keep-days': {
      if (!history) throw new Error('未启用落盘');
      const r = history.setKeepDays(body.keepDays);
      config.save({ history: { keepDays: r.keepDays } });
      return { ...r, stats: history.stats() };
    }

    case '/api/history/clear': {
      if (!history) throw new Error('未启用落盘');
      const r = history.clear(body.topic || null);
      // 库清了，界面上的曲线也该跟着清（内存缓冲与画面一起复位）
      if (!body.topic) {
        bridge.reset();
        ctx.hub.broadcast('reset', {});
      }
      return { ...r, stats: history.stats() };
    }

    case '/api/history/export': {
      if (!history) throw new Error('未启用落盘');
      const topic = url.searchParams.get('topic') || '';
      const days = url.searchParams.get('days');
      const max = url.searchParams.get('max');
      const { csv, rows } = history.exportCsv({ topic, days, limit: max });
      const stamp = new Date().toISOString().slice(0, 19).replace(/[:T]/g, '-');
      return {
        __raw: true,
        contentType: 'text/csv; charset=utf-8',
        filename: `mqtt-history-${stamp}.csv`,
        body: csv,
        rows,
      };
    }

    // 保存看板卡片布局与阈值
    case '/api/cards': {
      const cards = (Array.isArray(body.cards) ? body.cards : []).map((c) => {
        // 兼容老配置：以前是单个 field 字符串，现在统一成可多选的 fields 数组
        const list = Array.isArray(c.fields) ? c.fields : (c.field ? [c.field] : []);
        const fields = [...new Set(list.map((f) => String(f || '').trim()).filter(Boolean))].slice(0, 8);
        const { field, ...rest } = c;
        return { ...rest, fields };
      });
      config.save({ cards });
      return cards;
    }

    // 通用 UI 配置
    case '/api/ui': {
      const ui = body.ui || {};
      config.save({ ui });
      return config.load().ui;
    }

    // ---------------- 内置 Broker ----------------

    case '/api/broker':
      return broker.getSnapshot();

    case '/api/broker/start': {
      const port = Number(body.port) || config.load().broker.port || 1883;
      const username = body.username ?? config.load().broker.username ?? '';
      const password = body.password ?? config.load().broker.password ?? '';
      const started = await broker.start({ port, username, password });
      config.save({ broker: { enabled: true, port: started.port, username, password } });

      // 按配置自动把平台接到本地 broker，设备一发数据看板就能看到
      if (config.load().broker.autoConnect !== false) {
        const conn = {
          ...config.load().connection,
          protocol: 'mqtt', host: '127.0.0.1', port: started.port, username, password,
        };
        config.save({ connection: conn });
        bridge.connect(conn, config.load().subscriptions);
      }
      return broker.getSnapshot();
    }

    case '/api/broker/stop':
      await broker.stop();
      config.save({ broker: { enabled: false } });
      return broker.getSnapshot();

    // 让平台自身连接到本地 broker（一键接入）
    case '/api/broker/connect-self': {
      const snap = broker.getSnapshot();
      if (!snap.running) throw new Error('内置 Broker 未运行，请先启动');
      const cfg = config.load();
      const conn = {
        ...cfg.connection,
        protocol: 'mqtt',
        host: '127.0.0.1',
        port: snap.port,
        username: cfg.broker.username || '',
        password: cfg.broker.password || '',
      };
      config.save({ connection: conn });
      bridge.connect(conn, cfg.subscriptions);
      return { connection: conn, snapshot: bridge.getSnapshot() };
    }

    // 数据模拟器开关
    case '/api/broker/sim': {
      const action = body.action;
      if (action === 'start') {
        const r = broker.simStart({ intervalMs: Number(body.intervalMs) || 1000 });
        config.save({ broker: { simEnabled: true, simIntervalMs: r.intervalMs } });
        return { ...r, broker: broker.getSnapshot() };
      }
      broker.simStop();
      config.save({ broker: { simEnabled: false } });
      return { running: false, broker: broker.getSnapshot() };
    }

    // 踢掉某个设备连接
    case '/api/broker/disconnect': {
      if (!body.clientId) throw new Error('缺少 clientId');
      const ok = broker.disconnectClient(body.clientId);
      if (!ok) throw new Error('该客户端已不在线');
      return broker.getSnapshot();
    }

    // 清空历史与统计（内存 + 落盘的库；看板卡片保留）
    case '/api/reset': {
      bridge.reset();
      let purged = 0;
      if (history?.available) purged = history.clear().deleted;
      ctx.hub.broadcast('reset', {});
      return { reset: true, purgedRows: purged };
    }

    // 服务端信息
    case '/api/info':
      return {
        pid: process.pid,
        node: process.version,
        platform: process.platform,
        startedAt: ctx.startedAt,
        configPath: require('./configStore').CONFIG_PATH,
      };

    default:
      return undefined;
  }
}

module.exports = { createServer };

'use strict';

/**
 * 历史数据落盘（SQLite）
 *
 * 为什么单独一层：进程内的环形缓冲（mqttBridge.history）只在内存里，
 * 平台一重启、页面一刷新就没了。这里把每条消息追加写进 SQLite，
 * 于是「曲线能回溯多久」不再受内存大小限制，而是由保留天数决定。
 *
 * 要点：
 * - 用 Node 22 自带的 node:sqlite，**零额外依赖**（不用装原生模块）。
 *   若当前 Node 版本没有该模块，自动降级为「不落盘」，平台照常可用。
 * - 写入是攒批的（默认 400ms 或 500 条一次事务），避免每条消息都 fsync。
 * - 保留策略两条线：按天数（keepDays）+ 按行数上限（maxRows）兜底防爆盘。
 * - 读接口前会先把队列刷干净，保证「刚到的数据立刻能查回来」。
 */

const fs = require('fs');
const path = require('path');
const { pickNumericField } = require('./mqttBridge');

const DEFAULT_DIR = path.join(__dirname, '..', 'data');
const BATCH_MS = 400;
const BATCH_MAX = 500;
const PRUNE_INTERVAL_MS = 30 * 60 * 1000;
const EXPORT_MAX_ROWS = 200000;

/** 静音 node:sqlite 的实验性特性警告（只过滤这一条，别的警告照常打印） */
let sqlite = null;
try {
  const emitWarning = process.emitWarning;
  process.emitWarning = function patched(warning, ...rest) {
    if (String(warning).includes('SQLite is an experimental feature')) return;
    return emitWarning.call(process, warning, ...rest);
  };
  sqlite = require('node:sqlite');
} catch {
  sqlite = null;
}

/** 载荷看起来是 JSON 就解析出来，前端回填曲线时要用 */
function parseMaybeJson(text) {
  if (!text) return null;
  const t = String(text).trim();
  if (!t.startsWith('{') && !t.startsWith('[')) return null;
  try { return JSON.parse(t); } catch { return null; }
}

class HistoryStore {
  constructor(opts = {}) {
    this.enabled = opts.enabled !== false;
    // 环境变量优先：便于在不影响正式库的前提下起临时实例做验证
    const dir = process.env.MQTT_MONITOR_DATA_DIR || opts.dir;
    this.dir = dir ? path.resolve(dir) : DEFAULT_DIR;
    this.dbPath = path.join(this.dir, 'history.db');
    this.keepDays = Number(opts.keepDays) > 0 ? Number(opts.keepDays) : 7;
    this.maxRows = Number(opts.maxRows) > 0 ? Number(opts.maxRows) : 2000000;

    this.db = null;
    this.available = false;
    this.reason = '';           // 不可用原因（供界面显示）

    this.queue = [];
    this.timer = null;
    this.written = 0;           // 本次进程写入行数
    this.lastError = '';
    this._stmts = {};
    this._pruneTimer = null;
  }

  /** 打开数据库并建表；失败不抛错，只降级 */
  open() {
    if (!this.enabled) { this.reason = '配置里已关闭落盘'; return false; }
    if (!sqlite) { this.reason = '当前 Node 版本没有 node:sqlite（需 Node 22.5+）'; return false; }
    try {
      fs.mkdirSync(this.dir, { recursive: true });
      this.db = new sqlite.DatabaseSync(this.dbPath);
      this.db.exec('PRAGMA journal_mode = WAL');
      this.db.exec('PRAGMA synchronous = NORMAL');
      this.db.exec('PRAGMA busy_timeout = 4000');
      this.db.exec(`
        CREATE TABLE IF NOT EXISTS samples (
          id      INTEGER PRIMARY KEY AUTOINCREMENT,
          topic   TEXT    NOT NULL,
          ts      INTEGER NOT NULL,
          num     REAL,
          payload TEXT    NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_samples_topic_ts ON samples (topic, ts);
        CREATE INDEX IF NOT EXISTS idx_samples_ts       ON samples (ts);
      `);
      this._stmts.insert = this.db.prepare(
        'INSERT INTO samples (topic, ts, num, payload) VALUES (?, ?, ?, ?)',
      );
      this._stmts.lastTs = this.db.prepare('SELECT MAX(ts) t FROM samples');
      this._stmts.count = this.db.prepare('SELECT COUNT(*) c FROM samples');
      this._stmts.countTopic = this.db.prepare('SELECT COUNT(*) c FROM samples WHERE topic = ?');
      this._stmts.topicSpan = this.db.prepare(
        'SELECT COUNT(*) c, MIN(ts) a, MAX(ts) b FROM samples WHERE topic = ?',
      );
      this._stmts.countSince = this.db.prepare(
        'SELECT COUNT(*) c FROM samples WHERE topic = ? AND ts >= ?',
      );
      this._stmts.range = this.db.prepare('SELECT MIN(ts) a, MAX(ts) b FROM samples');
      this._stmts.topics = this.db.prepare(
        'SELECT topic, COUNT(*) c, MAX(ts) t FROM samples GROUP BY topic ORDER BY t DESC',
      );
      this._stmts.pruneDays = this.db.prepare('DELETE FROM samples WHERE ts < ?');
      this.available = true;
      this.reason = '';
      this.prune(true);
      this._pruneTimer = setInterval(() => this.prune(), PRUNE_INTERVAL_MS);
      this._pruneTimer.unref?.();
      return true;
    } catch (err) {
      this.lastError = err.message;
      this.reason = `打开失败：${err.message}`;
      this.available = false;
      this.db = null;
      return false;
    }
  }

  /** 记一条消息（入队，稍后批量落盘） */
  record(rec) {
    if (!this.available || !rec || !rec.topic) return;
    this.queue.push(rec);
    if (this.queue.length >= BATCH_MAX) { this.flushNow(); return; }
    if (!this.timer) this.timer = setTimeout(() => this.flushNow(), BATCH_MS);
    this.timer.unref?.();
  }

  /** 立即落盘（读接口 / 退出前调用，保证读得到刚写的数据） */
  flushNow() {
    if (this.timer) { clearTimeout(this.timer); this.timer = null; }
    if (!this.available || this.queue.length === 0) return;
    const rows = this.queue;
    this.queue = [];
    try {
      this.db.exec('BEGIN');
      for (const r of rows) {
        this._stmts.insert.run(r.topic, r.ts, r.num ?? null, r.payload ?? '');
      }
      this.db.exec('COMMIT');
      this.written += rows.length;
    } catch (err) {
      this.lastError = err.message;
      try { this.db.exec('ROLLBACK'); } catch { /* ignore */ }
      console.error(`[history] 落盘失败：${err.message}（本批 ${rows.length} 条已丢弃）`);
    }
  }

  /**
   * 查某主题历史，返回与内存记录同构的对象（前端直接可用）。
   * @param {object} opt { limit, since, until, desc, sample }
   *
   * sample = true 时 limit 的含义从「最多返回多少条（超了只给最新的一段）」变成
   * 「分辨率上限」：整段区间**均匀抽样**到约 limit 个点，首尾一定保留。
   * 这是为了"曲线显示不全" —— 1 小时的窗口里有 3.6 万条时，
   * 只取最新 5000 条等于只画了最后 8 分钟。
   */
  query(topic, opt = {}) {
    if (!this.available || !topic) return [];
    this.flushNow();
    const limit = Math.min(Math.max(Number(opt.limit) || 100, 1), 200000);
    let sql = 'SELECT id, ts, num, payload FROM samples WHERE topic = ?';
    const args = [topic];
    if (opt.since) { sql += ' AND ts >= ?'; args.push(Number(opt.since)); }
    if (opt.until) { sql += ' AND ts <= ?'; args.push(Number(opt.until)); }

    if (opt.sample) {
      const sampled = this._querySampled(sql, args, limit);
      if (sampled) return sampled.map((r) => this._toRecord(topic, r));
      // 抽样查询失败（老 SQLite 没有窗口函数）→ 退回下面"只取最新一段"的老行为
    }

    try {
      const rows = this.db.prepare(`${sql} ORDER BY ts DESC, id DESC LIMIT ?`).all(...args, limit);
      rows.reverse();                       // 转成时间升序，前端画曲线用
      return rows.map((r) => this._toRecord(topic, r));
    } catch (err) {
      this.lastError = err.message;
      console.error(`[history] 查询失败：${err.message}`);
      return [];
    }
  }

  /**
   * 在 [since, until] 区间里均匀抽 limit 个点（首尾必留）。
   * @returns {Array|null} null 表示抽样查询不可用，调用方退回普通查询
   */
  _querySampled(baseSql, args, limit) {
    try {
      const total = this.db.prepare(`SELECT COUNT(*) c FROM (${baseSql})`).get(...args).c;
      if (!total) return [];
      if (total <= limit) {
        return this.db.prepare(`${baseSql} ORDER BY ts ASC, id ASC`).all(...args);
      }
      // 每隔 stride 行取一行。注意"每 k 行取一行"取出来的条数 ≈ total/stride，
      // 向上取整会让条数明显少于 limit（total=3001、limit=500 时 stride=7 只取到 429 个），
      // 所以先算一下能取到多少，太少就把 stride 降一档。
      let stride = Math.max(1, Math.ceil(total / limit));
      if (stride > 1 && (1 + Math.floor(total / stride)) < limit) stride -= 1;
      const rows = this.db.prepare(
        `SELECT ts, num, payload FROM (
           SELECT ts, num, payload,
                  ROW_NUMBER() OVER (ORDER BY ts ASC, id ASC) AS rn
           FROM (${baseSql})
         )
         WHERE rn = 1 OR rn % ? = 0
         ORDER BY ts ASC`,
      ).all(...args, stride);

      // 最新那条一定要在 —— 曲线右端就是「当前值」，缺了会看着像停更
      const last = this.db.prepare(`${baseSql} ORDER BY ts DESC, id DESC LIMIT 1`).get(...args);
      if (last && (!rows.length || rows[rows.length - 1].ts < last.ts)) rows.push(last);
      return rows;
    } catch (err) {
      this.lastError = err.message;
      console.warn(`[history] 均匀抽样不可用，退回"只取最新一段"：${err.message}`);
      return null;
    }
  }

  _toRecord(topic, r) {
    return {
      topic,
      ts: r.ts,
      payload: r.payload,
      num: r.num === null || r.num === undefined ? null : r.num,
      json: parseMaybeJson(r.payload),
      binary: false,
      qos: 0,
      retain: false,
      size: Buffer.byteLength(r.payload || '', 'utf8'),
    };
  }

  /**
   * 把库里 num 为 NULL 的 JSON 报文补上一个数值（导出的 CSV 才有「数值」列）。
   *
   * 老版本只把「裸数字」报文认成数值，设备这种
   * {"tag":"电池电量","dataType":"Int16","value":"0",…} 落下来 num 全是 NULL。
   * **默认只统计不写库**，必须显式传 apply: true 才真的 UPDATE —— 这是用户的历史数据，
   * 不能因为版本升级就悄悄改动。
   *
   * @param {object} opt { apply=false, limit=200000 }
   * @returns {{scanned, fillable, updated, examples: string[]}}
   */
  backfillNum(opt = {}) {
    if (!this.available) throw new Error(this.reason || '历史库不可用');
    this.flushNow();
    const apply = opt.apply === true;
    const limit = Math.max(1, Number(opt.limit) || 200000);
    const rows = this.db.prepare(
      "SELECT id, payload FROM samples WHERE num IS NULL AND payload LIKE '{%' LIMIT ?",
    ).all(limit);

    const filled = [];
    for (const r of rows) {
      const json = parseMaybeJson(r.payload);
      if (!json) continue;
      const n = pickNumericField(json);
      if (n === null) continue;
      filled.push({ id: r.id, num: n });
    }

    let updated = 0;
    if (apply && filled.length) {
      const stmt = this.db.prepare('UPDATE samples SET num = ? WHERE id = ?');
      this.db.exec('BEGIN');
      try {
        for (const f of filled) { stmt.run(f.num, f.id); updated += 1; }
        this.db.exec('COMMIT');
      } catch (err) {
        try { this.db.exec('ROLLBACK'); } catch { /* ignore */ }
        throw err;
      }
    }
    return {
      scanned: rows.length,
      fillable: filled.length,
      updated,
      examples: filled.slice(0, 3).map((f) => `id=${f.id} → num=${f.num}`),
    };
  }

  /** 每个主题在库里的统计（用于界面展示） */
  topics(limit = 100) {
    if (!this.available) return [];
    try {
      return this._stmts.topics.all().slice(0, limit).map((r) => ({
        topic: r.topic, count: r.c, lastTs: r.t,
      }));
    } catch { return []; }
  }

  /**
   * 某主题在库里的条数与时间跨度。
   * 前端拿它回答两个问题：「这条曲线库里到底有多少数据」「我选的窗口内大概有多少点」——
   * 不然用户只会看到「曲线显示不全」却不知道为什么。
   * 库不可用 / 没这个主题 → null（前端退回内存缓冲的估算）。
   */
  topicSpan(topic) {
    if (!this.available || !topic) return null;
    this.flushNow();
    try {
      const r = this._stmts.topicSpan.get(topic);
      if (!r || !r.c) return null;
      return { count: r.c, firstTs: r.a, lastTs: r.b };
    } catch (err) {
      this.lastError = err.message;
      return null;
    }
  }

  /** 某主题从 since 起的条数（用于判断窗口内有多少点） */
  countSince(topic, since) {
    if (!this.available || !topic) return 0;
    try {
      return this._stmts.countSince.get(topic, Number(since) || 0).c || 0;
    } catch { return 0; }
  }

  stats() {
    // 先把队列刷干净：这样界面读到的「待落盘」和「总条数」不会自相矛盾
    this.flushNow();
    const out = {
      enabled: this.enabled,
      available: this.available,
      reason: this.reason,
      dbPath: this.dbPath,
      dir: this.dir,
      keepDays: this.keepDays,
      maxRows: this.maxRows,
      rows: 0,
      topics: 0,
      oldest: null,
      newest: null,
      bytes: 0,
      writtenThisRun: this.written,
      pendingRows: this.queue.length,
      lastError: this.lastError,
      keepDaysOptions: [1, 3, 7, 15, 30, 90, 365],
    };
    if (!this.available) return out;
    try {
      out.rows = this._stmts.count.get().c;
      const range = this._stmts.range.get();
      out.oldest = range.a ?? null;
      out.newest = range.b ?? null;
      out.topics = this._stmts.topics.all().length;
      // 主库 + WAL 一起算，才是真实占用
      for (const f of [this.dbPath, `${this.dbPath}-wal`, `${this.dbPath}-shm`]) {
        try { out.bytes += fs.statSync(f).size; } catch { /* 不存在就跳过 */ }
      }
    } catch (err) {
      out.lastError = err.message;
    }
    return out;
  }

  /** 保留策略：先按天数删，再按行数封顶 */
  prune(force) {
    if (!this.available) return { deleted: 0 };
    const now = Date.now();
    if (!force && now - (this._lastPrune || 0) < 60 * 1000) return { deleted: 0 };
    this._lastPrune = now;
    this.flushNow();
    let deleted = 0;
    try {
      const cutoff = now - this.keepDays * 86400000;
      deleted += this._stmts.pruneDays.run(cutoff).changes || 0;

      const total = this._stmts.count.get().c;
      if (total > this.maxRows) {
        const drop = total - this.maxRows;
        // 按 id 升序删最旧的 drop 行（id 单调递增，等价于按时间删最旧）
        const r = this.db.prepare(
          'DELETE FROM samples WHERE id IN (SELECT id FROM samples ORDER BY id ASC LIMIT ?)',
        ).run(drop);
        deleted += r.changes || 0;
      }
      if (deleted > 0) {
        console.log(`[history] 保留策略清掉 ${deleted} 条（保留 ${this.keepDays} 天 / 上限 ${this.maxRows} 行）`);
      }
    } catch (err) {
      this.lastError = err.message;
      console.error(`[history] 清理失败：${err.message}`);
    }
    return { deleted };
  }

  /** 清空（可按主题） */
  clear(topic) {
    if (!this.available) return { deleted: 0 };
    this.queue = [];
    try {
      const r = topic
        ? this.db.prepare('DELETE FROM samples WHERE topic = ?').run(topic)
        : this.db.prepare('DELETE FROM samples').run();
      this.db.exec('VACUUM');
      return { deleted: r.changes || 0 };
    } catch (err) {
      this.lastError = err.message;
      throw new Error(`清空历史库失败：${err.message}`);
    }
  }

  /**
   * 导出 CSV（带 BOM，Excel 直接打开不乱码）。
   * @param {object} opt { topic, days, limit }
   */
  exportCsv(opt = {}) {
    if (!this.available) throw new Error(this.reason || '历史库不可用');
    this.flushNow();
    const limit = Math.min(Number(opt.limit) || EXPORT_MAX_ROWS, EXPORT_MAX_ROWS);
    const since = opt.days ? Date.now() - Number(opt.days) * 86400000 : (opt.since || null);
    let sql = 'SELECT topic, ts, num, payload FROM samples';
    const args = [];
    const where = [];
    if (opt.topic) { where.push('topic = ?'); args.push(opt.topic); }
    if (since) { where.push('ts >= ?'); args.push(Number(since)); }
    if (where.length) sql += ' WHERE ' + where.join(' AND ');
    sql += ' ORDER BY ts ASC LIMIT ?';
    args.push(limit);

    const rows = this.db.prepare(sql).all(...args);
    const esc = (v) => {
      const s = v === null || v === undefined ? '' : String(v);
      return /[",\n\r]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
    };
    const lines = ['时间,主题,数值,原始载荷'];
    for (const r of rows) {
      lines.push([
        new Date(r.ts).toLocaleString('zh-CN', { hour12: false }),
        r.topic,
        r.num === null ? '' : r.num,
        r.payload,
      ].map(esc).join(','));
    }
    return { csv: '\uFEFF' + lines.join('\r\n') + '\r\n', rows: rows.length };
  }

  setKeepDays(days) {
    const n = Number(days);
    if (!Number.isFinite(n) || n <= 0) throw new Error('保留天数必须是正数');
    this.keepDays = Math.min(Math.round(n), 3650);
    const r = this.prune(true);
    return { keepDays: this.keepDays, deleted: r.deleted };
  }

  close() {
    if (this._pruneTimer) clearInterval(this._pruneTimer);
    this.flushNow();
    try { this.db?.close(); } catch { /* ignore */ }
    this.db = null;
    this.available = false;
  }
}

module.exports = { HistoryStore, DEFAULT_HISTORY_DIR: DEFAULT_DIR };

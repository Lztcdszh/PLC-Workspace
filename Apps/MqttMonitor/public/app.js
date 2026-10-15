/* ============================================================
   MQTT 实时数据监视平台 —— 前端逻辑
   零依赖：原生 WebSocket + Canvas 绘图
   ============================================================ */
(function () {
  'use strict';

  // ---------------- 常量 ----------------
  const WS_RETRY_MIN = 1500;
  const WS_RETRY_MAX = 10000;
  const FLUSH_RENDER_MS = 120;   // 画面渲染节流
  const STALE_MS = 15000;        // 离线判定的兜底下限（拿不到上报节拍时用它）
  /**
   * 离线判定要按设备**实测上报节拍**来，不能写死 15 秒。
   *
   * 现场 PLC 是 60~80 秒上报一次，写死 15 秒的后果是：每来一条报文，
   * 过后 15 秒卡片就变灰显示「无数据」，剩下 80% 的时间都挂着这个标签 ——
   * 用户以为平台没在收数据，其实曲线上的点一条没少。
   * 所以取最近若干个间隔的中位数 × 3（容忍连丢 2 个包），并夹在上下限之间：
   * 再快也不低于 15 秒（防抖动），再慢也不超过 5 分钟（真断了要看得出来）。
   */
  const STALE_FACTOR = 3;
  const STALE_SAMPLE = 12;                // 用最近多少个间隔估节拍
  const STALE_MAX_MS = 5 * 60 * 1000;
  const AUTO_CARD_LIMIT = 24;    // 自动上板的最大卡片数
  const LOG_KEEP_MAX = 5000;     // 导出用的日志保留上限
  const MAX_SERIES = 6;          // 「自动」模式下一张卡片最多画几条曲线（避免糊成一团）
  const MAX_SELECT = 8;          // 手动勾选时的上限（配色表长度）
  /**
   * 一张卡片 = 一个主题，但同一个主题下可以有**多个测点**。
   *
   * 现场网关常把一台设备的点位全发到一个主题里，靠报文里的身份字段区分：
   *   {"tag":"小车状态","address":"DB1.DBW230","value":"1"}
   *   {"tag":"设定位置","address":"DB1.DBW250","value":"26"}
   * 这两条报文属于两个测点，应该在同一张卡片上画成两条曲线 —— 用户还能勾选只画哪几个。
   *
   * 所以卡片有两种出曲线上限的方式：
   *   ① 测点模式（有身份字段时优先）：一条身份值 = 一条曲线，key 带 ITEM_PREFIX 前缀；
   *   ② 字段模式（没有身份字段时）：一条数值字段 = 一条曲线，key 就是字段路径。
   * 两者不冲突：勾选列表里同时列出，各自画各自的。
   */
  const ITEM_PREFIX = '__item__:';
  const ID_FIELD_CANDIDATES = ['tag', 'address', 'name', 'id', 'key', 'signal', 'point', 'code', 'param'];
  // 测点模式下，"这个测点这次的值"从哪个字段取（和落盘库 num 列的优先级保持一致）
  const VALUE_FIELD_PRIORITY = ['value', 'val', 'v', 'num', 'pv', 'reading', 'data'];

  /**
   * 第三种卡片：**分组卡片**（一台设备一张卡）。
   *
   * 同一个 PLC 的多个点位常常发在**不同的主题**上：
   *   plc/DB1.DBW290 = 电池电量 、plc/DB1.DBW292 = 电池电流
   * 默认一张卡片只管一个主题，于是同一台设备的电压电流被拆到两张卡上、时间轴对不齐。
   * 分组卡片把「主题前缀」当成一台设备：`plc/*` 下的所有主题进同一张卡，每个主题一条曲线。
   *
   * 曲线 key = TOPIC_PREFIX + 主题 + TOPIC_SEP + 字段键（字段键与单主题那张卡的 key 同构：
   * `__item__:电池电量` / `value` / `__v`），所以"每个主题画哪个 tag"可以单独切换。
   */
  const TOPIC_PREFIX = '__topic__:';
  const TOPIC_SEP = '|';

  /**
   * 归一「组前缀」。用户在主题框里怎么写的都有，这些写法必须是同一张卡：
   *   `plc`  `plc/`  `plc/*`  `plc/#`  `plc/+`
   * 不归一的话 `plc/*` 会当成字面前缀，`plc/*` + `/` 谁都匹配不上 ——
   * 卡片建出来了、副标题也对，但一条曲线都不出，最容易被当成"没收到数据"。
   */
  function groupRoot(topic) {
    let t = String(topic || '').trim();
    for (;;) {
      const before = t;
      t = t.replace(/\/+$/, '');        // 尾斜杠：plc/ → plc
      t = t.replace(/\/[#*+]$/, '');    // 通配符尾：plc/* 、plc/# → plc
      if (t === before) break;
    }
    return t;
  }
  /** 分组卡片的「组内主题」判定：前缀 + `/`，避免 `plc` 误收 `plc2/...` */
  function inGroupOf(root, topic) {
    const r = groupRoot(root);
    return !!r && typeof topic === 'string' && topic.startsWith(r + '/');
  }

  /**
   * 主题的父路径（自动合并的判据）：
   *   plc/DB1.DBW290 → plc      一台设备的点位发在 plc/ 下
   *   a/b/c          → a/b      多层也是一样，归到直接父级
   *   裸主题(没有 /) → ''       没法判断属于哪台设备，不参与自动合并
   */
  function parentTopic(topic) {
    const t = String(topic || '');
    const i = t.lastIndexOf('/');
    return i > 0 ? t.slice(0, i) : '';
  }
  function isTopicKey(k) {
    return typeof k === 'string' && k.startsWith(TOPIC_PREFIX);
  }
  function topicKeyOf(k) {
    if (!isTopicKey(k)) return '';
    const s = k.slice(TOPIC_PREFIX.length);
    const i = s.indexOf(TOPIC_SEP);
    return i < 0 ? s : s.slice(0, i);
  }
  function fieldKeyOf(k) {
    if (!isTopicKey(k)) return '';
    const s = k.slice(TOPIC_PREFIX.length);
    const i = s.indexOf(TOPIC_SEP);
    return i < 0 ? '' : s.slice(i + 1);
  }
  function topicKey(topic, fieldKey) {
    return TOPIC_PREFIX + topic + TOPIC_SEP + (fieldKey || '__v');
  }

  /**
   * 曲线「显示多久」只由 X 轴时间窗口决定，不由点数决定。
   * 点数上限只是个安全阀：设备 1Hz 跑一天就是 8.6 万个点，全塞进 DOM/Canvas 会卡。
   * 所以超出上限时是**降采样**（均匀抽稀，仍然铺满整个窗口），不是把前面对半截丢掉。
   */
  const POINTS_HARD_MAX = 20000; // 每条曲线在内存里最多留多少点（安全阀）
  const POINTS_DEFAULT = 0;      // 0 = 不限（画多少个点交给画布像素和下面这个上限兜底）
  const FETCH_MAX = 5000;        // 一次从落盘库取多少个点（区间里更多就在服务端均匀抽样）
  const PX_PER_POINT = 4;        // 画布横向 1px 最多摆几个点，再多也是重复像素

  /** 多曲线配色：第一条沿用主题蓝，其余按顺序取 */
  const SERIES_COLORS = ['#22d3ee', '#a78bfa', '#34d399', '#fbbf24', '#f472b6', '#60a5fa', '#f87171', '#2dd4bf'];

  // ---------------- 全局状态 ----------------
  const S = {
    ws: null,
    wsRetry: WS_RETRY_MIN,
    mqttStatus: 'disconnected',
    paused: false,
    theme: 'light',
    autoAdd: true,
    // 同前缀自动合并：一台设备的点位常分开发在不同主题上（plc/DB1.DBW290 电量、
    // plc/DB1.DBW292 电流），默认把它们并成一张卡，而不是各占一张。
    autoGroup: true,
    cols: 3,

    cards: [],          // 卡片定义（含运行时数据）
    cardMap: new Map(), // topic -> card
    topics: new Map(),  // topic -> {topic, count, lastTs, lastPayload, lastNum, historyLen}

    logs: [],           // 最近的日志记录（倒序无关，按到达顺序）
    logPaused: false,
    logFilter: '',

    subs: [],           // [{topic, qos}]
    topicFilter: '',

    stats: { rate: 0, totalMessages: 0, topicCount: 0, uptimeMs: 0, bytesReceived: 0, connectedAt: null },
    // chartPoints 已废弃：曲线显示多久由 chartWindowMin（X 轴时间窗口）决定，
    // 点数上限只在「测点设置」里逐张卡片设，默认不限。这里保留字段只为兼容老配置。
    ui: { maxLogRows: 300, chartPoints: 0, chartWindowMin: 0 },

    editingCard: null,
    renderTimer: null,
    dirtyCanvas: new Set(),
    backfilled: new Set(),   // 已经补过历史的「卡片|窗口|起点|主题」，避免重复打接口
    restoringCards: false,   // 正在按配置重建看板卡片（这期间进来的报文先缓存）
    pendingMsgs: [],         // 上一条注释说的缓存

    broker: null,       // 内置 Broker 台账
    history: null,      // 历史库统计（落盘）
    catalog: [],        // 模拟器测点定义（用于一键生成看板）
  };

  // 测点定义索引：主题 -> {name, unit, high, low}
  let catalogMap = new Map();

  // ---------------- 工具函数 ----------------
  const $ = (id) => document.getElementById(id);

  function fmtTime(ts, withMs) {
    const d = new Date(ts);
    const p = (n, w) => String(n).padStart(w || 2, '0');
    let s = `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
    if (withMs) s += '.' + p(d.getMilliseconds(), 3);
    return s;
  }

  /**
   * X 轴时间标签：跨度一大，秒就是噪声（40 分钟的窗口里标 12:32:13 白占位置）；
   * 只有跨度在 1 分钟以内时才精确到秒。
   */
  function fmtAxisTime(ts, spanMs) {
    const full = fmtTime(ts);
    if (!Number.isFinite(spanMs) || spanMs < 60000) return full;
    return full.slice(0, 5);
  }

  function fmtUptime(ms) {
    if (!ms || ms < 0) return '--';
    const sec = Math.floor(ms / 1000);
    if (sec < 60) return sec + 's';
    const min = Math.floor(sec / 60);
    if (min < 60) return `${min}m${sec % 60}s`;
    const hr = Math.floor(min / 60);
    if (hr < 24) return `${hr}h${min % 60}m`;
    return `${Math.floor(hr / 24)}d${hr % 24}h`;
  }

  function fmtBytes(n) {
    if (n < 1024) return n + ' B';
    if (n < 1048576) return (n / 1024).toFixed(1) + ' KB';
    return (n / 1048576).toFixed(2) + ' MB';
  }

  /** 纯数字文本判定，用于原样展示设备上报的格式 */
  function isNumericText(s) {
    return /^[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?$/.test(String(s).trim());
  }

  /**
   * 带单位的数值写法："62.4℃" / "1.5 MPa" / "1500 r/min"。
   * 单位走白名单，避免把「3号工位」「1号线」这类文本当成数字 3 / 1。
   */
  const UNIT_NUM_RE = /^([+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?)\s*(℃|°C|%|mm|cm|km|m|MPa|kPa|Pa|bar|kg|g|t|rpm|r\/min|m3\/h|m³\/h|L\/min|Hz|kHz|kW|W|V|mV|A|mA|ms|min|s|h)$/i;

  function fmtNum(v) {
    if (!Number.isFinite(v)) return String(v);
    if (Number.isInteger(v)) return String(v);
    const a = Math.abs(v);
    if (a >= 1000) return v.toFixed(1);
    if (a >= 1) return v.toFixed(2);
    return v.toFixed(3);
  }

  /**
   * 判断一个值能不能作为曲线数据点。
   * - 数字直接用
   * - JSON 布尔 → 0/1（开关量、运行状态也能画成台阶曲线）
   * - **数字字符串也算**：PLC / 网关 / OPC 转发常把数值发成 "0"、"62.4"，
   *   不认这种就会「有值但画不出线、勾选列表里也没有」
   * - 时间戳、型号名之类的非纯数字字符串不认（"Int16" / "2026-09-24T…" 都会被排除）
   */
  function toPlotNum(v) {
    if (typeof v === 'number') return Number.isFinite(v) ? v : null;
    if (typeof v === 'boolean') return v ? 1 : 0;
    if (typeof v === 'string') {
      const s = v.trim();
      if (!s) return null;
      if (isNumericText(s)) {
        const n = Number(s);
        return Number.isFinite(n) ? n : null;
      }
      const m = UNIT_NUM_RE.exec(s);   // "62.4℃" 这类带单位的也认
      if (m) {
        const n = Number(m[1]);
        return Number.isFinite(n) ? n : null;
      }
      return null;
    }
    return null;
  }

  /**
   * 从 JSON 载荷里挖出所有可绘制的字段（数字 / 布尔 / 数字字符串）。
   * 往下钻两层，够覆盖 {"data":{"temp":1}} 这类常见结构。
   * 同时数一下有几个纯文本字段（画不了），用于界面提示。
   */
  function extractNumericFields(json, prefix, depth) {
    const out = [];
    const d = depth || 0;
    if (!json || typeof json !== 'object' || Array.isArray(json) || d > 1) return out;

    for (const [k, v] of Object.entries(json)) {
      const path = prefix ? `${prefix}.${k}` : k;
      if (Array.isArray(v)) continue;
      if (v !== null && typeof v === 'object') {
        const sub = extractNumericFields(v, path, d + 1);
        out.push(...sub);
        out.textCount = (out.textCount || 0) + (sub.textCount || 0);   // 嵌套层里的文本字段也要算上
        continue;
      }
      const num = toPlotNum(v);
      if (num === null) {
        // 纯文本字段（description / dataType / ts 之类），只计数不画
        out.textCount = (out.textCount || 0) + (v === null || v === undefined ? 0 : 1);
        continue;
      }
      const bool = typeof v === 'boolean';
      // 数字字符串保留原样显示（设备的 "62.40" / "007" 别被规整掉）
      const text = bool ? (num ? 'ON' : 'OFF') : (typeof v === 'string' ? v.trim() : fmtNum(num));
      out.push({ path, num, bool, text });
    }
    return out;
  }

  /** 给一张卡片分配下一个曲线颜色 */
  function nextColor(card) {
    return SERIES_COLORS[card.series.length % SERIES_COLORS.length];
  }

  /** 测点曲线的 key 带前缀，避免和字段路径撞名（比如某个 tag 恰好叫 "temp"） */
  function isItemKey(k) {
    return typeof k === 'string' && k.startsWith(ITEM_PREFIX);
  }
  function itemNameOf(k) {
    return isItemKey(k) ? k.slice(ITEM_PREFIX.length) : '';
  }

  /**
   * 这条报文的「身份字段」—— 决定它属于哪个测点。
   * 卡片显式指定了 groupBy 就只用它；否则按常见名依次找（tag 最常用，其次 address）。
   * 找不到 → null，说明这个主题是"一条报文多个数值字段"的形态，走字段模式。
   */
  function detectIdentity(json, groupBy) {
    if (!json || typeof json !== 'object' || Array.isArray(json)) return null;
    const take = (path) => {
      const v = getPath(json, path);
      if (v === null || v === undefined || typeof v === 'object') return null;
      const s = String(v).trim();
      return s === '' ? null : { field: path, name: s };
    };
    if (groupBy) return take(groupBy);
    for (const k of ID_FIELD_CANDIDATES) {
      const hit = take(k);
      if (hit) return hit;
    }
    return null;
  }

  /** 报文里名字最像"当前值"的那个字段的原始值（取不到返回 undefined） */
  function valueCandidate(json, idField) {
    for (const want of VALUE_FIELD_PRIORITY) {
      for (const k of Object.keys(json)) {
        if (k.toLowerCase() !== want || k === idField) continue;
        const v = json[k];
        if (v !== null && typeof v === 'object') continue;   // 嵌套对象不是"一个值"
        return v;
      }
    }
    return undefined;
  }

  /** 兜底：整个报文（去掉身份字段后）只剩一个数值字段，就认它 */
  function onlyNumericField(json, idField) {
    const rest = extractNumericFields(json).filter((f) => f.path !== idField);
    return rest.length === 1 ? rest[0] : null;
  }

  /**
   * 测点模式下「这个测点这次的数据」。
   * 数值优先取 value 之类的常见字段，取不到再退到"只剩一个数值字段"。
   * 都定不出数字时 num 给 null（这条画不了线），但 text 照样给出来 ——
   * 设备报 "运行" 这种文字值时，卡片上至少能看到文字，别让人以为数据丢了。
   */
  function itemSample(json, id) {
    const key = ITEM_PREFIX + id.name;
    const cand = valueCandidate(json, id.field);
    const num = cand === undefined ? null : toPlotNum(cand);
    if (num !== null) {
      const isBool = typeof cand === 'boolean';
      const text = isBool ? (num ? 'ON' : 'OFF') : (typeof cand === 'string' ? cand.trim() : fmtNum(num));
      return { key, label: id.name, num, text, bool: isBool };
    }
    if (cand !== undefined && cand !== null && typeof cand !== 'object') {
      const s = String(cand).trim();
      if (s) return { key, label: id.name, num: null, text: s };
    }
    const mv = onlyNumericField(json, id.field);
    if (mv) return { key, label: id.name, num: mv.num, text: mv.text, bool: mv.bool };
    return { key, label: id.name, num: null, text: '—' };
  }

  /**
   * 记下这个主题出现过的测点。
   * 必须"见过就记"，而不是"画了才记" —— 勾选列表要能列出所有测点，
   * 包括因为超过 MAX_SERIES 而暂时没画的那些。
   */
  function noteSeenItem(card, id, sample, ts) {
    if (!card.seenItems) card.seenItems = new Map();
    const prev = card.seenItems.get(id.name);
    card.seenItems.set(id.name, {
      name: id.name,
      num: sample ? sample.num : (prev ? prev.num : null),
      text: sample ? sample.text : (prev ? prev.text : '—'),
      bool: sample ? !!sample.bool : !!(prev && prev.bool),
      count: (prev ? prev.count : 0) + 1,
      lastTs: ts || (prev ? prev.lastTs : 0),
    });
  }

  /** 这张卡片当前画了几条测点曲线（用来算"还有几个测点没显示"） */
  function countItemSeries(card) {
    let n = 0;
    for (const s of card.series) if (isItemKey(s.key)) n += 1;
    return n;
  }

  /**
   * 给一个 key 建新曲线允不允许。
   *
   * 自动模式最多 MAX_SERIES 条 —— 一个主题几十个测点时不能糊成一团；
   * 手动勾了就是用户自己的选择（上限由勾选界面按 MAX_SELECT 挡），这里放行。
   *
   * 这个判断必须放在**真正建曲线的地方**，不能放在 pickSamples 里：
   * mergeHistory 是"先收集所有样本、再统一建曲线"，收集阶段 series.length
   * 一直是 0，放在那里上限会完全失效（8 个测点会画出 8 条）。
   */
  function seriesAllowed(card, key) {
    if (card.seriesMap.has(key)) return true;
    if (card.group) {
      // 分组卡：一个主题一条曲线。用户勾过就**只画勾上的**（没勾的不占名额），
      // 一个都没勾 = 自动，按成员顺序补到上限。
      if (card.series.length >= MAX_SERIES) return false;
      const on = card.picked || [];
      return on.length === 0 || on.indexOf(topicKeyOf(key)) >= 0;
    }
    if ((card.items || []).length || (card.fields || []).length) return true;
    return card.series.length < MAX_SERIES;
  }

  /** 自动模式下应该画哪几个测点：已经画着的优先保留，其余按见过的顺序补到上限 */
  function autoItemKeys(card) {
    const kept = [];
    const rest = [];
    for (const name of (card.seenItems ? card.seenItems.keys() : [])) {
      const k = ITEM_PREFIX + name;
      (card.seriesMap.has(k) ? kept : rest).push(k);
    }
    return [...kept, ...rest].slice(0, MAX_SERIES);
  }

  /**
   * 这张卡片当前是走"测点模式"还是"字段模式"。
   * 判据必须和 pickSamples 里的一致，否则两边会各建各的曲线 ——
   * 比如测点模式下又按字段建出一条空的 value 曲线（画不出来，却占着曲线位和颜色）。
   */
  function usesItemMode(card) {
    const json = card.rawJson;
    if (!json || typeof json !== 'object' || Array.isArray(json)) return false;
    const id = detectIdentity(json, card.groupBy);
    if (!id) return false;
    const smp = itemSample(json, id);
    return smp.num !== null || smp.text !== '—';
  }

  /**
   * 卡片图例上那个 "+N" —— 自动模式下还有多少条曲线没显示出来。
   * 两类都算：报文里有但超出 MAX_SERIES 的数值字段、见过但没画的测点。
   *
   * 手动勾选时一律不报：那是用户自己的选择，再挂个"+6"反而像是平台丢了数据。
   */
  function computeOverflow(card) {
    // 分组卡：图例一行一个主题、最多 MAX_SERIES 行 —— 没挤进去的那几个才是"+N"。
    // （和曲线数无关：没勾曲线的主题只显示值，但它照样占图例一行）
    if (card.group) return Math.max(0, groupMembers(card).length - MAX_SERIES);
    if ((card.items || []).length || (card.fields || []).length) return 0;
    return (card._overflowFields || 0)
      + Math.max(0, (card.seenItems ? card.seenItems.size : 0) - countItemSeries(card));
  }

  /**
   * 归一首选字段列表：新配置用 fields（数组，可多选），老配置只有 field（单个字符串）。
   * 两个都为空 = 自动模式（把 JSON 里所有数值字段都画出来）。
   */
  function normFields(fields, field) {
    const out = [];
    const push = (p) => {
      const s = String(p === null || p === undefined ? '' : p).trim();
      if (s && !out.includes(s)) out.push(s);
    };
    if (Array.isArray(fields)) fields.forEach(push);
    if (out.length === 0 && field) push(field);
    return out;
  }

  /** 字段在选单里的预览颜色（已建曲线沿用曲线的颜色） */
  function colorForField(card, path, index) {
    const ser = card.seriesMap.get(path);
    if (ser && ser.color) return ser.color;
    return SERIES_COLORS[index % SERIES_COLORS.length];
  }

  /** 按 "a.b.c" 路径从对象取值 */
  function getPath(obj, path) {
    if (!obj || !path) return undefined;
    const parts = String(path).split('.').filter(Boolean);
    let cur = obj;
    for (const p of parts) {
      if (cur === null || cur === undefined) return undefined;
      cur = cur[p];
    }
    return cur;
  }

  // ---------------- X 轴时间窗口 ----------------

  /** 毫秒 → “2 小时 14 分” 这种给人看的话 */
  function fmtDuration(ms) {
    const sec = Math.round(ms / 1000);
    if (sec < 60) return `${sec} 秒`;
    const min = Math.floor(sec / 60);
    if (min < 60) return `${min} 分钟`;
    const hr = Math.floor(min / 60);
    if (hr < 24) return `${hr} 小时 ${min % 60} 分`;
    const day = Math.floor(hr / 24);
    return `${day} 天 ${hr % 24} 小时`;
  }

  /**
   * 归一卡片的 windowMin：'' / null / undefined → null（跟随全局）；其余转成 >=0 的数字。
   * 注意 0 是有效值（显式"全部"），不能和 null 混为一谈。
   */
  function normWindow(val) {
    if (val === null || val === undefined || val === '') return null;
    const n = Number(val);
    return Number.isFinite(n) && n >= 0 ? n : null;
  }

  /** 窗口的短文案：“全部” / “30 分钟” / “1 小时” */
  function windowLabel(min) {
    const m = Number(min) || 0;
    if (m <= 0) return '全部';
    if (m < 60) return `${m} 分钟`;
    if (m < 1440) return `${Math.round((m / 60) * 10) / 10} 小时`;
    return `${Math.round((m / 1440) * 10) / 10} 天`;
  }

  /**
   * 这张卡片实际生效的 X 轴窗口（分钟），0 = 全部不限。
   * 卡片自己设过就用卡片的（包括显式选“全部”），否则跟随顶栏的全局默认值。
   */
  function effectiveWindowMin(card) {
    const own = card && card.windowMin;
    if (own !== null && own !== undefined && own !== '') return Math.max(0, Number(own) || 0);
    return Math.max(0, Number(S.ui.chartWindowMin) || 0);
  }

  /** 卡片所有曲线里最新的一个数据时间；取不到则退回 card.lastTs */
  function cardAnchorTs(card) {
    let t = 0;
    for (const s of card.series || []) {
      if (!s.ts || s.ts.length !== s.data.length || s.ts.length === 0) continue;
      const last = s.ts[s.ts.length - 1];
      if (last > t) t = last;
    }
    return t || card.lastTs || 0;
  }

  /**
   * 曲线的时间覆盖情况：跨度、点数、平均节拍。
   * 用来回答“我选了最近 1 小时，怎么只看到 2 分钟” —— 手上（内存缓冲 + 历史回补）
   * 到底有多少数据、铺开了多久，是这个问题的唯一事实依据。
   */
  function seriesCoverage(card) {
    let best = null;
    for (const s of card.series || []) {
      if (!s.ts || s.ts.length < 2 || s.ts.length !== s.data.length) continue;
      const span = s.ts[s.ts.length - 1] - s.ts[0];
      if (!best || span > best.span) {
        best = { span, n: s.ts.length, interval: span / (s.ts.length - 1), key: s.key };
      }
    }
    return best;
  }

  /**
   * 归一卡片的 points（点数上限）：'' / null / 0 / 负数 / 非数 → 0（不限）。
   * 上限再大也不超过 POINTS_HARD_MAX，防止手滑填个 99999999 把页面拖死。
   */
  function normPoints(val) {
    if (val === null || val === undefined || val === '') return POINTS_DEFAULT;
    const n = Math.floor(Number(val));
    if (!Number.isFinite(n) || n <= 0) return POINTS_DEFAULT;
    return Math.min(n, POINTS_HARD_MAX);
  }

  /**
   * 内存里每条曲线最多留多少点。
   * ⚠️ 这里**故意不看** card.points —— 那是"最多画多少个点"（显示层），
   * 一旦拿它当保留上限，"最近 1 小时"就只会剩下最后 120 个点，
   * 又回到用户抱怨的「曲线显示不全」。保留多少只由硬上限兜底。
   */
  function bufferCap() {
    return POINTS_HARD_MAX;
  }

  /** 这张卡片最多画多少个点：用户设了就用用户的；没设返回 0（交给像素兜底） */
  function drawCapOf(card) {
    return normPoints(card && card.points);
  }

  /**
   * 均匀抽稀：把 n 个点压到 maxN 个，**首尾一定保留**。
   * 关键区别：裁剪是「只要最后一段」，抽稀是「整段都还在，只是稀了」——
   * 用户抱怨的「曲线显示不全」正是被裁剪裁出来的。
   */
  function decimate(data, ts, maxN) {
    const n = data.length;
    if (!(maxN > 0) || n <= maxN || maxN < 2) return { data, ts, dropped: 0 };
    const outData = new Array(maxN);
    const outTs = ts ? new Array(maxN) : null;
    const step = (n - 1) / (maxN - 1);
    for (let i = 0; i < maxN; i += 1) {
      const j = i === maxN - 1 ? n - 1 : Math.round(i * step);
      outData[i] = data[j];
      if (outTs) outTs[i] = ts[j];
    }
    return { data: outData, ts: outTs, dropped: n - maxN };
  }

  /** 按内存硬上限裁剪一条曲线（超出部分从最旧的开始丢），data / ts 必须同步裁 */
  function trimSeries(card) {
    const cap = bufferCap();
    for (const s of card.series || []) {
      if (s.data.length > cap) {
        const drop = s.data.length - cap;
        s.data.splice(0, drop);
        if (s.ts) s.ts.splice(0, Math.max(0, s.ts.length - cap));
      }
    }
  }

  /** 曲线里最早一个数据的时间（用于判断"要不要往更早补"） */
  function earliestTs(card) {
    let t = Infinity;
    for (const s of card.series || []) {
      if (!s.ts || s.ts.length === 0) continue;
      const first = s.ts[0];
      if (first < t) t = first;
    }
    return Number.isFinite(t) ? t : 0;
  }

  /**
   * 把一张卡片上所有曲线的 data / ts 按时间排好序，并去掉完全重复的点。
   *
   * 为什么需要这一步（这是「曲线显示不全」背后真正的脏数据源）：
   *   页面刚打开时，WS 会先回放"最近一条报文"（服务端内存里还留着），
   *   于是一条**最新**的点先落到数组里；紧接着 `boot` 才把历史库的记录回填进去。
   *   历史是 append 上去的，数组就变成
   *       [最新点, 更早的历史..., 更早的历史...]
   *   —— ts 不再递增。后果是：
   *     · 「窗口裁剪」的二分/游标假设 ts 递增，窗口形同失效；
   *     · X 轴左端刻度直接取 ts[0]，会显示成最新时间；
   *     · 「缓冲覆盖多久」算成 0 秒（卡片小字变成只有一个点数）。
   *   逐条 append 无法避免这个顺序问题（历史里也可能混着刚回放的那一条），
   *   所以在数据落地之后统一做一次归并排序最省心。
   *
   * @returns {boolean} 是否真的修过（有乱序或重复）
   */
  function normalizeSeriesTs(card) {
    let changed = false;
    for (const s of card.series || []) {
      if (!s.ts || s.ts.length !== s.data.length || s.ts.length < 2) continue;
      let sorted = true;
      let dup = false;
      for (let i = 1; i < s.ts.length; i += 1) {
        if (s.ts[i] < s.ts[i - 1]) { sorted = false; break; }
        if (s.ts[i] === s.ts[i - 1] && s.data[i] === s.data[i - 1]) dup = true;
      }
      if (sorted && !dup) continue;
      // 下标排序（V8 的 sort 是稳定的，同一时刻保留先到的那个 —— 实时点优先）
      const idx = s.ts.map((_, i) => i).sort((a, b) => s.ts[a] - s.ts[b]);
      const data = [];
      const ts = [];
      for (const i of idx) {
        if (ts.length && ts[ts.length - 1] === s.ts[i] && data[data.length - 1] === s.data[i]) continue;
        ts.push(s.ts[i]);
        data.push(s.data[i]);
      }
      s.data = data;
      s.ts = ts;
      changed = true;
    }
    if (changed) card._tsFixed = (card._tsFixed || 0) + 1;
    return changed;
  }

  /** 用一条记录更新曲线的"当前值"、卡片大字与告警，但**不往曲线上加点**（点已由归并处理好） */
  function applyLatest(card, rec) {
    card.raw = rec.payload;
    card.rawJson = rec.json;     // 弹窗里「可勾选的字段」就来自它
    card.lastTs = rec.ts;
    for (const s of pickSamples(card, rec)) {
      const ser = card.seriesMap.get(s.key);
      if (!ser) continue;
      ser.num = s.num;
      ser.text = s.text;
      ser.bool = !!s.bool;
    }
    card.main = pickMainSeries(card);
    const mv1 = mainValue(card);
    card.text = mv1.text;
    card.num = mv1.num;
    card.level = computeLevel(card, card.num);
    S.dirtyCanvas.add(card);
  }

  /**
   * 把一条曲线（按 ts 升序的）历史数据并进卡片，**按时间归并**而不是直接 append。
   * 用于页面打开/刷新时的历史回填：那时缓冲里可能已经躺着一条更新的实时点。
   *
   * @param {object} card
   * @param {Array} recs  按 ts 升序的记录（服务端保证）
   * @returns {number} 合并进去的点数
   */
  function mergeHistory(card, recs) {
    const bags = new Map();
    for (const rec of recs) {
      for (const s of pickSamples(card, rec)) {
        if (s.num === null) continue;
        let b = bags.get(s.key);
        if (!b) { b = { label: s.label, bool: !!s.bool, data: [], ts: [], text: s.text }; bags.set(s.key, b); }
        b.data.push(s.num);
        b.ts.push(rec.ts);
        b.text = s.text;      // recs 按时间升序 → 循环结束时它就是"最新那个点的展示值"
      }
    }

    let added = 0;
    for (const [k, b] of bags) {
      // 自动模式到上限就不再建新曲线。这里逐个建、逐个查，series.length 是对的；
      // 若把上限判断塞进上面那个收集循环，那时一条都还没建，上限就废了。
      if (!seriesAllowed(card, k)) continue;
      let ser = card.seriesMap.get(k);
      if (!ser) {
        ser = { key: k, label: b.label, num: null, text: '—', data: [], ts: [], color: '', bool: b.bool };
        ser.color = nextColor(card);
        card.seriesMap.set(k, ser);
        card.series.push(ser);
      }
      // 归并两段升序序列；同一时刻保留"本来就在缓冲里"的那个点（实时点优先）
      const od = ser.data;
      const ot = ser.ts || [];
      const data = [];
      const ts = [];
      let i = 0;
      let j = 0;
      while (i < ot.length && j < b.ts.length) {
        if (ot[i] < b.ts[j]) { ts.push(ot[i]); data.push(od[i]); i += 1; }
        else if (ot[i] > b.ts[j]) { ts.push(b.ts[j]); data.push(b.data[j]); j += 1; }
        else { ts.push(ot[i]); data.push(od[i]); i += 1; j += 1; }
      }
      while (i < ot.length) { ts.push(ot[i]); data.push(od[i]); i += 1; }
      while (j < b.ts.length) { ts.push(b.ts[j]); data.push(b.data[j]); j += 1; }
      ser.data = data;
      ser.ts = ts;
      // ★ 只被回填补过、还没有实时报文打到这条曲线的（典型：刚把这个主题的曲线切到另一个字段），
      //   `ser.num` 仍是 null —— 卡片大字和图例就会一直显示「—」，看着像"这条曲线没有数据"，
      //   其实下面已经画了一串点。用最新那个点的展示值把当前值补上。
      //   （实时报文一旦到达，pushSample 会覆盖，不影响实时值优先）
      if (ser.num === null && data.length) {
        ser.num = data[data.length - 1];
        ser.text = b.text;
      }
      if (b.data.length > added) added = b.data.length;
    }
    trimSeries(card);
    normalizeSeriesTs(card);
    return added;
  }

  /**
   * 问服务端「这个主题库里有多少条 / 窗口内有多少点」。
   * 结果按「主题|窗口」缓存 —— 弹窗里下拉一改就刷提示，不能每次都打接口；
   * 失败不缓存（下次还能重试）。
   * @returns {Promise<object|null>}
   */
  const spanCache = new Map();

  function askSpan(card, win) {
    const key = `${card.topic}|${Math.round(Number(win) || 0)}`;
    if (spanCache.has(key)) return spanCache.get(key);
    const p = api(`/api/history/span?topic=${encodeURIComponent(card.topic)}&windowMin=${Math.round(Number(win) || 0)}`)
      .catch((err) => {
        spanCache.delete(key);
        console.warn('查历史库跨度失败', card.topic, err.message);
        return null;
      });
    spanCache.set(key, p);
    return p;
  }

  /** 某条时间线在 [fromTs, ∞) 里有多少个点（用 ts 从后往前数，比 filter 快得多） */
  function pointsInWindow(card, fromTs) {
    let best = 0;
    for (const s of card.series || []) {
      if (!s.ts || s.ts.length !== s.data.length) continue;
      let n = 0;
      for (let i = s.ts.length - 1; i >= 0 && s.ts[i] >= fromTs; i -= 1) n += 1;
      if (n > best) best = n;
    }
    return best;
  }

  /**
   * 往更早的方向补历史：把落盘库里比当前缓冲更早的数据取回来，接到每条曲线前面。
   * 这是「曲线显示不全」的正解 —— 内存缓冲只有那么深，但库里存了保留天数内的全部，
   * 用户要看最近 1 小时，就该把那一小时从库里捞回来，而不是只显示缓冲里那十几分钟。
   *
   * @param {object} card
   * @param {number} fromTs 想要的最早时间（一般是窗口起点）
   * @param {object} [opts] replace=true：这是"提高分辨率"的一次补数据
   *                        （缓冲够长但点太稀，比如 10 小时压成 5000 点后，
   *                         再看"最近 1 小时"只有 500 个点），
   *                        补回来的更细的点会按时间并进去，比原来密
   * @returns {Promise<number>} 补进来的条数
   */
  async function backfillCard(card, fromTs, opts) {
    if (!card || !card.topic) return 0;
    if (!S.backfilled) S.backfilled = new Set();
    const replace = !!(opts && opts.replace);

    const win = effectiveWindowMin(card);
    const first = earliestTs(card);
    // 缓冲已经覆盖到窗口起点了，不用往前接。
    // 分组卡例外：多个主题的起点各不相同，"最早的一条"覆盖了不代表每个主题都覆盖了。
    if (!replace && !card.group && first > 0 && Number.isFinite(fromTs) && first <= fromTs + 1000) return 0;

    // 分组卡要为每个成员主题各拉一次（服务端的历史接口按主题取）
    const topics = card.group ? await groupTopicsFor(card) : [card.topic];

    // 去重 key **按主题**，不是按卡片。
    // 按卡片的话会出现这个坑：主题被撤下卡片又重新加回来时，"卡片|窗口|起点"一点没变，
    // 于是这次回填被当成重复请求直接跳过 —— 用户看到的就是"加回来了，可是曲线是空的，
    // 只有实时值在跳"。key 落到主题上，新进卡片的主题照样立刻补历史，
    // 已经补过的主题也不会被重复打接口。
    const base = `${card.topic}|${win}|${Math.floor(fromTs || 0)}|${replace ? 'r' : 'p'}`;
    const pending = topics.filter((t) => !S.backfilled.has(`${base}|${t}`));
    if (!pending.length) return 0;
    for (const t of pending) S.backfilled.add(`${base}|${t}`);

    const q = win > 0 ? `&windowMin=${win}` : '';
    let added = 0;
    let newest = null;
    for (const t of pending) {
      let recs;
      try {
        recs = await api(`/api/history?topic=${encodeURIComponent(t)}&limit=${FETCH_MAX}&sample=1${q}`);
      } catch (err) {
        console.warn('补历史失败', t, err.message);
        continue;
      }
      if (!Array.isArray(recs) || recs.length === 0) continue;
      if (recs.length >= FETCH_MAX) card.dbTruncated = true;   // 顶到上限：画面上是抽样结果
      // 归并（不是 append，也不是单向前插）：库里既有更早的、也可能有更细的，
      // 一次按时间并进去就行，同一时刻的点自动去重。
      const n = mergeHistory(card, recs);
      if (n > added) added = n;
      const last = recs[recs.length - 1];
      if (last && (!newest || last.ts > newest.ts)) newest = last;
    }
    if (!added) return 0;

    // 大字取"手上最新的那条"：库里那条要是比缓冲里的还新，就更新过来
    if (newest && newest.ts > (card.lastTs || 0)) applyLatest(card, newest);
    else if (!card.main) applyLatest(card, newest);

    // 兜底：主曲线已经有值了，但卡片还挂着「—」（只有上面那句 applyLatest 不够 ——
    // 库里那条和手上最新那条往往是同一条，`newest.ts > card.lastTs` 不成立，
    // 而 card.main 早在建曲线时就非空了）。不补的话大字会一直空着。
    const mainNow = pickMainSeries(card);
    if (mainNow && mainNow.num !== null && card.num === null) {
      card.main = mainNow;
      card.text = mainNow.text;
      card.num = mainNow.num;
      card.level = computeLevel(card, card.num);
    }

    updateCardDom(card);
    markDirty(card);
    return added;
  }

  /**
   * 检查这张卡片手上的数据够不够撑起当前窗口，不够就从落盘库补。
   * 两种情况都要补，性质不同：
   *   1) 缓冲**短于**窗口 —— 曲线铺不满（用户看到的就是"显示不全"）；
   *   2) 缓冲够长但**点太稀** —— 铺是铺满了，但分辨率被前面的抽样拉低了
   *      （10 小时压成 5000 点后，再看最近 1 小时只剩 500 个点）。
   */
  async function ensureCoverage(card, opts) {
    // 勾选带出来的"新曲线"（之前没画过的测点/字段）要先补历史。
    // 现场设备 60~80 秒才上报一次，不补的话用户勾完对着空曲线干等一分钟，
    // 会以为"勾了也没用"。backfillCard 内部按 topic|窗口|起点 去重，不会反复打接口。
    // 注意 `card.series.length === 0` 这一支：空数组上 `.some()` 恒为 false，
    // 少了它的话"刚换了主题/刚取消分组"的卡片会一条曲线都没有却不去问库，
    // 用户对着空卡片干等设备下一条报文（现场 60~80 秒一条，看着就像"换了设置数据就没了"）。
    if (opts && opts.fillEmpty
      && (card.series.length === 0 || card.series.some((s) => s.data.length === 0))) {
      await backfillCard(card, 0).catch(() => 0);
    }
    const win = effectiveWindowMin(card);
    if (win <= 0) return 0;                    // 「全部」没有窗口起点可对齐，启动回填时已经拉满
    const cov = seriesCoverage(card);
    if (!cov || cov.n < 1) return 0;           // 一条数据都没有，等实时数据来了再说

    const anchor = cardAnchorTs(card);
    const fromTs = anchor - win * 60000;
    const needMs = win * 60000;
    if (cov.span < needMs * 0.95) return backfillCard(card, fromTs);   // 情况 1

    // 情况 2：问一下库里这个窗口有多少条，比手上多得多就按窗口重新拉一遍
    const inBuf = pointsInWindow(card, fromTs);
    const sp = await askSpan(card, win).catch(() => null);
    const dbIn = sp && Number.isFinite(sp.dbInWindow) ? sp.dbInWindow : null;
    if (dbIn !== null && inBuf > 0 && dbIn > inBuf * 2.5 && dbIn > 200) {
      return backfillCard(card, fromTs, { replace: true });
    }
    return 0;
  }

  /** 卡片小字用的紧凑跨度：40 秒 / 12 分 / 9.9 小时 / 3.2 天 */
  function fmtSpanShort(ms) {
    const sec = Math.round(ms / 1000);
    if (sec < 60) return `${sec} 秒`;
    const min = sec / 60;
    if (min < 60) return `${Math.round(min)} 分`;
    const hr = min / 60;
    if (hr < 24) return `${Math.round(hr * 10) / 10} 小时`;
    return `${Math.round((hr / 24) * 10) / 10} 天`;
  }

  /** 上报节拍这类"几十秒到几分钟"的量：两分钟以内保留到秒，别都糊成"1 分" */
  function fmtGap(ms) {
    if (ms < 120000) return `${Math.round(ms / 1000)} 秒`;
    return fmtSpanShort(ms);
  }

  /** 卡片右下角那行小字：这次到底画了多少点、铺开多久、库里一共多少条 */
  function chartNote(card) {
    const c = card._chart;
    if (!c || !c.points) return '';
    const bits = [];
    bits.push(c.rawPoints > c.points ? `${c.points}/${fmtCount(c.rawPoints)} 点（抽稀）` : `${c.points} 点`);
    if ((card.series || []).length > 1) bits.push(`${card.series.length} 条曲线`);
    if (c.spanMs > 0) bits.push(fmtSpanShort(c.spanMs));
    if (card.dbCount && card.dbCount > c.rawPoints) bits.push(`库内 ${fmtCount(card.dbCount)} 条`);
    else if (card.dbTruncated) bits.push('库内更多（已抽样）');
    return bits.join(' · ');
  }

  function fmtCount(n) {
    if (!Number.isFinite(n)) return '—';
    if (n < 10000) return String(n);
    if (n < 1000000) return `${Math.round(n / 1000)}k`;
    return `${Math.round(n / 100000) / 10}M`;
  }


  /** 把 S.ui.chartWindowMin 回填到顶栏下拉；值不在预设列表里就退回 0（全部） */
  function syncWindowSelect() {
    const sel = $('fWindow');
    const want = String(Math.max(0, Number(S.ui.chartWindowMin) || 0));
    const has = [...sel.options].some((o) => o.value === want);
    if (!has) S.ui.chartWindowMin = 0;
    sel.value = has ? want : '0';
  }

  function toast(msg, kind) {
    const el = document.createElement('div');
    el.className = 'toast ' + (kind || '');
    el.textContent = msg;
    $('toastWrap').appendChild(el);
    setTimeout(() => {
      el.style.transition = 'opacity .25s';
      el.style.opacity = '0';
      setTimeout(() => el.remove(), 260);
    }, kind === 'err' ? 4200 : 2600);
  }

  // ---------------- API ----------------
  async function api(path, body) {
    const opt = body
      ? { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }
      : {};
    const res = await fetch(path, opt);
    const json = await res.json();
    if (!json.ok) throw new Error(json.error || '请求失败');
    return json.data;
  }

  // ============================================================
  //  WebSocket
  // ============================================================
  function connectWs() {
    const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const url = `${proto}//${location.host}/ws`;
    let ws;
    try {
      ws = new WebSocket(url);
    } catch (err) {
      scheduleReconnect();
      return;
    }
    S.ws = ws;

    ws.onopen = () => {
      S.wsRetry = WS_RETRY_MIN;
      console.log('[ws] 已连接', url);
    };

    ws.onmessage = (ev) => {
      let frame;
      try { frame = JSON.parse(ev.data); } catch { return; }
      handleFrame(frame);
    };

    ws.onclose = () => {
      S.ws = null;
      scheduleReconnect();
    };

    ws.onerror = () => { /* onclose 会处理 */ };
  }

  function scheduleReconnect() {
    const delay = S.wsRetry;
    S.wsRetry = Math.min(Math.round(S.wsRetry * 1.6), WS_RETRY_MAX);
    setTimeout(connectWs, delay);
  }

  function handleFrame(frame) {
    switch (frame.type) {
      case 'hello':
        break;

      case 'messages':
        ingestMessages(frame.items || [], { replay: frame.replay === true });
        break;

      case 'status':
        applyStatus(frame);
        break;

      case 'stats':
        applyStats(frame);
        break;

      case 'topics':
        applyTopicList(frame.topics || []);
        break;

      case 'broker':
        applyBroker(frame);
        break;

      case 'reset':
        S.logs = [];
        S.topics.clear();
        for (const c of S.cards) { resetSeries(c); c.count = 0; }
        renderLogs(true);
        renderTopics();
        renderAllCards();
        toast('已清空历史数据');
        break;

      default:
        break;
    }
  }

  // ============================================================
  //  数据摄入
  // ============================================================
  function ingestMessages(items, opts) {
    if (!items.length) return;
    const replay = !!(opts && opts.replay);

    // 卡片正在按配置重建（boot 里那一小段同步+异步的间隙），此刻进来的报文先存着，
    // 等卡片建好再灌进去 —— 否则 WS 连接时回放的"最近一条报文"会被重建过程丢掉，
    // 刷新页面后得干等设备下一次上报才看得到实时值。
    if (S.restoringCards) {
      S.pendingMsgs.push(...items);
      if (S.pendingMsgs.length > 2000) S.pendingMsgs.splice(0, S.pendingMsgs.length - 2000);
      return;
    }

    for (const rec of items) {
      // 主题统计
      const t = S.topics.get(rec.topic) || { topic: rec.topic, count: 0, lastTs: 0 };
      t.count += 1;
      t.lastTs = rec.ts;
      t.lastPayload = rec.payload;
      t.lastNum = rec.num;
      S.topics.set(rec.topic, t);

      // 日志（回放的历史不写流水，避免刚打开页面就刷屏）
      if (!S.logPaused && !replay) {
        S.logs.push(rec);
        if (S.logs.length > LOG_KEEP_MAX) S.logs.splice(0, S.logs.length - LOG_KEEP_MAX);
      }

      // 卡片数据：先看有没有这个主题自己的卡，再看有没有管着它的分组卡
      // （分组卡优先于"自动上板" —— 设备已经有一张 PLC 总卡了，就别每条点位再冒一张）
      let card = S.cardMap.get(rec.topic) || groupCardFor(rec.topic);
      // 被某张分组卡删掉的主题：既不进那张卡，也不自动给它建单卡 ——
      // 否则用户刚点完「删除」，旁边的卡片又冒出来了。想单独看就在侧栏点 + 上板。
      if (!card && S.autoAdd && !removedFromAnyGroup(rec.topic) && S.cards.length < AUTO_CARD_LIMIT) {
        // 若这个主题在测点定义里（内置模拟器），直接带上名称/单位/上下限
        const def = catalogMap.get(rec.topic);
        card = createCard(
          def ? { ...def, topic: rec.topic } : { topic: rec.topic, name: shortName(rec.topic), unit: '' },
        );
      }
      if (card) {
        card.lastTs = rec.ts;
        card.count = (card.count || 0) + 1;
        pushSample(card, rec);
      }
    }

    if (!S.paused) scheduleRender();
  }

  /**
   * 把一条消息写进卡片的各条曲线。
   * 一条消息可能带多个可绘制的值（JSON 里的多个数值/布尔字段），所以这里是复数。
   */
  function pushSample(card, rec) {
    card.raw = rec.payload;
    card.rawJson = rec.json;     // 弹窗里「可勾选的字段」就来自它
    card.lastTs = rec.ts;

    const samples = pickSamples(card, rec);
    const pts = bufferCap();
    card.overflow = computeOverflow(card);

    for (const s of samples) {
      // 自动模式到上限了就不再建新曲线（数据一并丢掉，避免内存里堆着不画的东西）
      if (!seriesAllowed(card, s.key)) continue;
      let ser = card.seriesMap.get(s.key);
      if (!ser) {
        ser = { key: s.key, label: s.label, num: null, text: '—', data: [], ts: [], color: '', bool: !!s.bool };
        ser.color = nextColor(card);
        card.seriesMap.set(s.key, ser);
        card.series.push(ser);
      }
      ser.num = s.num;
      ser.text = s.text;
      ser.bool = !!s.bool;   // 预览时预建的曲线 bool 还是 false，这里补齐（决定画不画台阶线）
      if (s.num !== null) {
        const prevTs = ser.ts.length ? ser.ts[ser.ts.length - 1] : 0;
        ser.data.push(s.num);
        ser.ts.push(rec.ts);      // 与 data 一一对应，X 轴要拿它标时间
        // 设备时钟回拨、或迟到的历史记录都会让 ts 倒退 —— 一旦不递增，
        // 窗口裁剪和 X 轴刻度就全错，所以发现就立刻归并回正序
        if (rec.ts < prevTs) normalizeSeriesTs(card);
        if (ser.data.length > pts) {
          ser.data.splice(0, ser.data.length - pts);
          ser.ts.splice(0, ser.ts.length - pts);
        }
      }
    }

    // 主曲线决定卡片上的大字和告警判定
    const main = pickMainSeries(card);
    card.main = main;
    const mv2 = mainValue(card);
    card.text = mv2.text;
    card.num = mv2.num;
    card.level = computeLevel(card, card.num);

    // 数值变了，曲线画布必须重绘（只靠上面的 scheduleRender 不会重画 canvas）
    S.dirtyCanvas.add(card);
  }

  /** 卡片的主曲线：勾了数据项就用第一个勾上的，否则用第一个已经有数据的 */
  function pickMainSeries(card) {
    if (card.series.length === 0) return null;
    const wanted = [
      ...(card.items || []).map((n) => card.seriesMap.get(ITEM_PREFIX + n)),
      ...(card.fields || []).map((f) => card.seriesMap.get(f)),
    ].find((s) => s && s.num !== null);
    if (wanted) return wanted;
    return card.series.find((s) => s.num !== null) || card.series[0];
  }

  /**
   * 卡片大字该显示哪个值。
   * ⚠️ 掉线点位的曲线不能当"当前值"：分组卡没勾选时 `pickMainSeries` 兜底取 `series[0]`，
   *    而它往往正是第一个上报的点位 —— 掉线后大字会把 10 分钟前的旧值一直摆着，
   *    整张卡看上去照常在收数，就是现场最容易误判的那种。
   *    主曲线掉线就改挑一条还在发的；全掉线了才老实显示「—」。
   */
  function mainValue(card) {
    const main = card.main;
    if (!main) return { text: '—', num: null };
    const dead = (s) => {
      const t = topicKeyOf(s && s.key);
      return !!t && topicStale(card, t);
    };
    if (!dead(main)) return { text: main.text, num: main.num };
    const live = (card.series || []).find((s) => s !== main && s.num !== null && !dead(s));
    return live ? { text: live.text, num: live.num } : { text: '—', num: null };
  }

  /** 主题末段：plc/DB1.DBW290 → DB1.DBW290（分组卡片的曲线标签用它） */
  function tailName(topic) {
    const p = String(topic || '').split('/').filter(Boolean);
    return p.length ? p[p.length - 1] : String(topic || '');
  }

  // ============================================================
  //  分组卡片（一台设备一张卡）：主题前缀 → 每个主题一条曲线
  // ============================================================
  /** 这张分组卡已经收录的主题 */
  function groupMembers(card) {
    const set = new Set();
    if (card.seenByTopic) for (const t of card.seenByTopic.keys()) set.add(t);
    if (Array.isArray(card.members)) for (const t of card.members) set.add(t);
    for (const t of Object.keys(card.picks || {})) set.add(t);
    // 勾了要显示的主题也算成员：还没收到报文时它也得留在清单里（勾选界面得列出来）
    for (const t of card.picked || []) set.add(t);
    return [...set].filter((t) => inGroupOf(card.topic, t) && !isRemoved(card, t)).sort();
  }

  /**
   * 这个主题被这张分组卡**显式删掉**了吗。
   * 删掉 = 彻底不管它：不进成员清单、不建曲线、不把它的大字刷上卡片，
   * 也不会在下次自动合并时又被塞回这张卡。想找回来就在弹窗里点「恢复」。
   */
  function isRemoved(card, topic) {
    return !!card && (card.removed || []).indexOf(topic) >= 0;
  }

  /** 哪些分组卡把这个主题删掉了（用来挡住"自动上板"再给它建一张单卡） */
  function removedFromAnyGroup(topic) {
    for (const c of S.cards) if (c.group && isRemoved(c, topic)) return c;
    return null;
  }

  function countTopicSeries(card) {
    let n = 0;
    for (const s of card.series || []) if (isTopicKey(s.key)) n += 1;
    return n;
  }

  /**
   * 这条主题现在画不画曲线。
   * **只决定曲线，不决定它上不上卡片** —— 没勾的主题照样在图例里显示实时值，
   * 只是不占曲线名额。要让一个主题整个离开卡片，用「删除」。
   */
  function hasCurve(card, topic) {
    const on = card.picked || [];
    if (!on.length) return true;               // 一个都没勾 = 自动：成员都画（超上限由 seriesAllowed 挡）
    return on.indexOf(topic) >= 0;
  }

  /**
   * 这张分组卡**该画哪几个主题的曲线**（勾过就只画勾的，没勾过 = 全部成员）。
   * 返回顺序跟着成员清单（排序过的）走，而不是用户点击的顺序 ——
   * 否则勾选的先后会改变曲线颜色和顺序，看起来像在乱跳。
   */
  function drawnTopics(card) {
    return groupMembers(card).filter((t) => hasCurve(card, t));
  }

  /**
   * 前缀上"认领"这条主题的分组卡 —— **包含已经把它删掉的卡**。
   * 用途只有一个：侧栏里判断"点 + 应该加回哪张卡"，而不是又冒出一张单主题卡。
   */
  function groupCardForPrefix(topic) {
    let best = null;
    for (const c of S.cards) {
      if (!c.group) continue;
      if (!inGroupOf(c.topic, topic)) continue;
      if (!best || groupRoot(c.topic).length > groupRoot(best.topic).length) best = c;
    }
    return best;
  }

  /** 这条主题归哪张分组卡管（最长前缀优先，避免 plc 抢走 plc2/…） */
  function groupCardFor(topic) {
    let best = null;
    for (const c of S.cards) {
      if (!c.group) continue;
      if (!inGroupOf(c.topic, topic)) continue;
      // 被这张卡删掉的主题不归它管 —— 否则删了等于没删，下一条报文又被拉回来
      if (isRemoved(c, topic)) continue;
      if (!best || groupRoot(c.topic).length > groupRoot(best.topic).length) best = c;
    }
    return best;
  }

  /** 这个主题「自动」时会画哪个字段 —— 由 sampleForTopic 事后写回，保证两处判断永远一致 */
  function desiredFieldKey(card, topic) {
    const pick = (card.picks || {})[topic];
    if (pick) return pick;
    const e = card.seenByTopic && card.seenByTopic.get(topic);
    return (e && e.autoKey) || '';
  }

  /** 记住这个主题里出现过哪些测点 / 数值字段（弹窗里那排下拉的选项来源） */
  function noteSeenTopic(card, topic, json, rec) {
    if (!card.seenByTopic) card.seenByTopic = new Map();
    let e = card.seenByTopic.get(topic);
    if (!e) {
      e = {
        items: new Map(), fields: new Map(), idField: '', autoKey: '', autoLabel: '',
        count: 0, lastTs: 0,
        // 这个主题的最新实时值（图例里"有值没曲线"那几行用它）
        lastNum: null, lastText: '—',
        // 这个主题**自己**的上报节拍（相邻两条的间隔，只留最近若干个）。
        // 分组卡里各点位的节拍可能差很远（有的 5 秒、有的 70 秒），
        // 拿卡片级的节拍去判"某个点位掉线"，会把慢的那个一直误报成离线。
        gaps: [],
      };
      card.seenByTopic.set(topic, e);
    }
    // 记节拍要趁 lastTs 还是上一条的时候（ts 可能回拨，非正增长就不记）
    if (e.lastTs && rec.ts > e.lastTs) {
      e.gaps = [...(e.gaps || []), rec.ts - e.lastTs].slice(-STALE_SAMPLE);
    }
    e.count += 1;
    e.lastTs = rec.ts;

    const id = detectIdentity(json, card.groupBy);
    e.idField = id ? id.field : '';
    if (id) {
      const smp = itemSample(json, id);
      const prev = e.items.get(id.name);
      e.items.set(id.name, {
        name: id.name,
        num: smp.num !== null ? smp.num : (prev ? prev.num : null),
        text: smp.text,
        bool: !!smp.bool,
        count: (prev ? prev.count : 0) + 1,
      });
    }
    if (json) {
      for (const f of extractNumericFields(json)) {
        if (f.path === e.idField) continue;
        e.fields.set(f.path, { path: f.path, num: f.num, text: f.text, bool: f.bool });
      }
    }
    return e;
  }

  /**
   * 分组卡片的一条报文 → 最多**一条**曲线样本。
   *
   * 用户要的是「一个主题一条曲线」：所以这里不做字段模式的多字段展开，
   * 一个主题只出一路值。想看这个主题的别的字段，就在弹窗里把这条曲线切过去（card.picks）。
   */
  function sampleForTopic(card, rec, topic, entry) {
    const json = (rec.json && typeof rec.json === 'object' && !Array.isArray(rec.json)) ? rec.json : null;
    const pick = (card.picks || {})[topic] || '';
    const mk = (fieldKey, label, num, text, bool) => ({
      key: topicKey(topic, fieldKey),
      label: label || tailName(topic),
      num,
      text,
      bool: !!bool,
    });
    const setAuto = (k, l) => { if (entry) { entry.autoKey = k; entry.autoLabel = l; } };
    const rawText = () => {
      const txt = String(rec.payload == null ? '' : rec.payload).trim();
      return isNumericText(txt) ? txt : fmtNum(rec.num);
    };

    // ① 这条曲线被指定了测点/字段 → 只认它（指定的值这轮没出现就不出点，不当成别的）
    if (pick) {
      if (isItemKey(pick)) {
        const id = detectIdentity(json, card.groupBy);
        if (!id || ITEM_PREFIX + id.name !== pick) return null;
        const smp = itemSample(json, id);
        return mk(pick, id.name, smp.num, smp.text, smp.bool);
      }
      if (pick === '__v') {
        if (rec.num === null) return null;
        return mk('__v', tailName(topic), rec.num, rawText());
      }
      const v = json ? getPath(json, pick) : undefined;
      if (v === undefined || v === null || typeof v === 'object') return null;
      const num = toPlotNum(v);
      if (num === null) return null;
      const isBool = typeof v === 'boolean';
      return mk(pick, pick, num, isBool ? (num ? 'ON' : 'OFF') : (typeof v === 'string' ? v.trim() : fmtNum(num)), isBool);
    }

    // ② 自动：测点身份 → 整条数值 → 报文里第一个可绘制的数值字段
    const id = detectIdentity(json, card.groupBy);
    if (id) {
      const smp = itemSample(json, id);
      if (smp.num !== null || smp.text !== '—') {
        setAuto(ITEM_PREFIX + id.name, id.name);
        return mk(ITEM_PREFIX + id.name, id.name, smp.num, smp.text, smp.bool);
      }
    }
    if (rec.num !== null) {
      setAuto('__v', tailName(topic));
      return mk('__v', tailName(topic), rec.num, rawText());
    }
    if (json) {
      const f = extractNumericFields(json).find((x) => x.path !== (id ? id.field : ''));
      if (f) {
        setAuto(f.path, f.path);
        return mk(f.path, f.path, f.num, f.text, f.bool);
      }
    }
    return null;
  }

  /** 分组卡片的采样入口：不属于这一组、被删掉、或这轮没值 → 空数组（不建曲线） */
  function pickGroupSamples(card, rec) {
    if (!inGroupOf(card.topic, rec.topic)) return [];
    // 删掉的主题连"见过"都不记 —— 记了的话勾选列表里又会冒出来，删了等于没删
    if (isRemoved(card, rec.topic)) return [];
    const json = (rec.json && typeof rec.json === 'object' && !Array.isArray(rec.json)) ? rec.json : null;
    const entry = noteSeenTopic(card, rec.topic, json, rec);
    const s = sampleForTopic(card, rec, rec.topic, entry);
    if (!s) {
      if (entry) { entry.lastNum = null; entry.lastText = '—'; }
      return [];
    }
    // ★ 卡片上每个主题都要有实时值：没勾曲线的也把最新值记下来，
    //   只是**不把它作为曲线样本返回** —— 图形上不画线，图例里照样显示数值。
    if (entry) { entry.lastNum = s.num; entry.lastText = s.text; }
    return hasCurve(card, rec.topic) ? [s] : [];
  }

  /** 曲线标签：测点名 > 主题末段（`__v`）> 字段路径 */
  function labelForTopicField(topic, fieldKey) {
    if (isItemKey(fieldKey)) return itemNameOf(fieldKey);
    if (fieldKey === '__v' || fieldKey === 'value') return tailName(topic);
    return fieldKey;
  }

  /**
   * 分组卡片的曲线集合：每个该画的主题一条（drawnTopics —— 勾过就只留勾的）。
   * 和单主题那张卡一样，`fresh` 时会把「该画但还没有曲线」的先建出来（颜色顺序立刻定下来）。
   */
  function syncGroupSeries(card, fresh) {
    const want = new Set();
    for (const t of drawnTopics(card)) {
      const fk = desiredFieldKey(card, t);
      if (fk) want.add(topicKey(t, fk));
    }
    for (let i = card.series.length - 1; i >= 0; i -= 1) {
      const s = card.series[i];
      if (!want.has(s.key)) {
        card.series.splice(i, 1);
        card.seriesMap.delete(s.key);
      }
    }
    if (!fresh) return;
    for (const k of want) {
      if (card.seriesMap.has(k) || !seriesAllowed(card, k)) continue;
      const ser = {
        key: k, label: labelForTopicField(topicKeyOf(k), fieldKeyOf(k)),
        num: null, text: '—', data: [], ts: [], color: '', bool: false,
      };
      ser.color = nextColor(card);
      card.seriesMap.set(k, ser);
      card.series.push(ser);
    }
  }

  /** 分组卡的成员主题清单：见过的 + 平台内存里的 + 落盘库里查到的
   *  （刷新页面后内存里可能一条报文都还没有，光靠"见过的"会漏掉整组） */
  async function groupTopicsFor(card) {
    const set = new Set(groupMembers(card));
    for (const t of S.topics.keys()) if (inGroupOf(card.topic, t)) set.add(t);
    if (!card._membersAt || Date.now() - card._membersAt > 60000) {
      card._membersAt = Date.now();
      try {
        const list = await api('/api/history/topics?limit=500');
        for (const r of (Array.isArray(list) ? list : [])) {
          if (r && inGroupOf(card.topic, r.topic)) set.add(r.topic);
        }
      } catch (err) { /* 库不可用就只用已知的 */ }
    }
    // 排序：成员清单要稳定。不排的话顺序取决于"谁先被查到"（内存 / 库里返回的顺序），
    // 刷新一次可能换一批（一卡最多 6 条曲线，超出的报「+N」）——
    // 用户会看到刷新前后画的是不同的几个点位，颜色也跟着换。
    card.members = [...set].filter((t) => inGroupOf(card.topic, t)).sort();
    return card.members;
  }

  /**
   * 卡片副标题：单主题卡就是主题名；分组卡写清楚「前缀/* · 几个点位」。
   * 勾选只影响曲线数，所以要分别写明白：卡片上几个点位、其中几条画了曲线。
   */
  function topicLabel(card) {
    if (!card.group) return card.topic;
    const all = groupMembers(card).length;
    if (!all) return `${groupRoot(card.topic)}/*　等待数据`;
    const n = countTopicSeries(card);
    return `${groupRoot(card.topic)}/*　${all} 个点位`
      + (n && n < all ? `　曲线 ${n} 条` : '');
  }

  /**
   * 同前缀自动合并：把「父路径相同的多张单主题卡」并成一张分组卡（一台设备一张卡）。
   *
   * 为什么需要这一步：卡片是**收到报文才上板**的 —— `plc/DB1.DBW290` 先上报、先占了一张卡，
   * 等 `plc/DB1.DBW292` 也上报时，那张卡已经在看板上了。分组卡只在"新建卡片"这一刻生效，
   * 存量卡片不会自己并起来，用户看到的就是"同一台设备的两个点位各占一张卡、时间轴还对不齐"。
   *
   * 只在启动、打开开关、以及新建卡片之后跑。用户手动把某张卡改回单主题后，
   * 只要它父路径下没有第二张卡，这里就不会再动它 —— 不会来回折腾。
   */
  async function reconcileAutoGroups() {
    if (!S.autoGroup) return 0;
    const byParent = new Map();
    for (const c of S.cards) {
      if (c.group) continue;
      const p = parentTopic(c.topic);
      if (!p) continue;                       // 裸主题判断不出属于哪台设备，不合并
      if (!byParent.has(p)) byParent.set(p, []);
      byParent.get(p).push(c);
    }

    let mergedTopics = 0;
    for (const [p, list] of byParent) {
      // 这个前缀下已经有分组卡了？→ 把手上的单主题卡收编进去；否则要凑够两张才值得并
      const g = groupCardFor(`${p}/__probe__`);
      // 被这张卡删过的主题不收回：用户删它就是为了让它独立，收回来等于跟用户对着干
      const cand = list.filter((c) => !g || !isRemoved(g, c.topic));
      const victims = g ? cand : (cand.length >= 2 ? cand.slice(1) : []);
      if (victims.length === 0) continue;
      const host = g || cand[0];
      // 极端情况：看板上真有一张主题正好叫 `plc` 的卡（不是前缀，是字面主题名）。
      // 那这张前缀卡会让位，合并就跳过这一组，免得两张卡抢同一个 cardMap 槽位。
      if (!g && S.cardMap.has(p) && S.cardMap.get(p) !== host) continue;

      if (!g) {
        S.cardMap.delete(host.topic);
        // 名字还是旧主题名就跟着换成前缀；用户自己改过名字（“1号炉”）就留着
        if (host.name === host.topic || host.name === tailName(host.topic)) host.name = p;
        host.topic = p;
        host.group = true;
        S.cardMap.set(p, host);
        if (host.el) host.el.root.dataset.topic = p;
        // 换了范围：按旧主题攒的曲线和"见过哪些测点"全作废，重新按前缀收集
        host.series = [];
        host.seriesMap = new Map();
        host.seenItems = new Map();
        host.seenByTopic = new Map();
        host.seenIdField = '';
        host.picks = {};
        host.picked = [];
        host.removed = [];
        host.members = [];
        host._membersAt = 0;
        host.main = null;
        host.rawJson = null;
        host.text = '—';
        host.num = null;
      }
      // 已有的分组卡吸收新成员时要**留住用户设过的 picks**，
      // 只把新主题纳进来（曲线由 syncGroupSeries 补建）
      for (const v of victims) if (v !== host) removeCard(v);

      mergedTopics += victims.length + (g ? 0 : 1);
      await groupTopicsFor(host).catch(() => []);
      syncGroupSeries(host, true);
      host.main = pickMainSeries(host);
      updateCardDom(host);
      markDirty(host);
      await backfillCard(host, 0).catch(() => 0);
      updateCardDom(host);
      markDirty(host);
    }

    if (mergedTopics) {
      const n = S.cards.reduce((a, c) => a + (c.group ? countTopicSeries(c) : 0), 0);
      renderTopics();
      saveCards();
      updateTopStats();
      toast(`同前缀已合并：${mergedTopics} 个主题归到一张卡` +
        `（左侧「同前缀自动合并」关掉即可保持分开）`, 'ok');
    }
    return mergedTopics;
  }

  /**
   * 按数据类型决定这条消息该显示/该画什么 —— 这是"自动出图"的核心。
   * 返回 [{key, label, num, text, bool}]；num 为 null 表示画不了曲线（纯文本）。
   *
   * 两条路线，按报文形态自动选：
   *   测点模式 —— 报文有 tag/address 这类身份字段：一个身份值一条曲线（见 ITEM_PREFIX 注释）
   *   字段模式 —— 没有身份字段：报文里每个数值字段一条曲线
   * 用户在卡片设置里勾过就按勾的来，一个都不勾 = 自动。
   */
  function pickSamples(card, rec) {
    // 分组卡片（一台设备一张卡）：一个主题一条曲线，走另一套
    if (card.group) return pickGroupSamples(card, rec);

    card._overflowFields = 0;

    const json = rec.json !== null && rec.json !== undefined
      && typeof rec.json === 'object' && !Array.isArray(rec.json) ? rec.json : null;

    // 身份字段：决定这条报文属于哪个测点
    const id = detectIdentity(json, card.groupBy);
    card.seenIdField = id ? id.field : '';
    const idSample = id ? itemSample(json, id) : null;
    // 注意：即使最后没画（超出上限、或没被勾选），也要记下来 —— 勾选列表要用
    if (id) noteSeenItem(card, id, idSample, rec.ts);

    const pickedItems = card.items || [];
    const pickedFields = card.fields || [];

    // ① 用户勾了测点 / 字段 → 只画勾上的（顺序按勾选顺序）
    if (pickedItems.length || pickedFields.length) {
      const out = [];

      // 测点：一条报文只属于一个测点，不是它的就跳过。
      // 这里**不能**像字段模式那样补空点，否则每条报文都会去戳一遍所有曲线。
      for (const name of pickedItems.slice(0, MAX_SELECT)) {
        if (id && id.name === name) out.push(idSample);
      }

      // 字段路径：沿用手动勾字段那套（纯数字载荷也能用）
      if (pickedFields.length) {
        if (!json && rec.num !== null) {
          const only = pickedFields[0];
          out.push({ key: only, label: only, num: rec.num, text: fmtNum(rec.num) });
        } else if (json) {
          for (const path of pickedFields.slice(0, MAX_SELECT)) {
            const v = getPath(json, path);
            if (v === undefined || v === null) {
              out.push({ key: path, label: path, num: null, text: '—' });
              continue;
            }
            const num = toPlotNum(v);
            if (num !== null) {
              const isBool = typeof v === 'boolean';
              out.push({
                key: path,
                label: path,
                num,
                text: isBool ? (num ? 'ON' : 'OFF') : (typeof v === 'string' ? v.trim() : fmtNum(num)),
                bool: isBool,
              });
            } else if (typeof v === 'object') {
              out.push({ key: path, label: path, num: null, text: JSON.stringify(v) });
            } else {
              out.push({ key: path, label: path, num: null, text: String(v) });
            }
          }
        }
      }
      return out;
    }

    // ② 自动 · 测点模式：有身份字段，且这条报文能定出一个"当前值"
    if (id) {
      // "能不能定出值"：有数字最好；只有文字也算（设备报 "运行" 这种）
      const usable = idSample.num !== null || idSample.text !== '—';
      // 注意这里**不做 MAX_SERIES 上限判断** —— 上限由建曲线的地方统一把关
      // （seriesAllowed）。放这里的话，mergeHistory 那种"先收集后建曲线"的批量
      // 处理会全程读到同一个 series.length，上限直接失效。
      if (usable) return [idSample];
      // 定不出唯一的值 → 说明这个主题其实是"一条报文多个数值"的形态（tag 只是标签），
      // 别把它的曲线丢了，继续往下走字段模式
    }

    // ③ 纯数字报文（最常见）→ 原样保留设备上报的小数位
    if (rec.num !== null) {
      const txt = String(rec.payload).trim();
      return [{ key: '__v', label: '', num: rec.num, text: isNumericText(txt) ? txt : fmtNum(rec.num) }];
    }

    // ④ JSON 报文 → 把里面所有数值/布尔字段挖出来，各画一条
    if (json) {
      const all = extractNumericFields(json).filter((f) => f.path !== card.seenIdField);
      const fields = all.slice(0, MAX_SERIES);
      // 字段太多时只画前 MAX_SERIES 条，多出来的在卡片上提示一下，别让人以为漏了
      card._overflowFields = Math.max(0, all.length - fields.length);
      if (fields.length > 0) {
        return fields.map((f) => ({
          key: f.path,
          label: f.path,
          num: f.num,
          text: f.text,          // 数字字符串保留设备原样（"62.40" 不被规整成 62.40）
          bool: f.bool,
        }));
      }
      // 里面没有可绘制的字段 → 只显示内容
      return [{ key: '__v', label: '', num: null, text: JSON.stringify(json) }];
    }

    // ⑤ 普通文本 → 显示原文，不画曲线
    return [{ key: '__v', label: '', num: null, text: rec.payload }];
  }

  /** 告警判定：超限告警，接近限值预警，带 3% 回差防抖 */
  function computeLevel(card, num) {
    if (num === null || !Number.isFinite(num)) return 'normal';
    const back = 0.03;
    const wasAlarm = card.level === 'alarm';

    let level = 'normal';

    if (card.high !== null && card.high !== undefined && card.high !== '') {
      const high = Number(card.high);
      const band = Math.abs(high) > 0 ? Math.abs(high) * 0.1 : 1;
      if (num > high) level = 'alarm';
      else if (num > high * (1 - back) && wasAlarm) level = 'alarm';
      else if (num > high - band) level = level === 'alarm' ? 'alarm' : 'warn';
    }
    if (level !== 'alarm' && card.low !== null && card.low !== undefined && card.low !== '') {
      const low = Number(card.low);
      const band = Math.abs(low) > 0 ? Math.abs(low) * 0.1 : 1;
      if (num < low) level = 'alarm';
      else if (num < low * (1 + back) && wasAlarm) level = 'alarm';
      else if (num < low + band) level = level === 'alarm' ? 'alarm' : 'warn';
    }
    return level;
  }

  /**
   * 从一条 ts 数组估上报节拍（最近 STALE_SAMPLE 个正间隔的中位数）。
   * 点数太少（估不准）→ null —— **不要在这里兜底 15 秒**，见 staleThreshold 的说明。
   */
  function intervalOfTs(ts) {
    if (!ts || ts.length < 3) return null;
    const n = Math.min(STALE_SAMPLE, ts.length - 1);
    const gaps = [];
    for (let i = ts.length - n; i < ts.length; i += 1) {
      const g = ts[i] - ts[i - 1];
      if (g > 0) gaps.push(g);
    }
    if (gaps.length < 2) return null;
    gaps.sort((a, b) => a - b);
    return gaps[Math.floor(gaps.length / 2)] || null;
  }

  /**
   * 这条曲线实测的「上报节拍」（相邻两点间隔的中位数，毫秒）。
   * 取点数最多的那条曲线、只用最近 STALE_SAMPLE 个间隔：设备改了上报周期也能跟上。
   * 点数太少（估不准）→ null，调用方退回兜底值。
   */
  function reportIntervalMs(card) {
    let best = null;
    for (const s of card.series || []) {
      if (!s || !s.ts || s.ts.length < 3) continue;
      if (!best || s.ts.length > best.length) best = s.ts;
    }
    return intervalOfTs(best);
  }

  /**
   * 这条卡片的离线阈值：实测节拍 ×3，夹在 [15 秒, 5 分钟] 之间。
   * ⚠️ 估不出节拍时返回 **null（＝判不了）**，而不是退回 15 秒兜底。
   *    "不知道它多久发一次"和"它掉线了"是两件事。页面刚打开、历史还没回填完的那几十秒里，
   *    卡片手上只有一两条报文、估不出节拍，若拿 15 秒当阈值，现场 60 秒节拍的设备
   *    会在每次上报后 15 秒就变「无数据」—— 实测 8 张卡里有 4 张在闪，全是误报。
   *    15 秒只在下限的意义上使用：**估出来的**节拍 ×3 不足 15 秒时抬到 15 秒。
   */
  function staleThreshold(card) {
    const iv = reportIntervalMs(card);
    if (!iv) return null;
    return Math.min(Math.max(iv * STALE_FACTOR, STALE_MS), STALE_MAX_MS);
  }

  function isStale(card) {
    if (!card || !card.lastTs) return false;
    const thr = staleThreshold(card);
    if (!thr) return false;        // 节拍还估不出来 → 先不判，宁可晚报也别误报
    return Date.now() - card.lastTs > thr;
  }

  /**
   * 分组卡里**某个成员主题**自己停发多久了 —— 这是卡片级判定看不见的盲点：
   * `card.lastTs` 取的是"卡片上最新的一条"，所以 plc 下 3 个点位里挂掉 1 个时，
   * 整张卡照样显示「正常」，那个掉线的点位会拿一个 10 分钟前的旧值一直摆着，
   * 看着像还活着。现场就是这么漏掉「济枣4标」掉线的。
   */
  function topicSilenceMs(card, topic) {
    const e = card.seenByTopic && card.seenByTopic.get(topic);
    if (!e || !e.lastTs) return 0;
    return Date.now() - e.lastTs;
  }

  /**
   * 这个成员主题自己的离线阈值。三级降级，**先信它自己的数据**：
   *   ① 它自己那条曲线（历史回填完就有几千个点，最可靠；也是唯一能区分
   *      "这个点位 5 秒一发、那个 70 秒一发"的来源）
   *   ② 它自己收到的实时间隔（曲线还没数据时用）
   *   ③ 卡片级节拍（同前缀的兄弟点位，节拍通常一样）
   * 三级都估不出来 → null（判不了）。**不能用卡片级去替代"知道这个点位多快"**：
   * `reportIntervalMs` 只取点数最多的那条曲线，混合节拍的卡里它会偏向快的那条，
   * 拿它去判慢点位就会一直误报离线 —— 这正是逐成员判定要避免的事。
   */
  function topicThreshold(card, topic) {
    let iv = null;
    for (const s of card.series || []) {
      if (topicKeyOf(s.key) !== topic) continue;
      iv = intervalOfTs(s.ts);
      if (iv) break;
    }
    if (!iv) {
      const e = card.seenByTopic && card.seenByTopic.get(topic);
      const gaps = (e && e.gaps) || [];
      if (gaps.length >= 2) {
        const sorted = [...gaps].sort((a, b) => a - b);
        iv = sorted[Math.floor(sorted.length / 2)] || null;
      }
    }
    if (!iv) iv = reportIntervalMs(card);
    if (!iv) return null;
    return Math.min(Math.max(iv * STALE_FACTOR, STALE_MS), STALE_MAX_MS);
  }

  function topicStale(card, topic) {
    const quiet = topicSilenceMs(card, topic);
    if (quiet <= 0) return false;
    const thr = topicThreshold(card, topic);
    if (!thr) return false;        // 节拍还没估出来 → 先不判
    return quiet > thr;
  }

  /** 分组卡里已经掉线的成员主题（按成员顺序，用于图例与提示） */
  function staleTopics(card) {
    if (!card || !card.group) return [];
    return groupMembers(card).filter((t) => topicStale(card, t));
  }

  /**
   * 看板上"无数据"的全景，供顶栏一处汇总：
   *   cards —— 整张卡都没数据了
   *   partial —— 卡片还在正常收，但组里有成员掉线了（最容易漏的一类）
   */
  function staleSummary() {
    const cards = [];
    const partial = [];
    for (const c of S.cards) {
      const quiet = isStale(c);
      if (quiet) cards.push(c);
      if (!c.group) continue;
      const gone = staleTopics(c);
      // 整卡已静默的就在 cards 里说过了，这里只报"部分掉线"，避免重复计数
      if (!quiet && gone.length) partial.push({ card: c, topics: gone });
    }
    return { cards, partial };
  }

  // ============================================================
  //  状态与统计
  // ============================================================
  function applyStatus(snap) {
    S.mqttStatus = snap.status;
    S.stats.connectedAt = snap.connectedAt;
    S.stats.uptimeMs = snap.uptimeMs;
    S.stats.totalMessages = snap.totalMessages;
    S.stats.topicCount = snap.topicCount;

    const dot = $('connDot');
    dot.className = 'brand-dot';
    let text = '未连接';
    if (snap.status === 'connected') {
      dot.classList.add('on');
      text = '已连接';
    } else if (snap.status === 'connecting') {
      dot.classList.add('ing');
      text = '连接中…';
    } else if (snap.status === 'reconnecting') {
      dot.classList.add('ing');
      text = '重连中…';
    } else if (snap.status === 'error') {
      dot.classList.add('err');
      text = '连接错误';
    }

    if (snap.status === 'connected') {
      const conn = readForm();
      $('connText').textContent = `${text} · ${conn.host}:${conn.port}`;
      $('connHint').textContent = '';
      $('btnConnectTop').textContent = '断开';
      $('btnConnectTop').dataset.mode = 'disconnect';
    } else {
      $('connText').textContent = text;
      $('btnConnectTop').textContent = '连接';
      $('btnConnectTop').dataset.mode = 'connect';
      $('connHint').textContent = snap.lastError ? '错误：' + snap.lastError : '';
    }

    if (Array.isArray(snap.subscriptions)) {
      S.subs = snap.subscriptions.slice();
      renderSubs();
    }
    updateTopStats();
  }

  function applyStats(st) {
    Object.assign(S.stats, st);
    updateTopStats();
  }

  function updateTopStats() {
    const meta = $('statMeta');

    // 已连接但一条订阅都没有 → 表现为"数据不动"，必须明确提示，否则很难排查
    if (S.mqttStatus === 'connected' && S.subs.length === 0) {
      meta.textContent = '⚠ 未订阅任何主题，收不到数据（去「连接设置」添加订阅）';
      meta.classList.add('is-warn');
    } else {
      const total = Number(S.stats.totalMessages || 0).toLocaleString('en-US');
      const topics = S.stats.topicCount ?? S.topics.size;
      meta.textContent =
        `${S.stats.rate ?? 0} msg/s · ${total} 条 · ${topics} 个主题 · 在线 ${fmtUptime(S.stats.uptimeMs)}`;
      meta.classList.remove('is-warn');
    }

    const cc = $('cardCount');
    if (cc) cc.textContent = S.cards.length ? `${S.cards.length} 个测点` : '';

    // 看板健康度汇总：只在"整卡静默 / 组里有成员掉线"时冒出来。
    // 预警、告警是「值越限」，卡片自己就在闪，不用这里重复；
    // 而"某个点位悄悄不发了"卡片级判定根本看不见，正是要靠这一行兜住。
    const alertEl = $('statAlert');
    if (alertEl) {
      const { cards, partial } = staleSummary();
      const lostTopics = partial.reduce((n, p) => n + p.topics.length, 0);
      const bits = [];
      if (cards.length) bits.push(`${cards.length} 张卡片无数据`);
      if (lostTopics) bits.push(`${partial.length} 张卡有 ${lostTopics} 个点位掉线`);

      if (bits.length) {
        alertEl.hidden = false;
        alertEl.textContent = '⚠ ' + bits.join(' · ');
        alertEl.classList.toggle('is-danger', cards.length > 0);
        alertEl._targetCard = cards[0] || (partial[0] && partial[0].card) || null;
        // 悬停给一份明细，免得"1 张卡有 3 个点位掉线"还要自己去翻是哪三个
        const names = [
          ...cards.map((c) => `${c.name}（整卡）`),
          ...partial.map((p) => `${p.card.name}（${p.topics.length} 个点位）`),
        ];
        alertEl.title = `已无新数据的看板：${names.slice(0, 6).join('、')}${names.length > 6 ? ` 等 ${names.length} 张` : ''}（点击跳到第一张）`;
      } else {
        alertEl.hidden = true;
        alertEl._targetCard = null;
        alertEl.title = '';
      }

      // 只绑一次。跳转用 card.el.root 而不是查 DOM（主题里带 / 和中文，选择器不好写）
      if (!alertEl._bound) {
        alertEl._bound = true;
        alertEl.addEventListener('click', () => {
          const c = alertEl._targetCard;
          if (!c || !c.el || !c.el.root) return;
          c.el.root.scrollIntoView({ behavior: 'smooth', block: 'center' });
          c.el.root.classList.remove('is-flash');
          void c.el.root.offsetWidth;        // 强制重排，连点同一张卡也能重放动画
          c.el.root.classList.add('is-flash');
          setTimeout(() => c.el.root.classList.remove('is-flash'), 1600);
        });
      }
    }

    updatePlaceholder();
  }

  /** 看板空态文案：区分"还没数据"和"订阅空了"两种情况 */
  function updatePlaceholder() {
    const ph = $('cardPlaceholder');
    if (!ph || S.cards.length > 0) return;

    const noSub = S.mqttStatus === 'connected' && S.subs.length === 0;
    const title = noSub ? '没有订阅任何主题' : '看板还是空的';
    const subHtml = noSub
      ? '平台已连上 broker，但没有任何订阅，所以收不到数据。<br>去左侧「连接设置」添加订阅，例如 <b>#</b> 收全部主题。'
      : '左侧「实时主题」里点 <b>+</b> 上板，或打开「自动上板」';

    const titleEl = ph.querySelector('p');
    const subEl = ph.querySelector('.ph-sub');
    if (titleEl && titleEl.textContent !== title) {
      titleEl.textContent = title;
      if (subEl) subEl.innerHTML = subHtml;
    }
  }

  // ============================================================
  //  卡片
  // ============================================================
  function createCard(def, silent) {
    // 分组卡片：topic 存的是**主题前缀**（一台设备），前缀下的每个主题一条曲线
    const group = def.group === true || def.group === 'true';
    const card = {
      topic: group ? groupRoot(def.topic) : def.topic,
      group,
      name: def.name || def.topic,
      unit: def.unit || '',
      fields: normFields(def.fields, def.field),  // 手动勾的字段路径；空数组 = 自动识别
      // 手动勾的「测点」（同一个主题下不同 tag/address 的报文）：
      //   空数组 = 自动（见到几个测点就画几个，上限 MAX_SERIES）
      items: normFields(def.items),
      // 身份字段路径；空 = 自动（tag → address → name → …，见 ID_FIELD_CANDIDATES）
      groupBy: String(def.groupBy || '').trim(),
      // 分组卡专用：主题 → 这条曲线画该主题的哪个字段（''/缺省 = 自动）
      picks: (def.picks && typeof def.picks === 'object' && !Array.isArray(def.picks))
        ? { ...def.picks } : {},
      // 分组卡专用：勾了要显示的主题（空数组 = 自动，按顺序画到上限）
      picked: normFields(def.picked),
      // 分组卡专用：显式删掉的主题（不再自动回到这张卡，弹窗里可以「恢复」）
      removed: normFields(def.removed),
      seenByTopic: new Map(),  // 分组卡专用：主题 → 见过的测点/字段
      members: [],             // 分组卡专用：落盘库里查到的成员主题
      seenItems: new Map(),    // 这个主题出现过的所有测点（勾选列表 + "+N" 提示都靠它）
      seenIdField: '',         // 实际生效的身份字段（弹窗里显示给用户看）
      low: def.low ?? null,
      high: def.high ?? null,
      // 每条曲线的点数上限（安全阀）：0 = 不限，曲线能铺多久由 X 轴时间窗口决定
      points: normPoints(def.points),
      // X 轴时间窗口（分钟）：null = 跟随顶栏全局默认；0 = 全部不限；>0 = 只看最近这么多分钟
      windowMin: normWindow(def.windowMin),
      series: [],              // 多条曲线：一个测点一条，或一个数值字段一条
      seriesMap: new Map(),    // key -> series，加速查找
      main: null,              // 主曲线（决定卡片大字与告警）
      text: '—',
      num: null,
      level: 'normal',
      count: 0,
      lastTs: 0,
      el: null,
    };
    S.cards.push(card);
    S.cardMap.set(card.topic, card);
    mountCard(card);
    if (!silent) saveCards();
    updateTopStats();
    // 新卡上板后顺手看一眼：和它同前缀的卡要不要并成一张
    // （自动上板就是这样一台设备先来一张、再来一张的，合并放在这里最省心）
    if (!group && !silent && S.autoGroup && !S.restoringCards) {
      void reconcileAutoGroups().catch(() => 0);
    }
    return card;
  }

  /** 清空一条消息带来的所有曲线（保留曲线本身的定义与颜色，只丢数据） */
  function resetSeries(card) {
    for (const s of card.series) { s.data = []; s.ts = []; s.num = null; s.text = '—'; }
    card.main = pickMainSeries(card);
    card.text = '—';
    card.num = null;
    card.level = 'normal';
    S.dirtyCanvas.add(card);
  }

  function mountCard(card) {
    const grid = $('cardGrid');
    const ph = $('cardPlaceholder');
    if (ph) ph.style.display = 'none';

    const root = document.createElement('div');
    root.className = 'card';
    root.dataset.topic = card.topic;
    root.innerHTML = `
      <div class="card-head">
        <span class="card-name"></span>
        <span class="card-state">正常</span>
        <button class="t-add" title="测点设置">⚙</button>
      </div>
      <div class="card-topic"></div>
      <div class="card-value"><span class="v"></span><span class="u"></span></div>
      <div class="card-legend"></div>
      <div class="card-plot">
        <canvas class="card-chart"></canvas>
        <canvas class="card-hover"></canvas>
      </div>
      <div class="card-foot"><span class="f-time">--</span><span class="f-note"></span></div>
    `;
    grid.appendChild(root);

    const el = {
      root,
      name: root.querySelector('.card-name'),
      state: root.querySelector('.card-state'),
      gear: root.querySelector('.t-add'),
      topic: root.querySelector('.card-topic'),
      value: root.querySelector('.v'),
      unit: root.querySelector('.u'),
      legend: root.querySelector('.card-legend'),
      plot: root.querySelector('.card-plot'),
      canvas: root.querySelector('.card-chart'),
      hover: root.querySelector('.card-hover'),
      time: root.querySelector('.f-time'),
      note: root.querySelector('.f-note'),
    };
    card.el = el;

    el.name.textContent = card.name;
    el.topic.textContent = card.topic;
    el.unit.textContent = card.unit || '';
    el.gear.title = '测点设置';

    // 鼠标悬停：定位到最近的那个数据点，十字线 + 提示框给出这一点的 X（时间）/ Y（值）。
    // 提示框挂在 <body> 上（position:fixed）—— 卡片是 overflow:hidden，且 :hover 时带
    // transform，放在卡片内部的 fixed 元素会以卡片为参照并被裁掉。
    el.canvas.addEventListener('mousemove', (e) => hoverMove(card, e));
    el.canvas.addEventListener('mouseleave', () => hoverClear(card));

    // 单击：放大/还原，方便看曲线
    root.addEventListener('click', (e) => {
      if (e.target === el.gear) {
        e.stopPropagation();
        openCardModal(card);
        return;
      }
      root.classList.toggle('card-large');
      requestAnimationFrame(() => markDirty(card));
    });

    // 尺寸变化（放大、窗口缩放）后重绘曲线
    if (window.ResizeObserver) {
      const ro = new ResizeObserver(() => markDirty(card));
      ro.observe(el.canvas);
    }

    updateCardDom(card);
    markDirty(card);
  }

  function updateCardDom(card) {
    const el = card.el;
    if (!el) return;

    el.name.textContent = card.name;
    el.topic.textContent = topicLabel(card);
    el.topic.title = card.group
      ? `分组卡片：${groupRoot(card.topic)}/ 下的所有主题各画一条曲线`
      : card.topic;
    el.unit.textContent = card.unit || '';

    // 大字跟着"离线"走：分组卡的主曲线掉线后，不能把它的旧值继续当当前值摆着。
    // 放在 DOM 渲染这一处（而不是只在来新报文时算），掉线那一拍才会立刻反映出来。
    const mv = mainValue(card);
    if (card.text !== mv.text || card.num !== mv.num) {
      card.text = mv.text;
      card.num = mv.num;
      card.level = computeLevel(card, card.num);
    }
    el.value.textContent = card.text;
    el.value.classList.toggle('text', isNaN(Number(card.text)) || card.num === null);

    // 多条曲线时给一份图例（色点 + 名称 + 当前值），否则分不清哪条是哪条
    // 页面刚刷新、历史回填阶段也要能算出 "+N"，所以这里重算一次（不能只靠 pushSample）
    card.overflow = computeOverflow(card);
    renderLegend(card);

    // 离线提示要"一眼看见 + 说清停了多久"，不能只靠鼠标悬停才知道判据：
    //  · 单主题卡：`无数据 3 分`
    //  · 分组卡：`无数据 1/3`（部分点位掉线）／`无数据 3/3`（整组静默）
    //    —— 部分掉线时卡片本身还在正常收数据，不单独标出来现场根本发现不了
    //
    // ⚠️ 这里有两级判定，千万别把"部分掉线"塞进 `if (stale)` 里：
    //    `isStale()` 看的是 `card.lastTs`（卡片上最新的一条），所以组里只挂掉一个点位时
    //    它**本来就是 false**（别的点位还在发）—— 那样写的话 `无数据 1/3` 这个标签
    //    会在它唯一该出现的场景里永远显示不出来（实测踩过）。
    const cardDead = isStale(card);                                // 整卡级：一条新的都没有
    const members = card.group ? groupMembers(card) : [];
    const gone = card.group ? staleTopics(card) : [];              // 逐成员：谁自己停发了
    const quietCard = card.lastTs ? Date.now() - card.lastTs : 0;
    const allQuiet = card.group ? (members.length > 0 && gone.length >= members.length) : cardDead;
    const partial = !allQuiet && gone.length > 0;                  // 卡片还在收，组里有成员掉线
    const noData = cardDead || partial;                            // 状态标签该说"无数据"吗

    el.root.classList.toggle('is-warn', !noData && card.level === 'warn');
    el.root.classList.toggle('is-alarm', !noData && card.level === 'alarm');
    // 整卡沉下去只在"整卡真没数据"时做；部分掉线时卡片本身还活着，
    // 把大字一起转灰会让人以为整张卡都死了
    el.root.classList.toggle('is-stale', cardDead);
    el.state.classList.toggle('is-stale', noData);
    el.state.classList.toggle('is-part', partial);

    if (partial) {
      el.state.textContent = `无数据 ${gone.length}/${members.length}`;
    } else if (cardDead) {
      // 组卡整卡静默时按理每个成员都该在 gone 里；`gone` 为空只可能是"某个成员自己的
      // 节拍比卡片级慢、还在它自己的容差内"—— 这时别硬拼出 `0/3`，老实说停了多久
      el.state.textContent = card.group && gone.length
        ? `无数据 ${gone.length}/${members.length}`
        : `无数据 ${fmtGap(quietCard)}`;
    } else {
      el.state.textContent = card.level === 'alarm' ? '告警'
        : card.level === 'warn' ? '预警' : '正常';
    }
    el.state.style.opacity = '';        // 具体颜色/明暗交给 CSS，别再让它淡到看不见
    // 灰标签最容易被误会成"平台没收数据"，鼠标停上去把判据和人话都写清
    const thr = staleThreshold(card);
    const iv = reportIntervalMs(card);
    if (noData) {
      const bits = [];
      if (card.group && gone.length) {
        bits.push(gone.length >= members.length
          ? `${members.length} 个点位全部没有新数据`
          : `${gone.length}/${members.length} 个点位没有新数据：${gone.slice(0, 4).join('、')}${gone.length > 4 ? ` 等 ${gone.length} 个` : ''}`);
        const t0 = gone.find((t) => topicSilenceMs(card, t) > 0);
        if (t0) bits.push(`停最久的是 ${t0}：${fmtGap(topicSilenceMs(card, t0))}`);
      }
      if (quietCard > 0) bits.push(`卡片上最近一条是 ${fmtGap(quietCard)} 前（${fmtTime(card.lastTs, true)}）`);
      // 阈值可能是 null（卡片级节拍还没估出来，只有成员级判得出来），别把 null 说成「0 秒」
      bits.push(`判据：上报节拍约 ${iv ? fmtGap(iv) : '未知'}，超过 ${
        thr ? fmtGap(thr) : (gone.length ? `${gone.length} 个点位各自的节拍 ×3` : '——')} 没数据就算掉线`);
      el.state.title = bits.join('；');
    } else {
      el.state.title = iv ? `上报节拍约 ${fmtGap(iv)}` : '';
    }

    el.time.textContent = card.lastTs ? fmtTime(card.lastTs, true) : '--';
  }

  /**
   * 图例里要显示哪几行。
   *
   * 单主题卡：就是它的几条曲线。
   * 分组卡：**卡片上每个主题一行**，不管画不画曲线 —— 用户要的是"选了哪些主题上卡片，
   * 就实时看得到它们的值"。画曲线的用曲线自己的颜色，没画曲线的用灰空心点区分，
   * 值都取自该主题最近一条报文。行数有上限（和曲线一样最多 MAX_SERIES 行），
   * 多出来的用 `+N` 交代，而不是让图例顶出卡片。
   */
  function legendItems(card) {
    if (!card.group) {
      return (card.series || []).map((s) => ({
        label: s.label || s.key, text: s.text, color: s.color,
        empty: s.num === null, noCurve: false,
      }));
    }
    const out = [];
    const drawn = new Set((card.series || []).map((s) => topicKeyOf(s.key)));
    // 画曲线的排前面（顺序和图上一致，颜色对得上），只显示值的不占曲线名额排在后面
    const members = groupMembers(card);
    const order = [...members.filter((t) => drawn.has(t)), ...members.filter((t) => !drawn.has(t))];
    for (const t of order.slice(0, MAX_SERIES)) {
      const e = card.seenByTopic && card.seenByTopic.get(t);
      const ser = card.seriesMap.get(topicKey(t, desiredFieldKey(card, t) || '__v'));
      const isDrawn = !!ser;
      const stale = topicStale(card, t);
      out.push({
        label: (isDrawn && ser.label) || (e && e.autoLabel) || tailName(t),
        // ★ 掉线的点位不能继续把它"最后一次的值"当当前值摆着 —— 那看着像还活着。
        //   撤成「—」，并在行上加 is-stale + title 说明停了多久。
        text: stale ? '—' : (isDrawn ? ser.text : ((e && e.lastText) || '—')),
        color: isDrawn ? ser.color : '',
        empty: stale ? true
          : (isDrawn ? ser.num === null : !(e && e.lastNum !== null && e.lastNum !== undefined)),
        noCurve: !isDrawn,
        stale,
        quiet: stale ? topicSilenceMs(card, t) : 0,
      });
    }
    return out;
  }

  function renderLegend(card) {
    const el = card.el;
    if (!el?.legend) return;
    const items = legendItems(card);
    // 只有一条、也没有"没显示出来的主题"时才不需要图例
    if (items.length <= 1 && !card.overflow) {
      if (el.legend.childElementCount || el.legend._sig) {
        el.legend.innerHTML = '';
        el.legend._sig = '';
        el.legend.style.display = 'none';
      }
      return;
    }
    // 值没变就不重写 DOM（这个函数每帧都可能被调用）
    // ⚠️ 签名里必须带 stale：掉线转换时值可能没变（旧值 → 「—」是变了，但"恢复"时
    //    值同样会变），只比 label/text 会在个别时序下漏掉一次刷新。
    const sig = items.map((it) => `${it.label}:${it.text}:${it.noCurve ? 'n' : 'y'}:${it.stale ? 's' : ''}`).join('|')
      + '#' + (card.overflow || 0);
    if (el.legend._sig === sig) return;
    el.legend._sig = sig;
    el.legend.style.display = '';
    const more = card.overflow
      ? `<span class="lg-more" title="卡片上还有 ${card.overflow} 个主题没地方列出来（打开「测点设置」可以删掉不要的，或把曲线数控制在 ${MAX_SERIES} 条内）">+${card.overflow}</span>`
      : '';
    el.legend.innerHTML = items.map((it) => `
      <span class="lg-item${it.empty ? ' is-empty' : ''}${it.noCurve ? ' is-nocurve' : ''}${it.stale ? ' is-stale' : ''}"${
  it.stale ? ` title="${esc(it.label)}：已经 ${fmtGap(it.quiet)} 没有新数据了"` : ''}>
        <i class="lg-dot"${it.noCurve ? '' : ` style="background:${it.color}"`}></i>
        <span class="lg-name">${esc(it.label)}</span>
        <b class="lg-val">${esc(it.text)}</b>
      </span>
    `).join('') + more;
  }

  function esc(s) {
    return String(s === null || s === undefined ? '' : s)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
  }

  function updateAllCardDoms() {
    for (const c of S.cards) updateCardDom(c);
  }

  function removeCard(card) {
    S.cardMap.delete(card.topic);
    const i = S.cards.indexOf(card);
    if (i >= 0) S.cards.splice(i, 1);
    card.el?.root.remove();
    if (S.cards.length === 0) {
      const ph = $('cardPlaceholder');
      if (ph) ph.style.display = '';
    }
    renderTopics();
    saveCards();
    updateTopStats();
  }

  function renderAllCards() {
    $('cardGrid').innerHTML = '';
    if (S.cards.length === 0) {
      $('cardGrid').innerHTML = $('cardPlaceholder')?.outerHTML || '';
      const ph = $('cardPlaceholder');
      if (ph) ph.style.display = '';
    }
    for (const c of S.cards) { c.el = null; mountCard(c); }
  }

  let saveTimer = null;
  function saveCards() {
    clearTimeout(saveTimer);
    saveTimer = setTimeout(() => {
      api('/api/cards', {
        cards: S.cards.map((c) => ({
          topic: c.topic, name: c.name, unit: c.unit, fields: c.fields || [],
          items: c.items || [],            // 勾选的测点（空 = 自动）
          groupBy: c.groupBy || '',        // 身份字段路径（空 = 自动）
          // 分组卡片：group=true 时 topic 存的是主题前缀，picks 是「每个主题画哪个字段」
          group: !!c.group,
          picks: c.group ? { ...(c.picks || {}) } : {},
          picked: c.group ? [...(c.picked || [])] : [],   // 勾了要显示的主题（空 = 自动）
          removed: c.group ? [...(c.removed || [])] : [], // 显式删掉的主题（不再自动回来）
          low: c.low, high: c.high, points: c.points, windowMin: c.windowMin ?? null,
        })),
      }).catch((err) => console.warn('保存看板失败', err.message));
    }, 500);
  }

  // ============================================================
  //  曲线绘制
  // ============================================================
  function markDirty(card) {
    S.dirtyCanvas.add(card);
    scheduleRender();
  }

  function scheduleRender() {
    if (S.renderTimer) return;
    S.renderTimer = setTimeout(() => {
      S.renderTimer = null;
      renderAll();
    }, FLUSH_RENDER_MS);
  }

  function renderAll() {
    if (S.paused) return;
    const gridH = $('cardGrid').scrollTop;
    updateAllCardDoms();
    drawAllCanvases();
    renderLogs();
    renderTopics(true);
    updateTopStats();
    // 保持网格滚动位置（数据更新不应打断用户浏览）
    $('cardGrid').scrollTop = gridH;
  }

  function drawAllCanvases() {
    const list = S.dirtyCanvas;
    S.dirtyCanvas = new Set();
    for (const card of list) drawCard(card);
    // 放大/缩小时的尺寸变化也可能未标记，兜底重绘可见卡片
    for (const card of S.cards) {
      if (!card.el) continue;
      const w = card.el.canvas.clientWidth;
      if (w > 0 && card._lastW !== w) drawCard(card);
    }
  }

  function drawCard(card) {
    const cv = card.el?.canvas;
    if (!cv) return;
    const w = cv.clientWidth;
    const h = cv.clientHeight;
    if (w <= 0 || h <= 0) return;

    const dpr = window.devicePixelRatio || 1;
    if (cv.width !== Math.round(w * dpr) || cv.height !== Math.round(h * dpr)) {
      cv.width = Math.round(w * dpr);
      cv.height = Math.round(h * dpr);
    }
    card._lastW = w;

    const ctx = cv.getContext('2d');
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, w, h);

    const css = getComputedStyle(document.body);
    const accent = css.getPropertyValue('--accent').trim();
    const dangerColor = css.getPropertyValue('--danger').trim();
    const gridColor = css.getPropertyValue('--border-soft').trim();
    const axisColor = css.getPropertyValue('--text-3').trim() || '#98a2ae';

    // ---------------- 时间窗口：先把窗口外的点切掉 ----------------
    // 窗口起点锚在「这张卡片最新一条数据的时间」，不是 Date.now()。
    // 用 Date.now() 的话：设备停发、或刚刷新页面从库里回放历史时，
    // 窗口会一直往前滑、把所有数据甩出去，曲线整屏变空 —— 看着像数据丢了。
    const winMin = effectiveWindowMin(card);
    const anchorTs = cardAnchorTs(card);
    const fromTs = (winMin > 0 && anchorTs > 0) ? anchorTs - winMin * 60000 : -Infinity;

    // views = 每条曲线的「窗口内切片」。下面一律用 v.data / v.ts，不再碰 s.data
    const views = [];
    for (const s of card.series || []) {
      if (s.data.length === 0) continue;
      const aligned = s.ts && s.ts.length === s.data.length;
      let i0 = 0;
      if (Number.isFinite(fromTs) && aligned) {
        // 留最后一个点：锚点就是最新数据，它必然落在窗口内
        while (i0 < s.ts.length - 1 && s.ts[i0] < fromTs) i0 += 1;
      }
      const data = i0 === 0 ? s.data : s.data.slice(i0);
      if (data.length === 0) continue;
      views.push({ s, data, ts: aligned ? (i0 === 0 ? s.ts : s.ts.slice(i0)) : null });
    }

    if (views.length === 0) {
      // 数据不足以成线时，画一条基线提示
      ctx.strokeStyle = gridColor;
      ctx.lineWidth = 1;
      ctx.beginPath();
      ctx.moveTo(0, h - 1.5);
      ctx.lineTo(w, h - 1.5);
      ctx.stroke();
      // 没曲线可指了，把悬停的十字线一起收掉，免得留一条指空的虚线
      card._markers = [];
      card._geometry = null;
      hoverClear(card);
      return;
    }
    // 主曲线排到最后画，压在其它线上面
    if (views.length > 1 && card.main) {
      const mi = views.findIndex((v) => v.s === card.main);
      if (mi >= 0 && mi < views.length - 1) views.push(views.splice(mi, 1)[0]);
    }
    const solo = views.length === 1;

    // 每条曲线各自算 Y 轴范围。
    // 一条 JSON 里可能同时有 62.5(温度) 和 0.83(压力)，共用一个 Y 轴会把小的那条压成平线。
    for (const v of views) {
      // 用循环而不是 Math.min(...arr)：窗口放大后点数可能上万，展开会爆栈
      let min = Infinity;
      let max = -Infinity;
      for (const val of v.data) { if (val < min) min = val; if (val > max) max = val; }
      if (v.s.bool) { min = Math.min(min, 0); max = Math.max(max, 1); }
      if (max - min < 1e-9) {
        const bump = Math.abs(max) * 0.05 || 1;
        max += bump; min -= bump;
      }
      if (solo) {
        // 单条曲线：上下限只有"离得足够近"时才纳入，
        // 否则 60~63℃ 的数据被 50~70 的阈值撑开，曲线会压成一条平线看不出趋势。
        const span = max - min;
        const reach = (val) => val >= min - span * 1.5 && val <= max + span * 1.5;
        const hi = numOrNull(card.high);
        const lo = numOrNull(card.low);
        if (hi !== null && reach(hi)) max = Math.max(max, hi);
        if (lo !== null && reach(lo)) min = Math.min(min, lo);
      }
      const pad = (max - min) * 0.12;
      v._min = min - pad;
      v._max = max + pad;
    }

    // 曲线实际铺开的真实时间跨度（不是窗口长度）：窗口选「全部」、或数据被抽稀时，
    // 这个数字才是"我到底看到了多久的数据"。界面上的小字直接用它。
    let spanMs = 0;
    for (const v of views) {
      if (!v.ts || v.ts.length < 2) continue;
      const d = v.ts[v.ts.length - 1] - v.ts[0];
      if (d > spanMs) spanMs = d;
    }

    // 把「这次到底画了什么」挂到卡片上 —— 控制台里能直接看，自检脚本也读它，
    // 免得去猜"窗口到底生效没有、画了几个点"。
    card._chart = {
      winMin,                                                    // 生效的窗口（分钟），0 = 全部
      points: views.reduce((m, v) => Math.max(m, v.data.length), 0), // 窗口内最长那条的点数
      rawPoints: views.reduce((m, v) => Math.max(m, v.data.length), 0), // 抽稀前的点数
      dropped: 0,                                                // 被抽稀掉多少点
      spanMs,                                                    // 实际铺开的时间跨度
      series: views.length,
      fromTs: Number.isFinite(fromTs) ? fromTs : null,            // 窗口起点（null = 不限）
      toTs: anchorTs || null,                                     // 窗口锚点（最新数据时间）
    };

    // ---------------- 坐标轴布局 ----------------
    // 绘图区要为刻度让出边距，曲线画在里面。
    // Y 轴只能有一个量程：单条曲线就用它自己的真实值；
    // 多条曲线各自归一化、量程本来就不同，就取主曲线的刻度，并用主曲线的颜色标明归属。
    const big = h >= 90;                    // 放大模式给更细的刻度
    const fs = big ? 11 : 9;
    const font = `${fs}px system-ui, -apple-system, "Segoe UI", sans-serif`;
    ctx.font = font;

    // 纵向放得下几条 Y 标签：按字号留出行距算，宁少勿叠。
    // 以前是固定的 2 / 4 档，44px 高的小卡片只有 2 档，等于没有刻度；
    // 现在按可用高度自适应，能排几条就排几条。
    const padT = big ? 6 : 3;
    const padB = fs + (big ? 5 : 4);
    const maxYLabels = Math.max(2, Math.floor((h - padT - padB) / (fs + 3)) + 1);

    // Y 轴量程取主曲线；主曲线没数据（被窗口切掉了）就退到最后一条。
    // ⚠️ 主曲线所属主题掉线时同理要让位：不然刻度会停在一条 10 分钟没动过的曲线上，
    //    跟上面的大字（已经改挑在线点位，见 mainValue）对不上，看着像数据错位。
    const viewDead = (v) => {
      const t = topicKeyOf(v.s && v.s.key);
      return !!t && topicStale(card, t);
    };
    let yRef = solo ? views[0] : null;
    if (!yRef) yRef = views.find((v) => v.s === card.main && !viewDead(v)) || null;
    if (!yRef) yRef = views.find((v) => !viewDead(v)) || null;
    if (!yRef && card.main) yRef = views.find((v) => v.s === card.main) || null;
    if (!yRef) yRef = views[views.length - 1];
    if ((yRef._max - yRef._min) <= 0) yRef = views[0];
    // 挂到 _chart 上：自检脚本要验"刻度没有停在掉线那条曲线上"
    if (card._chart) {
      card._chart.yRef = (yRef.s && yRef.s.key) || '';
      card._chart.yRefMin = yRef._min;
      card._chart.yRefMax = yRef._max;
    }

    // 刻度想要几档：niceTicks 的步长只走 1/2/5×10^n 这条梯子、而且只向上取整，
    // 所以要的档数得给得比实际想要的富裕一点，最终能排几条由 maxYLabels 兜底。
    const yTicks = thinTicks(niceTicks(yRef._min, yRef._max, big ? 8 : 3), maxYLabels);
    const yLabels = yTicks.map((v) => fmtAxis(v, yRef._max - yRef._min));
    const maxLabelW = Math.max(0, ...yLabels.map((t) => ctx.measureText(t).width));

    const padL = Math.min(Math.ceil(maxLabelW) + (big ? 10 : 7), Math.max(w * 0.34, 30));
    const padR = big ? 10 : 4;

    const plotX0 = padL;
    const plotX1 = Math.max(w - padR, plotX0 + 10);
    const plotY0 = padT;
    const plotY1 = Math.max(h - padB, plotY0 + 10);
    const plotW = plotX1 - plotX0;
    const plotH = plotY1 - plotY0;

    // ---------------- 点太多就抽稀 ----------------
    // 上限取「用户设的点数」（没设就是硬上限）与「画布像素能分辨的点数」里更小的那个。
    // 关键：抽稀是**均匀抽**、首尾都保留，整段窗口还在，只是点稀了；
    // 老逻辑是截断（只留最后 120 点），那才是用户看到的"曲线显示不全"。
    // Y 轴量程上面已经用完整窗口数据算过了，所以抽稀不会把尖峰抹掉。
    const drawCap = Math.min(drawCapOf(card) || POINTS_HARD_MAX, Math.max(600, Math.round(plotW) * PX_PER_POINT));
    let dropped = 0;
    for (const v of views) {
      if (v.data.length > drawCap) {
        const d = decimate(v.data, v.ts, drawCap);
        v.data = d.data;
        v.ts = d.ts;
        dropped += d.dropped;
      }
    }
    if (card._chart) {
      card._chart.points = views.reduce((m, v) => Math.max(m, v.data.length), 0);
      card._chart.dropped = dropped;
      if (card.el && card.el.note) card.el.note.textContent = chartNote(card);
    }

    const X = (i, n) => (n <= 1 ? plotX1 : plotX0 + (i / (n - 1)) * plotW);
    const Y = (s, v) => plotY1 - ((v - s._min) / (s._max - s._min)) * plotH;

    // ---------------- 画 X / Y 刻度 ----------------
    ctx.save();
    ctx.font = font;
    ctx.lineWidth = 1;
    const refColor = solo ? axisColor : (yRef.s.color || accent);

    for (let i = 0; i < yTicks.length; i += 1) {
      const y = Math.round(Y(yRef, yTicks[i])) + 0.5;
      if (y < plotY0 - 1 || y > plotY1 + 1) continue;
      ctx.globalAlpha = 0.6;                // 水平网格线：很淡，不抢曲线
      ctx.strokeStyle = gridColor;
      ctx.beginPath();
      ctx.moveTo(plotX0, y);
      ctx.lineTo(plotX1, y);
      ctx.stroke();
      ctx.globalAlpha = 0.95;
      ctx.fillStyle = refColor;
      ctx.textAlign = 'right';
      ctx.textBaseline = 'middle';
      ctx.fillText(yLabels[i], plotX0 - (big ? 5 : 4), y);
    }

    // 两条轴线
    ctx.globalAlpha = 1;
    ctx.strokeStyle = gridColor;
    ctx.beginPath();
    ctx.moveTo(plotX0 + 0.5, plotY0);
    ctx.lineTo(plotX0 + 0.5, plotY1);
    ctx.moveTo(plotX0, plotY1 + 0.5);
    ctx.lineTo(plotX1, plotY1 + 0.5);
    ctx.stroke();

    // X 轴时间：曲线是按索引等距画的，所以只标真实存在的首尾时间
    // （放大时补一个中点），不去编造等分刻度。
    // 参考曲线取**点数最多**的那条 —— 它覆盖的时间窗最宽。
    // 固定用第一条会踩坑：刚起步的短曲线（甚至只有 1 个点）排在最前时，
    // X 轴会整段标不出来，而别的曲线其实有满屏数据。
    let xRef = views[0];
    for (const v of views) {
      if (v.ts && v.ts.length > (xRef.ts ? xRef.ts.length : 0)) xRef = v;
    }
    const tsArr = (xRef.ts && xRef.ts.length === xRef.data.length && xRef.ts.length >= 2)
      ? xRef.ts : null;
    if (tsArr) {
      const spanMs = tsArr[tsArr.length - 1] - tsArr[0];
      const ty = plotY1 + (big ? 6 : 4);
      const nTs = tsArr.length;
      const txt = tsArr.map((t) => fmtAxisTime(t, spanMs));

      // 能标几个：按实测文字宽度算，宽卡片就多标几个。
      // 以前固定「首 + 尾（放大时补中点）」，卡片一宽中间一大段没有参照，
      // 只能肉眼估时间；现在放得下几条就标几条。
      const wOne = Math.max(0, ...txt.map((t) => ctx.measureText(t).width));
      const want = Math.max(2, Math.min(big ? 5 : 3, Math.floor(plotW / (wOne + (big ? 30 : 22))) + 1));

      // 均匀取索引；最后一个固定取 nTs-1，这样「最右 = 最新数据时间」不会漂
      const idx = [];
      for (let k = 0; k < want; k += 1) {
        const i = k === want - 1 ? nTs - 1 : Math.round((k * (nTs - 1)) / (want - 1));
        if (!idx.includes(i)) idx.push(i);
      }

      ctx.fillStyle = axisColor;
      ctx.globalAlpha = 0.95;
      ctx.textBaseline = 'top';
      for (let k = 0; k < idx.length; k += 1) {
        const i = idx[k];
        if (k === 0) {
          ctx.textAlign = 'left';
          ctx.fillText(txt[i], plotX0, ty);          // 最左贴绘图区左缘
        } else if (i === nTs - 1) {
          ctx.textAlign = 'right';
          ctx.fillText(txt[i], plotX1, ty);          // 最右贴绘图区右缘
        } else {
          ctx.textAlign = 'center';
          ctx.fillText(txt[i], X(i, nTs), ty);       // 中间按数据点实际位置居中
        }
      }
    }
    ctx.restore();

    // 限值参考线：只有能确定它属于哪条曲线时才画
    // （单条曲线就是它自己；多条曲线时看用户是否指定了字段）
    const owner = solo ? views[0]
      : ((card.fields || [])[0] ? views.find((v) => v.s.key === card.fields[0]) || null : null);
    if (owner) {
      const drawLimit = (val) => {
        const lim = numOrNull(val);
        if (lim === null) return;
        const y = Y(owner, lim);
        if (y < plotY0 - 1 || y > plotY1 + 1) return;
        ctx.save();
        ctx.strokeStyle = dangerColor;
        ctx.globalAlpha = .45;
        ctx.setLineDash([4, 3]);
        ctx.lineWidth = 1;
        ctx.beginPath();
        ctx.moveTo(plotX0 + 0.5, y);
        ctx.lineTo(plotX1, y);
        ctx.stroke();
        ctx.restore();
      };
      drawLimit(card.high);
      drawLimit(card.low);
    }

    for (const v of views) {
      const n = v.data.length;
      const color = v.s.color || accent;

      // 面积渐变只给单条曲线，多条叠加会糊成一片
      if (solo && n >= 2) {
        const grad = ctx.createLinearGradient(0, plotY0, 0, plotY1);
        grad.addColorStop(0, hexToRgba(color, .22));
        grad.addColorStop(1, hexToRgba(color, 0));
        ctx.beginPath();
        ctx.moveTo(X(0, n), plotY1);
        for (let i = 0; i < n; i += 1) ctx.lineTo(X(i, n), Y(v, v.data[i]));
        ctx.lineTo(X(n - 1, n), plotY1);
        ctx.closePath();
        ctx.fillStyle = grad;
        ctx.fill();
      }

      if (n >= 2) {
        ctx.beginPath();
        if (v.s.bool) {
          // 开关量画台阶：先横走再竖跳，不要斜线
          ctx.moveTo(X(0, n), Y(v, v.data[0]));
          for (let i = 1; i < n; i += 1) {
            ctx.lineTo(X(i, n), Y(v, v.data[i - 1]));
            ctx.lineTo(X(i, n), Y(v, v.data[i]));
          }
        } else {
          for (let i = 0; i < n; i += 1) {
            const x = X(i, n), y = Y(v, v.data[i]);
            if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
          }
        }
        ctx.strokeStyle = color;
        ctx.lineWidth = solo ? 1.6 : 1.5;
        ctx.lineJoin = 'round';
        ctx.stroke();
      }

      // 最新点
      const lx = X(n - 1, n);
      const ly = Y(v, v.data[n - 1]);
      ctx.beginPath();
      ctx.arc(lx, ly, solo ? 2.6 : 2, 0, Math.PI * 2);
      ctx.fillStyle = color;
      ctx.fill();
      if (solo) {
        ctx.beginPath();
        ctx.arc(lx, ly, 5.2, 0, Math.PI * 2);
        ctx.strokeStyle = hexToRgba(color, .3);
        ctx.lineWidth = 1.4;
        ctx.stroke();
      }
    }

    // ---------------- 给鼠标悬停留一份「画到哪了」 ----------------
    // 这里是 canvas 的逻辑像素坐标（ctx 已按 dpr 缩放过），覆层按自己的尺寸算比例就能直接用。
    // 每次重绘都重建：抽稀、窗口裁剪、尺寸变化都会让点位的索引平移，
    // 复用上一轮的下标会指到别的点上。
    card._geometry = { plotX0, plotX1, plotY0, plotY1, plotW, plotH };
    card._markers = [];
    for (const v of views) {
      const n = v.data.length;
      if (n === 0) continue;
      const xs = new Float64Array(n);
      const ys = new Float64Array(n);
      for (let i = 0; i < n; i += 1) { xs[i] = X(i, n); ys[i] = Y(v, v.data[i]); }
      card._markers.push({
        key: v.s.key,
        label: v.s.label || v.s.key,
        color: v.s.color || accent,
        bool: !!v.s.bool,
        // 单位只对"唯一的/主"曲线有意义，多条曲线量纲本来就可能不一样
        unit: (solo || v.s === card.main) ? (card.unit || '') : '',
        n,
        xs,
        ys,
        data: v.data,                                    // 只存引用，取值时直接读
        ts: (v.ts && v.ts.length === n) ? v.ts : null,
      });
    }
    if (card._markers.length === 0) card._geometry = null;

    // 数据每帧都在更新，十字线要跟着新点位走，不能停在上一帧的位置
    if (card._hoverLocal && card._geometry) drawHover(card, card._hoverLocal.x, card._hoverLocal.y);
  }

  /**
   * 刻度太密就等距抽掉几档。
   *
   * 只能挑「每 q 档留一档」的等间隔子序列 —— 均匀性不能破：
   * 刻度不等距比刻度少更糟，读数会按错误的步长去估。
   * 所以 q 必须整除 (m-1)，这样最后一段的间距和前面完全一致；
   * 找不到合数的（m 很小）就退化成只留首尾两档，也就是老行为。
   */
  function thinTicks(ticks, maxN) {
    const m = ticks.length;
    if (!(maxN > 0) || m <= maxN || maxN < 2) return ticks;
    const gaps = m - 1;
    for (let q = 1; q <= gaps; q += 1) {
      if (gaps % q !== 0) continue;
      if (gaps / q + 1 > maxN) continue;
      const out = [];
      for (let i = 0; i <= gaps; i += q) out.push(ticks[i]);
      return out;
    }
    return [ticks[0], ticks[m - 1]];
  }

  /** Y 轴刻度的“好看”取值：步长取 1 / 2 / 5 × 10^n，避免出现 3.333 这种刻度 */
  function niceTicks(min, max, count) {
    const span = max - min;
    if (!Number.isFinite(span) || span <= 0) return [min];

    // 在 1 / 2 / 5 × 10^n 这条梯子上，取不小于 raw 的最小值
    const niceStep = (raw) => {
      const mag = Math.pow(10, Math.floor(Math.log10(Math.abs(raw) || 1)));
      const n = Math.abs(raw) / mag;
      return (n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10) * mag;
    };

    let step = niceStep(span / Math.max(count, 1));
    // 兜底：刻度少于 2 条就失去参照意义了。
    // 比如量程 100~1450、count=2 时 raw=675 → 步长取到 1000，落在区间里只剩 1000 这一条。
    // 这时沿梯子降一档（1000 → 500 → 200 → 100 …），保证至少两条刻度。
    const tickCount = (s) => Math.floor(max / s) - Math.ceil(min / s) + 1;
    for (let guard = 0; guard < 20 && tickCount(step) < 2; guard += 1) {
      step = niceStep(step / 2.5);
    }

    // 用「首个刻度 + i×步长」而不是反复累加，减少浮点漂移（0.8+0.05 → 0.8500000000000001）
    const first = Math.ceil(min / step) * step;
    const n = Math.floor((max + step * 1e-6 - first) / step) + 1;
    const clean = (v) => Number(v.toPrecision(12));
    const out = [];
    for (let i = 0; i < n; i += 1) out.push(clean(first + i * step));
    return out.length >= 2 ? out : [min, max];
  }

  /** 轴标签格式化：按量程决定小数位，大数用 k / M 缩写 */
  function fmtAxis(v, span) {
    if (!Number.isFinite(v)) return '';
    const abs = Math.abs(v);
    if (abs >= 1e6) return (v / 1e6).toFixed(abs >= 1e7 ? 0 : 1) + 'M';
    if (abs >= 1e5) return Math.round(v / 1e3) + 'k';
    if (abs >= 1e4) return (v / 1e3).toFixed(1) + 'k';
    const s = Math.abs(span);
    const d = s >= 100 ? 0 : s >= 10 ? 1 : s >= 1 ? 2 : s >= 0.1 ? 3 : s >= 0.01 ? 4 : 5;
    let out = v.toFixed(d);
    if (d > 0 && out.includes('.')) out = out.replace(/0+$/, '').replace(/\.$/, '');
    return out === '' || out === '-' ? '0' : out;
  }

  function numOrNull(v) {
    if (v === null || v === undefined || v === '') return null;
    const n = Number(v);
    return Number.isFinite(n) ? n : null;
  }

  function hexToRgba(color, alpha) {
    const c = (color || '#409EFF').trim();
    if (c.startsWith('#')) {
      let hex = c.slice(1);
      if (hex.length === 3) hex = hex.split('').map((x) => x + x).join('');
      const r = parseInt(hex.slice(0, 2), 16);
      const g = parseInt(hex.slice(2, 4), 16);
      const b = parseInt(hex.slice(4, 6), 16);
      return `rgba(${r},${g},${b},${alpha})`;
    }
    return c;
  }

  // ============================================================
  //  鼠标悬停取点（读任意一点的 X / Y）
  // ============================================================
  // 鼠标在图上滑过 → 找横坐标最近的那个数据点 → 画十字线，用提示框给出
  // 这一点的 X（时间）与每条曲线的 Y（值）。
  // 竖线只有一根：多曲线时先按时间把各条曲线对齐到同一时刻，读数是"同一时刻的各路数据"，
  // 而不是各对一条线（各算各的最近点会得到 5 个不同时间，读数看起来就自相矛盾）。
  let tipEl = null;

  function ensureTip() {
    if (!tipEl || !tipEl.isConnected) tipEl = $('chartTip');
    return tipEl;
  }

  /** 单调递增数组里找最接近 target 的下标。二分而不是遍历：放大后一条曲线可能上万点 */
  function nearestIdx(arr, target) {
    const n = arr ? arr.length : 0;
    if (n === 0) return -1;
    if (target <= arr[0]) return 0;
    if (target >= arr[n - 1]) return n - 1;
    let lo = 0;
    let hi = n - 1;
    while (hi - lo > 1) {
      const mid = (lo + hi) >> 1;
      if (arr[mid] <= target) lo = mid; else hi = mid;
    }
    return (target - arr[lo]) <= (arr[hi] - target) ? lo : hi;
  }

  /** 提示框里的时间：跨天补上日期，否则只给时分秒 */
  function fmtTipTime(ts) {
    const d = new Date(ts);
    const p = (n, w) => String(n).padStart(w || 2, '0');
    const hm = `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
    const now = new Date();
    const sameDay = d.getFullYear() === now.getFullYear()
      && d.getMonth() === now.getMonth() && d.getDate() === now.getDate();
    return sameDay ? hm : `${p(d.getMonth() + 1)}-${p(d.getDate())} ${hm}`;
  }

  /** 把画布尺寸对齐 dpr 并清空，返回可直接画的 ctx（覆层每次移动都要清，单独抽出来） */
  function clearCanvas(cv) {
    const w = cv.clientWidth;
    const h = cv.clientHeight;
    if (w <= 0 || h <= 0) return null;
    const dpr = window.devicePixelRatio || 1;
    if (cv.width !== Math.round(w * dpr) || cv.height !== Math.round(h * dpr)) {
      cv.width = Math.round(w * dpr);
      cv.height = Math.round(h * dpr);
    }
    const ctx = cv.getContext('2d');
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, w, h);
    return ctx;
  }

  function hoverClear(card) {
    if (!card) return;
    card._hoverLocal = null;
    card._hoverHit = null;
    if (card.el && card.el.hover) clearCanvas(card.el.hover);
    const tip = ensureTip();
    if (tip) tip.classList.remove('show');
  }

  /**
   * 鼠标移动：横向只在绘图区内认点（滑到左边的刻度栏、右边的留白上就收掉），
   * 纵向不设限 —— 鼠标压在曲线附近就行，不用像素级对准那条线。
   */
  function hoverMove(card, ev) {
    const el = card.el;
    const g = card._geometry;
    if (!el || !g || !card._markers || card._markers.length === 0) { hoverClear(card); return; }
    const r = el.canvas.getBoundingClientRect();
    if (r.width <= 0) return;
    const x = ev.clientX - r.left;
    const y = ev.clientY - r.top;
    if (x < g.plotX0 - 10 || x > g.plotX1 + 10) { hoverClear(card); return; }
    card._hoverLocal = { x, y };
    drawHover(card, x, y);
  }

  /** 只重画覆层（十字线 + 高亮点），不动主曲线的画布 —— 这样移动鼠标是跟手的 */
  function drawHover(card, x, y) {
    const el = card.el;
    const g = card._geometry;
    const ms = card._markers || [];
    const tip = ensureTip();
    if (!el || !el.hover || !g || ms.length === 0) {
      if (el && el.hover) clearCanvas(el.hover);
      if (tip) tip.classList.remove('show');
      return;
    }
    const ctx = clearCanvas(el.hover);
    if (!ctx) return;

    const css = getComputedStyle(document.body);
    const crossColor = css.getPropertyValue('--text-3').trim() || '#98a2ae';
    const ringColor = css.getPropertyValue('--panel').trim() || '#fff';

    // ① 基准曲线取「点数最多」的那条：它覆盖的时段最完整，十字线跟着它不会指到半空的区间
    let anchor = ms[0];
    for (const m of ms) if (m.n > anchor.n) anchor = m;
    const ai = nearestIdx(anchor.xs, x);
    const crossX = anchor.xs[ai];
    const anchorTs = anchor.ts ? anchor.ts[ai] : null;

    // ② 竖直十字线
    ctx.save();
    ctx.strokeStyle = hexToRgba(crossColor, .8);
    ctx.lineWidth = 1;
    ctx.setLineDash([3, 3]);
    const cx = Math.round(crossX) + 0.5;
    ctx.beginPath();
    ctx.moveTo(cx, g.plotY0);
    ctx.lineTo(cx, g.plotY1);
    ctx.stroke();
    ctx.restore();

    // ③ 每条曲线取「同一时刻」的那个点（没有时间戳就退回按横坐标就近取）
    const rows = [];
    for (const m of ms) {
      let i = -1;
      if (anchorTs !== null && m.ts) i = nearestIdx(m.ts, anchorTs);
      if (i < 0) i = nearestIdx(m.xs, crossX);
      if (i < 0) continue;
      const px = m.xs[i];
      const py = m.ys[i];

      // 横线只在单条曲线时画：多条曲线各自量程不同，一条横线会指向错误的刻度
      if (ms.length === 1) {
        ctx.save();
        ctx.strokeStyle = hexToRgba(m.color, .5);
        ctx.lineWidth = 1;
        ctx.setLineDash([3, 3]);
        const hy = Math.round(py) + 0.5;
        ctx.beginPath();
        ctx.moveTo(g.plotX0, hy);
        ctx.lineTo(g.plotX1, hy);
        ctx.stroke();
        ctx.restore();
      }

      ctx.beginPath();
      ctx.arc(px, py, 4, 0, Math.PI * 2);
      ctx.fillStyle = m.color;
      ctx.fill();
      ctx.lineWidth = 1.6;
      ctx.strokeStyle = ringColor;
      ctx.stroke();

      rows.push({ m, i });
    }
    if (rows.length === 0) { tip && tip.classList.remove('show'); return; }

    // ④ 提示框：X 是时间，Y 是各条曲线在这一刻的读数
    const xText = anchorTs !== null ? fmtTipTime(anchorTs) : `第 ${ai + 1} 点`;
    const valOf = (m, i) => {
      const raw = m.data[i];
      const txt = m.bool ? (raw ? 'ON' : 'OFF') : fmtNum(raw);
      const unit = m.unit ? `<span class="tip-unit">${esc(m.unit)}</span>` : '';
      return { txt, unit };
    };

    if (!tip) return;
    // 小卡片只有 44px 高，提示框比绘图区还高 —— 用它压扁、限行，别把曲线整段盖住
    const compact = el.canvas.clientHeight < 90;
    tip.classList.toggle('tip-compact', compact);
    const maxRows = compact ? 4 : 8;

    if (rows.length === 1) {
      // 单条曲线：一行就够（时间 + 值），不必摆成两行的小卡片
      const { txt, unit } = valOf(rows[0].m, rows[0].i);
      tip.innerHTML = `<div class="tip-solo"><i class="tip-dot" style="background:${rows[0].m.color}"></i>`
        + `<span class="tip-x">${esc(xText)}</span><span class="tip-val">${esc(txt)}${unit}</span></div>`;
    } else {
      const shown = rows.slice(0, maxRows).map(({ m, i }) => {
        const { txt, unit } = valOf(m, i);
        return `<div class="tip-row"><i class="tip-dot" style="background:${m.color}"></i>`
          + `<span class="tip-name">${esc(m.label)}</span>`
          + `<span class="tip-val">${esc(txt)}${unit}</span></div>`;
      }).join('');
      const more = rows.length > maxRows
        ? `<div class="tip-more">…还有 ${rows.length - maxRows} 条</div>` : '';
      tip.innerHTML = `<div class="tip-x">${esc(xText)}</div>${shown}${more}`;
    }

    // 这次到底取到了哪几个点 —— 控制台里能直接看，自检脚本也读它，
    // 免得去猜"提示框里的数字是不是真实采样值、各条曲线是不是同一时刻"。
    card._hoverHit = {
      anchorKey: anchor.key,
      x: crossX,
      ts: anchorTs,
      rows: rows.map(({ m, i }) => ({
        key: m.key,
        label: m.label,
        i,
        v: m.data[i],                                  // 直接引用原数组元素，不做插值
        ts: m.ts ? m.ts[i] : null,
      })),
    };

    tip.classList.add('show');

    // ⑤ 摆位置：贴着鼠标；右边放不下翻到左边；别压住左边的 Y 刻度栏（压住就没法对照刻度读数）
    const pad = 12;
    const rect = el.canvas.getBoundingClientRect();
    const tw = tip.offsetWidth;
    const th = tip.offsetHeight;
    const vw = window.innerWidth;
    const vh = window.innerHeight;
    const axisRight = rect.left + g.plotX0;

    let left = rect.left + crossX + pad;
    if (left + tw > vw - 6) left = rect.left + crossX - pad - tw;
    if (left < axisRight && left + tw > axisRight) {
      const right = rect.left + crossX + pad;
      left = (right + tw <= vw - 6) ? right : axisRight;
    }
    if (left < 6) left = 6;
    if (left + tw > vw - 6) left = vw - 6 - tw;

    // 纵向：大图上放到鼠标上方（放不下翻到下方），小图上与鼠标同高居中 —— 这样不会被
    // "上方放不下就翻到下面"顶到卡片头部去盖住主题名和当前值。
    let top = compact ? (rect.top + y - th / 2) : (rect.top + y - th - pad);
    if (top < 6) top = rect.top + y + pad + 12;
    if (top + th > vh - 6) top = vh - 6 - th;
    if (top < 6) top = 6;
    tip.style.left = `${Math.round(left)}px`;
    tip.style.top = `${Math.round(top)}px`;
  }

  // ============================================================
  //  消息流水
  // ============================================================
  function renderLogs(force) {
    if (S.logPaused && !force) return;
    const body = $('logBody');
    const wrap = document.querySelector('.log-wrap');
    const maxRows = S.ui.maxLogRows || 300;
    const filter = S.logFilter.trim().toLowerCase();

    const source = filter
      ? S.logs.filter((r) => r.topic.toLowerCase().includes(filter))
      : S.logs;
    const rows = source.slice(-maxRows);

    // 是否已在底部：是则跟随滚动，否则不打扰
    const atBottom = wrap.scrollTop + wrap.clientHeight >= wrap.scrollHeight - 24;

    const frag = document.createDocumentFragment();
    for (const r of rows) {
      const tr = document.createElement('tr');
      const isAlarm = r.json && typeof r.json === 'object' && (r.json.alarm === 1 || r.json.alarm === true || r.json.level === 'major');
      if (isAlarm) tr.className = 'is-alarm';
      tr.innerHTML = `
        <td class="c-time">${fmtTime(r.ts, true)}</td>
        <td class="c-topic"></td>
        <td class="c-payload"></td>
        <td class="c-num">${r.qos}</td>
        <td class="c-num">${r.size}</td>
      `;
      tr.querySelector('.c-topic').textContent = r.topic;
      tr.querySelector('.c-payload').textContent = r.payload;
      frag.appendChild(tr);
    }
    body.innerHTML = '';
    body.appendChild(frag);

    $('logCount').textContent = `${source.length} 条${filter ? '（已过滤）' : ''}`;
    if (atBottom) wrap.scrollTop = wrap.scrollHeight;
  }

  function exportCsv() {
    if (S.logs.length === 0) { toast('还没有消息可以导出', 'warn'); return; }
    const esc = (s) => `"${String(s).replace(/"/g, '""')}"`;
    const lines = ['时间,主题,载荷,QoS,字节,retain'];
    for (const r of S.logs) {
      lines.push([
        new Date(r.ts).toISOString(), esc(r.topic), esc(r.payload), r.qos, r.size, r.retain ? 1 : 0,
      ].join(','));
    }
    // BOM 让 Excel 正确识别 UTF-8
    const blob = new Blob(['\ufeff' + lines.join('\r\n')], { type: 'text/csv;charset=utf-8' });
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = `mqtt-log-${new Date().toISOString().slice(0, 19).replace(/[:T]/g, '-')}.csv`;
    a.click();
    setTimeout(() => URL.revokeObjectURL(a.href), 1000);
    toast(`已导出 ${S.logs.length} 条消息`);
  }

  // ============================================================
  //  主题列表
  // ============================================================
  function applyTopicList(list) {
    for (const t of list) S.topics.set(t.topic, t);
    renderTopics();
  }

  function renderTopics(skipFooter) {
    const box = $('topicList');
    const filter = S.topicFilter.trim().toLowerCase();
    const all = [...S.topics.values()].sort((a, b) => b.lastTs - a.lastTs);
    const list = filter ? all.filter((t) => t.topic.toLowerCase().includes(filter)) : all;

    $('topicCount').textContent = all.length;

    if (all.length === 0) {
      box.innerHTML = '<p class="empty">还没有收到任何消息</p>';
      return;
    }
    if (list.length === 0) {
      box.innerHTML = '<p class="empty">没有匹配的主题</p>';
      return;
    }

    const frag = document.createDocumentFragment();
    for (const t of list) {
      const onBoard = S.cardMap.has(t.topic);            // 有它自己的一张卡
      const host = groupCardForPrefix(t.topic);          // 前缀上认领它的分组卡（含已删）
      const inGroup = !onBoard && host && !isRemoved(host, t.topic); // 正躺在分组卡上
      const offGroup = !onBoard && host && isRemoved(host, t.topic); // 从分组卡上被删掉了
      const div = document.createElement('div');
      div.className = 'topic-item' + (onBoard ? ' on-board' : '')
        + (inGroup ? ' in-group' : '') + (offGroup ? ' off-group' : '');
      const btnTitle = onBoard ? '已在看板'
        : inGroup ? `已在分组卡「${groupRoot(host.topic)}/*」里 · 点一下把它从卡片上撤下来`
        : offGroup ? `加回分组卡「${groupRoot(host.topic)}/*」`
        : '加入看板';
      div.innerHTML = `
        <span class="t-dot"></span>
        <div class="t-main">
          <div class="t-name"></div>
          <div class="t-val"></div>
        </div>
        <button class="t-add" title="${btnTitle}">${onBoard || inGroup ? '✓' : '+'}</button>
      `;
      div.querySelector('.t-name').textContent = t.topic;
      const valText = String(t.lastPayload ?? '').slice(0, 60);
      div.querySelector('.t-val').textContent =
        `${valText}   ·  ${t.count.toLocaleString('en-US')} 条  ·  ${fmtTime(t.lastTs)}`;
      div.querySelector('.t-add').addEventListener('click', (e) => {
        e.stopPropagation();
        if (S.cardMap.has(t.topic)) return;      // 自己已有卡：不动它（卡上有 ✕）
        // 已被分组卡收着 —— 再点一下就是"取消上板"：把它从那张卡上撤下来
        const g = groupCardFor(t.topic);
        if (g) {
          removeTopicFromCard(g, t.topic);
          toast(`已取消上板：${t.topic}（不再显示在卡片上）`);
          return;
        }
        // 被某张分组卡删过：点 + 直接加回那张卡，别再冒一张单主题卡
        const back = groupCardForPrefix(t.topic);
        if (back) {
          restoreTopicToCard(back, t.topic);
          toast(`已加回分组卡「${groupRoot(back.topic)}/*」：${t.topic}`);
          return;
        }
        createCard({ topic: t.topic, name: shortName(t.topic), unit: '' });
        renderTopics();
        toast(`已加入看板：${t.topic}`);
      });
      frag.appendChild(div);
    }
    box.innerHTML = '';
    box.appendChild(frag);
    if (!skipFooter) updateTopStats();
  }

  /** 用主题末段作为默认卡片名，太长则保留后半段 */
  function shortName(topic) {
    const parts = topic.split('/').filter(Boolean);
    if (parts.length <= 3) return topic;
    return parts.slice(-3).join('/');
  }

  function renderSubs() {
    const box = $('subList');
    $('subCount').textContent = S.subs.length;

    // 一条订阅都没有时给个一键恢复的入口，避免用户取消完就"数据不动"找不到原因
    if (S.subs.length === 0) {
      box.innerHTML = '';
      const tip = document.createElement('div');
      tip.className = 'warn-line';
      tip.textContent = '尚未订阅任何主题，此刻收不到数据';
      const btn = document.createElement('button');
      btn.className = 'btn btn-ghost btn-sm';
      btn.textContent = '订阅全部（#）';
      btn.addEventListener('click', async () => {
        try {
          S.subs = await api('/api/subscribe', { topic: '#', qos: 0 });
          renderSubs();
          updateTopStats();
          toast('已订阅 #，开始接收全部主题');
        } catch (err) { toast(err.message, 'err'); }
      });
      box.appendChild(tip);
      box.appendChild(btn);
      return;
    }

    const frag = document.createDocumentFragment();
    for (const s of S.subs) {
      const chip = document.createElement('span');
      chip.className = 'chip';
      chip.innerHTML = '<span></span><span class="qos"></span><button title="取消订阅">×</button>';
      chip.querySelector('span').textContent = s.topic;
      chip.querySelector('.qos').textContent = 'Q' + (s.qos ?? 0);
      chip.querySelector('button').addEventListener('click', async () => {
        if (S.subs.length === 1
          && !confirm(`「${s.topic}」是最后一个订阅，取消后平台将收不到任何数据。\n确定取消吗？`)) return;
        try {
          S.subs = await api('/api/unsubscribe', { topic: s.topic });
          renderSubs();
          updateTopStats();
          toast(`已取消订阅 ${s.topic}`, S.subs.length === 0 ? 'warn' : undefined);
        } catch (err) { toast(err.message, 'err'); }
      });
      frag.appendChild(chip);
    }
    box.innerHTML = '';
    box.appendChild(frag);
  }

  // ============================================================
  //  历史数据（落盘）
  // ============================================================
  function applyHistory(st) {
    if (!st) return;
    S.history = st;
    const badge = $('histBadge');
    const meta = $('histMeta');
    const pathBox = $('fHistPath');

    if (!st.available) {
      badge.textContent = '未落盘';
      badge.style.background = 'rgba(230,162,60,.16)';
      badge.style.color = 'var(--warn)';
      meta.textContent = `历史只在内存里，重启平台就没了。原因：${st.reason || '未启用'}（要落盘需 Node 22.5+）`;
      return;
    }

    badge.textContent = fmtCount(st.rows) + ' 条';
    badge.style.background = 'rgba(103,194,58,.16)';
    badge.style.color = 'var(--ok)';

    if (document.activeElement !== $('fKeepDays')) $('fKeepDays').value = st.keepDays;
    pathBox.value = st.dbPath;
    pathBox.title = st.dbPath;   // 输入框宽度有限，悬停时给出完整路径

    const parts = [`占用 ${fmtBytes(st.bytes)}`, `保留 ${st.keepDays} 天`];
    if (st.oldest) {
      const days = ((Date.now() - st.oldest) / 86400000);
      parts.push(`最早 ${fmtTime(st.oldest, true)}（${days < 1 ? '今天' : days.toFixed(1) + ' 天前'}）`);
    } else {
      parts.push('还没有数据');
    }
    if (st.pendingRows) parts.push(`待写 ${st.pendingRows} 条`);
    if (st.lastError) parts.push(`⚠ ${st.lastError}`);
    meta.textContent = parts.join(' · ') + '　（重启平台不丢，曲线按「X 轴时间范围」从库里回填）';
  }

  async function refreshHistory() {
    try {
      applyHistory(await api('/api/history/stats'));
    } catch (err) {
      console.warn('读取历史库统计失败', err.message);
    }
  }

  function fmtCount(n) {
    if (!Number.isFinite(n)) return '—';
    if (n >= 10000) return (n / 10000).toFixed(n >= 100000 ? 0 : 1) + ' 万';
    return n.toLocaleString('en-US');
  }

  function fmtBytes(b) {
    if (!b) return '0 B';
    if (b >= 1073741824) return (b / 1073741824).toFixed(2) + ' GB';
    if (b >= 1048576) return (b / 1048576).toFixed(1) + ' MB';
    if (b >= 1024) return (b / 1024).toFixed(0) + ' KB';
    return b + ' B';
  }

  // ============================================================
  //  内置 Broker 台账
  // ============================================================
  function applyBroker(raw) {
    if (!raw) return;
    // WebSocket 帧会多带一个 type 字段，解构时一并剥掉（API 返回不带 type 也适用）
    const { type: _t, ...snap } = raw;
    S.broker = snap;

    const badge = $('brokerBadge');
    badge.textContent = snap.running ? `运行中 :${snap.port}` : '未运行';
    badge.style.background = snap.running ? 'rgba(103,194,58,.16)' : '';
    badge.style.color = snap.running ? 'var(--ok)' : '';

    // 回填端口/账号（正在输入时不打断）
    if (snap.running && document.activeElement !== $('fBrokerPort')) $('fBrokerPort').value = snap.port;
    if (snap.username && document.activeElement !== $('fBrokerUser')) $('fBrokerUser').value = snap.username;

    // 设备接入地址
    const box = $('brokerAddrBox');
    const list = $('addrList');
    if (snap.running && Array.isArray(snap.addresses) && snap.addresses.length) {
      box.style.display = '';
      list.innerHTML = '';
      for (const addr of snap.addresses) {
        const el = document.createElement('span');
        el.className = 'addr-line';
        el.textContent = 'tcp://' + addr;
        el.title = '点击复制';
        el.addEventListener('click', () => {
          if (navigator.clipboard) {
            navigator.clipboard.writeText(addr).then(
              () => toast('已复制 ' + addr),
              () => toast('复制失败，请手动选择', 'warn'),
            );
          }
        });
        list.appendChild(el);
      }
    } else {
      box.style.display = 'none';
    }

    // 模拟器开关
    const sim = $('fSim');
    sim.checked = !!(snap.sim && snap.sim.running);
    sim.disabled = !snap.running;

    // 设备台账
    $('clientCount').textContent = snap.clientCount || 0;
    renderClients(snap.clients || [], !!snap.running);

    // 按钮状态
    $('btnBrokerStart').disabled = !!snap.running;
    $('btnBrokerStop').disabled = !snap.running;
    $('btnBrokerSelf').disabled = !snap.running;
  }

  function renderClients(clients, running) {
    const box = $('clientList');
    if (!running) {
      box.innerHTML = '<p class="empty">Broker 未运行</p>';
      return;
    }
    if (clients.length === 0) {
      box.innerHTML = '<p class="empty">暂无设备接入</p>';
      return;
    }

    const frag = document.createDocumentFragment();
    for (const c of clients) {
      const div = document.createElement('div');
      div.className = 'client-item';
      div.innerHTML = `
        <div class="c-top">
          <span class="c-dot"></span>
          <span class="c-id"></span>
          <span class="c-ip"></span>
          <button class="c-kick" title="断开该设备">断开</button>
        </div>
        <div class="c-sub"></div>
      `;
      div.querySelector('.c-id').textContent = c.id;
      div.querySelector('.c-ip').textContent = c.ip || '';

      const subs = (c.subscriptions || []).map((s) => s.topic).join(' , ');
      const line1 = document.createElement('div');
      line1.textContent = '订阅 ' + (subs || '无');
      const line2 = document.createElement('div');
      line2.textContent = `发布 ${c.msgIn.toLocaleString('en-US')} 条 / ${fmtBytes(c.bytesIn)}　在线 ${fmtUptime(c.uptimeMs)}`
        + (c.lastTopic ? `　最近 ${c.lastTopic}` : '')
        + (c.username ? `　账号 ${c.username}` : '');
      const subBox = div.querySelector('.c-sub');
      subBox.appendChild(line1);
      subBox.appendChild(line2);

      div.querySelector('.c-kick').addEventListener('click', async () => {
        if (!confirm(`断开设备 ${c.id}？`)) return;
        try {
          applyBroker(await api('/api/broker/disconnect', { clientId: c.id }));
          toast(`已断开 ${c.id}`);
        } catch (err) { toast(err.message, 'err'); }
      });
      frag.appendChild(div);
    }
    box.innerHTML = '';
    box.appendChild(frag);
  }

  // ============================================================
  //  表单
  // ============================================================
  function readForm() {
    return {
      protocol: $('fProtocol').value,
      host: $('fHost').value.trim(),
      port: Number($('fPort').value) || defaultPort($('fProtocol').value),
      clientId: $('fClientId').value.trim(),
      username: $('fUser').value.trim(),
      password: $('fPass').value,
      keepalive: Number($('fKeepalive').value) || 60,
      cleanSession: true,
      rejectUnauthorized: !$('fSelfSigned').checked,
    };
  }

  function defaultPort(protocol) {
    return { mqtt: 1883, mqtts: 8883, ws: 8083, wss: 8084 }[protocol] || 1883;
  }

  function writeForm(cfg) {
    if (!cfg) return;
    $('fProtocol').value = cfg.protocol || 'mqtt';
    $('fHost').value = cfg.host || '';
    $('fPort').value = cfg.port || defaultPort(cfg.protocol);
    $('fClientId').value = cfg.clientId || '';
    $('fUser').value = cfg.username || '';
    $('fPass').value = cfg.password || '';
    $('fKeepalive').value = cfg.keepalive || 60;
    $('fSelfSigned').checked = cfg.rejectUnauthorized === false;
  }

  async function doConnect() {
    const conn = readForm();
    if (!conn.host) { toast('请填写 broker 地址', 'warn'); $('fHost').focus(); return; }

    // 订阅列表沿用已有的；一条都没有时默认订阅全部
    let subs = S.subs.slice();
    if (subs.length === 0) subs = [{ topic: '#', qos: 0 }];

    try {
      $('connHint').textContent = '正在连接…';
      const snap = await api('/api/connect', { connection: conn, subscriptions: subs });
      applyStatus(snap);
      renderSubs();
      toast(`正在连接 ${conn.host}:${conn.port} …`);
    } catch (err) {
      toast(err.message, 'err');
    }
  }

  async function doDisconnect() {
    try {
      const snap = await api('/api/disconnect', {});
      applyStatus(snap);
      toast('已断开连接');
    } catch (err) { toast(err.message, 'err'); }
  }

  // ============================================================
  //  弹窗
  // ============================================================
  function openCardModal(card) {
    S.editingCard = card;
    $('modalTitle').textContent = card.name || card.topic;
    $('mTopic').value = card.topic;
    $('mGroup').checked = !!card.group;
    $('mTopic').placeholder = card.group ? 'plc（主题前缀，不写点位）' : 'plc/DB1.DBW290';
    $('mName').value = card.name;
    $('mUnit').value = card.unit || '';
    $('mField').value = '';
    $('mLow').value = card.low ?? '';
    $('mHigh').value = card.high ?? '';
    $('mPoints').value = card.points ? String(card.points) : '';
    $('mWindow').value = card.windowMin === null || card.windowMin === undefined ? '' : String(card.windowMin);
    $('mGroupBy').value = card.groupBy || '';
    $('mGroupBy').placeholder = card.seenIdField ? `自动（当前用 ${card.seenIdField}）` : '自动（tag / address）';
    renderFieldPicker(card);
    updateWindowNote(card);
    $('cardModal').classList.add('show');
  }

  /**
   * 弹窗里那行「当前缓冲能覆盖多久」的提示。
   * 用户会问“我选了最近 1 小时，怎么只看到 2 分钟” —— 原因有两层：
   *   1) 内存缓冲只有那么深（曲线画不出更多的）→ 会从历史库自动补；
   *   2) 历史库里也确实没有更早的数据（设备就没发过）→ 补也补不出来。
   * 所以这里把两边的确切数字都摊开，让用户一眼分清是"平台的问题"还是"设备没发"。
   */
  function updateWindowNote(card) {
    const box = $('mWindowNote');
    const cov = seriesCoverage(card);
    const win = effectiveWindowMin({
      windowMin: normWindow($('mWindow').value),
      // 弹窗里还没保存时，用当前输入值预览
    });

    if (!cov || cov.n < 2) {
      const base = '还没有足够的数据来判断缓冲覆盖 —— 让设备先发几条再看。'
        + (win > 0 ? `当前窗口：最近 ${windowLabel(win)}。` : '');
      box._base = base;
      box.textContent = base;
      return;
    }

    const parts = [
      `当前缓冲 ${cov.n} 点 · 覆盖 ${fmtDuration(cov.span)} · 平均 ${fmtDuration(cov.interval)}/点`,
      `窗口：${win > 0 ? `最近 ${windowLabel(win)}` : '全部'}`,
    ];

    if (win > 0 && win * 60000 > cov.span * 1.05) {
      parts.push(`⚠️ 内存缓冲只覆盖 ${fmtDuration(cov.span)}，比窗口短 —— 更早那段会自动从历史库补回来`);
    } else if (win > 0) {
      parts.push(`窗口内约 ${Math.max(2, Math.round((win * 60000) / Math.max(cov.interval, 1)) + 1)} 个点`);
    } else {
      parts.push(`「全部」= 内存缓冲里的这 ${cov.n} 点`);
    }

    box._base = parts.join('　|　');
    box.textContent = box._base;

    // 再去问一下落盘库：库里到底存了多少条 —— 用户真正想知道的往往是这个数
    askSpan(card, win).then((sp) => {
      if (!sp || S.editingCard !== card || !box._base) return;
      const extra = [];
      if (sp.db) {
        card.dbCount = sp.db.count;
        extra.push(`历史库共 ${sp.db.count} 条（跨 ${fmtDuration(sp.db.lastTs - sp.db.firstTs)}，最新 ${fmtTime(sp.db.lastTs)}）`);
        if (win > 0 && sp.dbInWindow !== null && sp.dbInWindow !== undefined) {
          extra.push(`窗口内 ${sp.dbInWindow} 条`);
        }
      } else {
        extra.push('历史库暂无该主题数据（曲线只能画内存缓冲里的点）');
      }
      box.textContent = `${box._base}　|　${extra.join('　|　')}`;
    });
  }

  /**
   * 这张卡片的报文里有哪些字段可以勾？
   * 来源：最近一条报文的数值/布尔字段 + 已经建好的曲线 + 已勾选的字段
   *（后两者保证「暂时没数据」或「字段这轮没出现」时也能看到、也能取消勾选）
   */
  /**
   * 弹窗勾选列表的数据源：这个主题出现过的「测点」+（可能有的）「数值字段」。
   *
   * 测点来自 card.seenItems —— 是"见过就记"，所以超过 MAX_SERIES 没画出来的测点
   * 也照样列在这里，用户勾一下就能把它画出来。
   */
  function collectFields(card) {
    const items = [];
    const seen = new Set();
    if (card.seenItems) {
      for (const it of card.seenItems.values()) {
        if (seen.has(it.name)) continue;
        seen.add(it.name);
        items.push({ name: it.name, num: it.num, text: it.text, bool: it.bool, count: it.count });
      }
    }
    // 刚建的卡片可能还没收到报文，但 rawJson 已经有了；把当前这条的测点也补进去
    if (card.seenIdField) {
      const cur = getPath(card.rawJson, card.seenIdField);
      const nm = cur === null || cur === undefined || typeof cur === 'object' ? '' : String(cur).trim();
      if (nm && !seen.has(nm)) {
        seen.add(nm);
        items.push({ name: nm, num: card.num, text: card.text, bool: false, count: 0 });
      }
    }
    items.sort((a, b) => String(a.name).localeCompare(String(b.name), 'zh-Hans-CN'));

    const map = new Map();
    let textCount = 0;
    const put = (path, num, text, bool) => {
      if (!path || map.has(path)) return;
      map.set(path, { path, num, text, bool });
    };
    if (card.rawJson && typeof card.rawJson === 'object' && !Array.isArray(card.rawJson)) {
      const found = extractNumericFields(card.rawJson);
      textCount = found.textCount || 0;
      for (const f of found) if (f.path !== card.seenIdField) put(f.path, f.num, f.text, f.bool);
    }
    // __v（整条载荷）和 __item__:（测点）是内部键，不是字段路径，不给勾
    for (const s of card.series) if (s.key !== '__v' && !isItemKey(s.key)) put(s.key, s.num, s.text, s.bool);
    for (const f of card.fields || []) put(f, null, '—', false);
    return { items, fields: [...map.values()], textCount };
  }

  /**
   * 弹窗里现在是不是「分组」形态？
   * 弹窗里的复选框是唯一真相（用户可能刚勾上还没保存），没在编辑这张卡时按卡片自己的设置。
   */
  function pickerGroupMode(card) {
    const box = $('mGroup');
    if (box && S.editingCard === card) return box.checked;
    return !!(card && card.group);
  }

  /** 分组卡的候选字段：这个主题见到过的测点 + 数值字段（用户手选的也留着，别因为没数据就消失） */
  function topicCandidates(card, topic) {
    const e = card.seenByTopic && card.seenByTopic.get(topic);
    const out = [];
    const autoKey = (e && e.autoKey) || '';
    const autoLabel = autoKey
      ? (isItemKey(autoKey) ? itemNameOf(autoKey) : (autoKey === '__v' ? tailName(topic) : autoKey))
      : '';
    out.push({ value: '', text: autoLabel ? `自动（${autoLabel}）` : '自动' });
    if (e) {
      for (const it of e.items.values()) {
        out.push({ value: ITEM_PREFIX + it.name, text: `${it.name}　${it.text}` });
      }
      for (const f of e.fields.values()) {
        out.push({ value: f.path, text: `${f.path}　${f.text}` });
      }
    }
    const cur = (card.picks || {})[topic];
    if (cur && !out.some((o) => o.value === cur)) out.push({ value: cur, text: `${cur}（暂无数据）` });
    return out;
  }

  /**
   * 分组卡片的「展示曲线」面板：前缀下每个主题一行 ——
   * 勾选框 = 这个主题的曲线要不要画（一个都不勾 = 自动按顺序画到上限），
   * 下拉 = 这条曲线画该主题里的哪个 tag / 字段。
   */
  function renderGroupPicker(card) {
    const box = $('mFieldPick');
    const members = groupMembers(card);
    const pickedTopics = card.picked || [];
    box.innerHTML = '';
    box._itemCount = 0;
    box._fieldCount = 0;
    box._topicCount = members.length;

    const note = $('mPickNote');
    if (note) {
      note.innerHTML = `<b>勾上要画曲线的主题</b>（不勾的也留在卡片上显示实时值，只是不画线）；`
        + '右边的下拉选「这条曲线画该主题里的哪个 tag / 字段」。'
        + `前缀 <b>${esc(groupRoot($('mTopic').value.trim() || card.topic))}/</b> 下的主题会自动进来；`
        + '不想要的点右边的 ✕ 从卡片删除。';
    }

    if (!members.length) {
      const p = document.createElement('div');
      p.className = 'fp-empty';
      p.textContent = '这个前缀下还没收到任何主题。让设备先发一条；'
        + '如果设备是刚接上的，也可以先保存，等第一条报文到了这里就会列出来。';
      box.appendChild(p);
      updateFieldHint();
      return;
    }

    for (const t of members) {
      const e = card.seenByTopic && card.seenByTopic.get(t);
      const head = document.createElement('div');
      head.className = 'fp-group is-topic';
      const cb = document.createElement('input');
      cb.type = 'checkbox';
      cb.className = 'fp-topic';   // saveCardModal / updateFieldHint 靠这个类把它和"字段勾选"分开
      cb.dataset.topic = t;
      cb.checked = pickedTopics.indexOf(t) >= 0;
      cb.title = '勾上 = 画这个主题的曲线（不勾也会在卡片上显示它的实时值）';
      const nm = document.createElement('span');
      nm.className = 'fp-topic-name';
      nm.textContent = t;
      nm.title = t;
      const meta = e
        ? `${e.idField ? `按 ${e.idField} 区分 · ` : ''}${e.count} 条 · ${fmtTime(e.lastTs)}`
        : '还没收到数据';
      const nt = document.createElement('span');
      nt.className = 'fp-group-note';
      nt.textContent = meta;
      const del = document.createElement('button');
      del.className = 'fp-del';
      del.type = 'button';
      del.textContent = '✕';
      del.title = '把这个主题从卡片上撤下来（值、曲线一起走；侧栏里点 + 可以加回来）';
      del.addEventListener('click', () => removeTopicFromCard(card, t));
      cb.addEventListener('change', () => {
        head.classList.toggle('is-off', !cb.checked);
        const on = box.querySelectorAll('input.fp-topic:checked').length;
        if (on > MAX_SERIES) {
          cb.checked = false;
          head.classList.toggle('is-off', true);
          toast(`一张卡片最多画 ${MAX_SERIES} 条曲线，先取消一个再勾`, 'warn');
          return;
        }
        updateFieldHint();
      });
      head.classList.toggle('is-off', !cb.checked && pickedTopics.length > 0);
      head.append(cb, nm, nt, del);
      box.appendChild(head);

      const row = document.createElement('div');
      row.className = 'fp-item is-on is-topic';
      const dot = document.createElement('i');
      dot.className = 'fp-dot';
      dot.style.background = colorForField(card, topicKey(t, desiredFieldKey(card, t) || '__v'), 0);
      const sel = document.createElement('select');
      sel.className = 'fp-select';
      sel.dataset.topic = t;
      for (const o of topicCandidates(card, t)) {
        const op = document.createElement('option');
        op.value = o.value;
        op.textContent = o.text;
        sel.appendChild(op);
      }
      sel.value = (card.picks || {})[t] || '';
      sel.addEventListener('change', () => { row.classList.toggle('is-on', true); updateFieldHint(); });
      row.append(dot, sel);
      box.appendChild(row);
    }

    // 已删除的主题单独列一栏：不在这里的话，"删了还能找回"就成了空话
    const removed = (card.removed || []).filter((t) => inGroupOf(card.topic, t)).sort();
    if (removed.length) {
      const h = document.createElement('div');
      h.className = 'fp-group fp-removed-head';
      h.textContent = `已从卡片删除（${removed.length}）`;
      box.appendChild(h);
      for (const t of removed) {
        const row = document.createElement('div');
        row.className = 'fp-item fp-removed';
        const nm = document.createElement('span');
        nm.className = 'fp-topic-name';
        nm.textContent = t;
        nm.title = t;
        const back = document.createElement('button');
        back.className = 'link-btn fp-restore';
        back.type = 'button';
        back.textContent = '恢复';
        back.dataset.topic = t;
        back.addEventListener('click', () => restoreTopicToCard(card, t));
        row.append(nm, back);
        box.appendChild(row);
      }
    }
    updateFieldHint();
  }

  /**
   * 把主题从这张分组卡上删掉：不再显示它的实时值与曲线，下一条报文也不会把它带回来。
   * 立即生效并落盘（删除动作不该还要再点一次"保存"——用户会以为没删掉），
   * 配置里记在 `card.removed`，刷新/重启后依然有效。
   */
  /**
   * 撤下卡片 = 这条主题的回填记录一起作废。
   * 不作废的话，它被加回来时 `backfillCard` 会认为"这张卡这个窗口已经补过了"而跳过，
   * 曲线一直空着，用户以为"加回来了却没恢复数据"。
   */
  function forgetBackfill(card, topic) {
    if (!S.backfilled || !card) return;
    const prefix = `${card.topic}|`;
    const suffix = `|${topic}`;
    for (const k of [...S.backfilled]) {
      if (k.startsWith(prefix) && k.endsWith(suffix)) S.backfilled.delete(k);
    }
  }

  function removeTopicFromCard(card, topic) {
    if (!card || !card.group) return;
    forgetBackfill(card, topic);
    card.removed = [...new Set([...(card.removed || []), topic])];
    card.picked = (card.picked || []).filter((t) => t !== topic);
    delete card.picks[topic];
    card.seenByTopic?.delete(topic);
    for (let i = card.series.length - 1; i >= 0; i -= 1) {
      if (topicKeyOf(card.series[i].key) === topic) {
        card.seriesMap.delete(card.series[i].key);
        card.series.splice(i, 1);
      }
    }
    card.main = pickMainSeries(card);
    const mv3 = mainValue(card);
    card.text = mv3.text;
    card.num = mv3.num;
    card.overflow = computeOverflow(card);
    updateCardDom(card);
    markDirty(card);
    saveCards();
    renderTopics();
    if (S.editingCard === card) renderGroupPicker(card);
    toast(`已取消上板：${topic}（侧栏里点 + 可以加回来）`);
  }

  /** 把删掉的主题放回卡片（曲线与历史会重新从库里补） */
  function restoreTopicToCard(card, topic) {
    if (!card || !card.group) return;
    card.removed = (card.removed || []).filter((t) => t !== topic);
    card.members = [...new Set([...(card.members || []), topic])];
    // 勾选模式下要顺手勾上，否则"恢复了却不显示"，看着像没恢复成功
    if ((card.picked || []).length && card.picked.indexOf(topic) < 0) {
      card.picked = [...card.picked, topic];
    }
    saveCards();
    renderTopics();
    if (S.editingCard === card) renderGroupPicker(card);
    void (async () => {
      syncGroupSeries(card, true);
      await backfillCard(card, 0).catch(() => 0);
      updateCardDom(card);
      markDirty(card);
    })();
    toast(`已把 ${topic} 放回卡片`);
  }

  /**
   * 弹窗里的勾选列表。三种形态：
   *   「测点」—— 同一个主题下不同 tag/address 的报文，勾哪个画哪个（主用法）
   *   「数值字段」—— 一条报文里含多个数值时，按字段路径勾
   *   「主题列表」—— 分组卡片：前缀下每个主题一行，下拉选这个主题画哪个 tag/字段
   * 前两种一个都不勾 = 自动。
   */
  function renderFieldPicker(card) {
    if (pickerGroupMode(card)) return renderGroupPicker(card);

    const box = $('mFieldPick');
    const note = $('mPickNote');
    if (note) {
      note.innerHTML = '一个都不勾 = 自动。「测点」是同一个主题下不同 <b>tag / address</b> 的报文'
        + '（比如小车状态、设定位置），勾哪个画哪个；「数值字段」是一条报文里含多个数值时按字段勾';
    }
    const { items, fields: list, textCount } = collectFields(card);
    card._textFields = textCount;
    box.innerHTML = '';

    const pickedItems = card.items || [];
    const pickedFields = card.fields || [];
    box._itemCount = items.length;      // updateFieldHint 要用，别再从文案里正则抠
    box._fieldCount = list.length;

    const makeRow = (value, name, text, color, opts) => {
      const on = opts.on;
      const row = document.createElement('label');
      row.className = 'fp-item' + (on ? ' is-on' : '') + (opts.item ? ' is-item' : '');
      const cb = document.createElement('input');
      cb.type = 'checkbox';
      cb.checked = on;
      cb.value = value;
      const dot = document.createElement('i');
      dot.className = 'fp-dot';
      dot.style.background = color;
      const nm = document.createElement('span');
      nm.className = 'fp-name';
      nm.textContent = name;
      nm.title = opts.title || name;
      row.append(cb, dot, nm);
      if (opts.tag) {
        const tg = document.createElement('span');
        tg.className = 'fp-tag';
        tg.textContent = opts.tag;
        row.append(tg);
      }
      const val = document.createElement('span');
      val.className = 'fp-val';
      val.textContent = text;
      row.append(val);
      cb.addEventListener('change', () => {
        row.classList.toggle('is-on', cb.checked);
        // 勾选上限：超出就不再允许勾，避免曲线糊成一团
        if (box.querySelectorAll('input:checked').length > MAX_SELECT) {
          cb.checked = false;
          row.classList.remove('is-on');
          toast(`一张卡片最多勾 ${MAX_SELECT} 条曲线`, 'warn');
          return;
        }
        updateFieldHint();
      });
      box.appendChild(row);
    };

    const group = (title, note) => {
      const h = document.createElement('div');
      h.className = 'fp-group';
      h.innerHTML = `${esc(title)}<span class="fp-group-note">${esc(note)}</span>`;
      box.appendChild(h);
    };

    if (items.length) {
      const idField = card.groupBy || card.seenIdField || 'tag';
      group(`测点（${items.length}）`, `按报文里的「${idField}」区分`);
      items.forEach((it, i) => {
        makeRow(ITEM_PREFIX + it.name, it.name, it.text,
          colorForField(card, ITEM_PREFIX + it.name, i),
          { on: pickedItems.includes(it.name), tag: it.bool ? '开关量' : '', title: it.name, item: true });
      });
    }

    if (list.length) {
      group(`数值字段（${list.length}）`, '一条报文里的多个数值');
      list.forEach((f, i) => {
        makeRow(f.path, f.path, f.text, colorForField(card, f.path, items.length + i),
          { on: pickedFields.includes(f.path), tag: f.bool ? '开关量' : '', title: f.path });
      });
    }

    updateFieldHint();
  }

  function updateFieldHint() {
    const box = $('mFieldPick');
    const card = S.editingCard;

    // 分组卡：一行一个主题（勾选框决定画不画，下拉决定画哪个字段）
    if (card && pickerGroupMode(card)) {
      const sels = [...box.querySelectorAll('select')];
      const boxes = [...box.querySelectorAll('input.fp-topic')];
      const on = boxes.filter((b) => b.checked).length;
      const picked = sels.filter((s) => s.value).length;
      const rm = (card.removed || []).length;
      const rmNote = rm ? ` · 已删除 ${rm} 个` : '';
      if (!boxes.length) {
        $('mFieldHint').textContent = rm
          ? `这个前缀下的主题都被删掉了（${rm} 个）—— 想找回来在下面点「恢复」`
          : '这个前缀下还没收到主题 —— 让设备先发一条，或确认前缀写对了';
      } else if (!on) {
        const draw = Math.min(boxes.length, MAX_SERIES);
        $('mFieldHint').textContent = `${boxes.length} 个主题在卡片上（都显示实时值） · 当前自动：画前 ${draw} 条曲线`
          + `${boxes.length > MAX_SERIES ? `（多的在图例上写「+${boxes.length - MAX_SERIES}」）` : ''}`
          + ` · 勾选可以只画其中几条${rmNote}`;
      } else {
        const rest = boxes.length - on;
        $('mFieldHint').textContent = `已勾 ${on} / ${boxes.length} 个主题画曲线`
          + (on > MAX_SERIES ? `（超过上限 ${MAX_SERIES} 条，只画前 ${MAX_SERIES} 条）` : '')
          // 一个不剩时别再写「其余 0 个」——那是句废话
          + (rest ? `，其余 ${rest} 个只显示实时值` : '（所有点位都画）')
          + `（都按自动取该主题的第一个值${picked ? `，其中 ${picked} 个指定了字段` : ''}）${rmNote}`;
      }
      return;
    }

    const total = box.querySelectorAll('input').length;
    const itemCount = box._itemCount || 0;
    const on = box.querySelectorAll('input:checked').length;
    const skipped = (card && card._textFields) || 0;
    const tail = skipped ? ` · 另有 ${skipped} 个文本字段画不了曲线` : '';
    let text;
    if (total === 0) {
      // 区分「载荷里没有可画的数值」（都是文本）和「还没收到数据」
      text = card && (card.series.length || card.seenItems?.size)
        ? `这条载荷里没有数值字段（${skipped ? skipped + ' 个字段都是文本，' : ''}画不了曲线）`
        : '这个主题还没收到数据，等第一条报文到了再看';
    } else if (on === 0) {
      text = itemCount
        ? `${itemCount} 个测点可选 · 当前自动：见到几个画几个（最多 ${MAX_SERIES} 条）${tail}`
        : `${total} 个字段可选 · 当前自动：全部都画${tail}`;
    } else {
      text = `已选 ${on} / ${total} 条曲线${itemCount ? `（其中 ${itemCount} 个测点可选）` : ''}${tail}`;
    }
    $('mFieldHint').textContent = text;
  }

  function closeCardModal() {
    $('cardModal').classList.remove('show');
    S.editingCard = null;
  }

  /**
   * 勾选变化后重算曲线集合：丢掉不该画的，保留该画的（颜色/历史都不丢）。
   *
   * 关键点：点了「全部取消」= 回到自动，必须**立刻**把已经见过的测点都恢复出来。
   * 只"什么都不做"是不行的 —— 现场设备 60~80 秒才上报一次，
   * 用户会看到卡片上少了两条曲线、要干等一分钟才回来，像是取消勾选把数据搞丢了。
   *
   * @param {boolean} fresh 自动模式下是否顺带建出空曲线（保存弹窗时 true）
   */
  function syncSeriesToFields(card, fresh) {
    if (card.group) return syncGroupSeries(card, fresh);
    const explicit = (card.fields || []).length > 0 || (card.items || []).length > 0;
    const keep = new Set();
    if (explicit) {
      for (const n of card.items || []) keep.add(ITEM_PREFIX + n);
      for (const f of card.fields || []) keep.add(f);
    } else {
      // 自动模式：见过的测点（上限 MAX_SERIES）
      for (const k of autoItemKeys(card)) keep.add(k);
      // 只有"字段模式"的报文才补数值字段。测点模式下 pickSamples 走的是测点那条路，
      // 这里再按字段加就会凭空多出一条永远没数据的 value 曲线。
      if (!usesItemMode(card) && card.rawJson
        && typeof card.rawJson === 'object' && !Array.isArray(card.rawJson)) {
        for (const f of extractNumericFields(card.rawJson)) {
          if (f.path === card.seenIdField) continue;
          if (keep.size >= MAX_SERIES) break;
          keep.add(f.path);
        }
      }
      // 纯数字 / 纯文本报文用的是内部键 __v，它既不是测点名也不是字段路径。
      // 不显式留着的话，上面的 keep 会是空的 → 保存一次弹窗就把曲线全删了
      // （裸数字主题的曲线会凭空消失，直到设备下一条报文才回来）。
      if (card.seriesMap.has('__v')) keep.add('__v');
    }

    for (let i = card.series.length - 1; i >= 0; i -= 1) {
      const s = card.series[i];
      if (!keep.has(s.key)) {
        card.series.splice(i, 1);
        card.seriesMap.delete(s.key);
      }
    }
    if (!fresh) return;

    // 该画但还没有曲线的：先建一条空的（让颜色和顺序立刻定下来）
    const make = (key, label) => {
      if (card.seriesMap.has(key) || !seriesAllowed(card, key)) return;
      const ser = { key, label, num: null, text: '—', data: [], ts: [], color: '', bool: false };
      ser.color = nextColor(card);
      card.seriesMap.set(key, ser);
      card.series.push(ser);
    };
    for (const k of keep) make(k, isItemKey(k) ? itemNameOf(k) : k);
  }

  function saveCardModal() {
    const card = S.editingCard;
    if (!card) return;
    const wantGroup = $('mGroup').checked;
    // 分组卡的主题存**前缀**：用户可能填 `plc/`、`plc/*`、`plc/#`，统一成 `plc`
    const newTopic = wantGroup ? groupRoot($('mTopic').value) : $('mTopic').value.trim();
    if (!newTopic) { toast('主题不能为空', 'warn'); return; }

    // 换了主题、或切换了分组形态 → 迁移索引，并把旧范围的曲线全部丢掉
    if (newTopic !== card.topic || wantGroup !== !!card.group) {
      if (newTopic !== card.topic && S.cardMap.has(newTopic)) { toast('该主题已在看板中', 'warn'); return; }
      S.cardMap.delete(card.topic);
      card.topic = newTopic;
      card.group = wantGroup;
      S.cardMap.set(newTopic, card);
      card.el.root.dataset.topic = newTopic;
      card.series = [];
      card.seriesMap = new Map();
      // 换了范围，"见过哪些测点"就全作废了 —— 不清的话勾选列表里会留着旧主题的测点
      card.seenItems = new Map();
      card.seenByTopic = new Map();
      card.seenIdField = '';
      card.picks = {};
      card.picked = [];
      card.removed = [];      // 换了前缀/形态，"删过哪几个主题"也跟着作废
      card.members = [];
      card._membersAt = 0;
      card.main = null;
      card.text = '—';
      card.num = null;
      card.lastTs = 0;
    }

    card.name = $('mName').value.trim() || shortName(card.topic);
    card.unit = $('mUnit').value.trim();

    // 勾的测点 / 字段 + 手填的路径（手填的用来补报文里没出现过的字段）
    // ⚠️ 必须排除 `input.fp-topic`（分组卡的"哪个主题"勾选框）——
    //    它的 value 是主题名，混进来会被当成字段路径存进 config。
    const box = $('mFieldPick');
    const checked = [...box.querySelectorAll('input[type="checkbox"]:checked:not(.fp-topic)')]
      .map((n) => n.value);
    const manual = $('mField').value.split(',').map((s) => s.trim()).filter(Boolean);
    // 分组卡：每个主题一行下拉，值就是"这条曲线画哪个字段"；勾选框决定这个主题画不画
    const picks = {};
    if (card.group) {
      for (const sel of box.querySelectorAll('select[data-topic]')) {
        if (sel.value) picks[sel.dataset.topic] = sel.value;
      }
    }
    card.picks = picks;
    card.picked = card.group
      ? [...box.querySelectorAll('input.fp-topic:checked')].map((n) => n.dataset.topic)
      : [];
    card.items = checked.filter(isItemKey).map(itemNameOf).slice(0, MAX_SELECT);
    card.fields = normFields([...checked.filter((v) => !isItemKey(v)), ...manual]);
    if (card.fields.length > MAX_SELECT) card.fields = card.fields.slice(0, MAX_SELECT);
    card.groupBy = $('mGroupBy').value.trim();

    card.low = $('mLow').value === '' ? null : Number($('mLow').value);
    card.high = $('mHigh').value === '' ? null : Number($('mHigh').value);
    // 0 / 空 = 不限。这里只按「点数上限」裁，**不再按窗口裁** ——
    // 窗口只是个显示过滤器，把数据真丢掉的话，用户把窗口调回来就发现"历史没了"。
    card.points = normPoints($('mPoints').value);
    card.windowMin = normWindow($('mWindow').value);
    trimSeries(card);
    // 换了窗口，之前「补过 / 没补到」的记录作废，该重新问一次库
    S.backfilled = new Set();

    // 勾选变化会改变曲线集合与主曲线，卡片大字跟着走
    syncSeriesToFields(card, true);
    card.main = pickMainSeries(card);
    if (card.main) { const mv4 = mainValue(card); card.text = mv4.text; card.num = mv4.num; }

    updateCardDom(card);
    markDirty(card);
    saveCards();
    renderTopics();
    closeCardModal();
    if (card.group) {
      const n = countTopicSeries(card);
      toast(n ? `分组卡已保存：${groupRoot(card.topic)}/ 下 ${n} 个点位各一条曲线`
        : `分组卡已保存，等 ${groupRoot(card.topic)}/ 下的主题上报数据`);
    } else {
      const pickedN = (card.items || []).length + (card.fields || []).length;
      toast(pickedN ? `已固定显示 ${pickedN} 条曲线` : '已保存（自动画这个主题上报的所有测点）');
    }

    // 内存缓冲铺不满新窗口、或者勾出了没数据的曲线时，去落盘库把缺的那段补回来
    // （异步，不挡界面）
    if (card.group) {
      // 分组卡要先知道"这个前缀下有哪些主题"，才能建曲线、才知道该拉谁的历史
      void (async () => {
        await groupTopicsFor(card).catch(() => []);
        syncGroupSeries(card, true);
        card.main = pickMainSeries(card);
        updateCardDom(card);
        markDirty(card);
        await ensureCoverage(card, { fillEmpty: true }).catch(() => 0);
        updateCardDom(card);
        markDirty(card);
      })();
    } else {
      void ensureCoverage(card, { fillEmpty: true });
    }
  }

  // ============================================================
  //  事件绑定
  // ============================================================
  function bindEvents() {
    // 看板滚动时把悬停的十字线收掉：提示框是 position:fixed，
    // 卡片一滚它就停在原地、指向别的点了。
    $('cardGrid').addEventListener('scroll', () => {
      for (const c of S.cards) if (c._hoverLocal) hoverClear(c);
    }, { passive: true });

    // 面板折叠
    document.querySelectorAll('[data-toggle]').forEach((t) => {
      t.addEventListener('click', () => {
        const panel = t.closest('.panel');
        panel.dataset.open = panel.dataset.open === 'true' ? 'false' : 'true';
        // 展开「历史数据」时刷新一次统计（条数/占用是查询出来的，不必实时）
        if (panel.dataset.name === 'history' && panel.dataset.open === 'true') refreshHistory();
      });
    });

    // 协议切换时同步默认端口
    $('fProtocol').addEventListener('change', (e) => {
      const cur = $('fPort').value;
      const isDefault = !cur || Object.values({ mqtt: 1883, mqtts: 8883, ws: 8083, wss: 8084 }).includes(Number(cur));
      if (isDefault) $('fPort').value = defaultPort(e.target.value);
    });

    $('btnConnect').addEventListener('click', doConnect);
    $('btnDisconnect').addEventListener('click', doDisconnect);
    $('btnConnectTop').addEventListener('click', () => {
      if ($('btnConnectTop').dataset.mode === 'disconnect') doDisconnect();
      else doConnect();
    });

    // 回车快速连接
    ['fHost', 'fPort', 'fUser', 'fPass'].forEach((id) => {
      $(id).addEventListener('keydown', (e) => { if (e.key === 'Enter') doConnect(); });
    });

    // 内置 Broker
    $('btnBrokerStart').addEventListener('click', async () => {
      try {
        const snap = await api('/api/broker/start', {
          port: Number($('fBrokerPort').value) || 1883,
          username: $('fBrokerUser').value.trim(),
          password: $('fBrokerPass').value,
        });
        applyBroker(snap);
        toast(`内置 Broker 已启动，监听端口 ${snap.port}`);
        if (snap.port !== Number($('fBrokerPort').value)) {
          toast(`端口被占用，已自动改用 ${snap.port}`, 'warn');
        }
      } catch (err) { toast(err.message, 'err'); }
    });

    $('btnBrokerStop').addEventListener('click', async () => {
      if (!confirm('停止内置 Broker？已接入的设备会被断开。')) return;
      try {
        applyBroker(await api('/api/broker/stop', {}));
        toast('内置 Broker 已停止');
      } catch (err) { toast(err.message, 'err'); }
    });

    // 让平台自己去订阅本地 broker，设备一发数据看板就能看到
    $('btnBrokerSelf').addEventListener('click', async () => {
      try {
        const r = await api('/api/broker/connect-self', {});
        $('fProtocol').value = 'mqtt';
        $('fHost').value = '127.0.0.1';
        $('fPort').value = r.connection.port;
        if (r.snapshot) applyStatus(r.snapshot);
        toast('平台已接入本地 Broker，开始汇总数据');
      } catch (err) { toast(err.message, 'err'); }
    });

    // 数据模拟器开关
    $('fSim').addEventListener('change', async (e) => {
      try {
        const r = await api('/api/broker/sim', { action: e.target.checked ? 'start' : 'stop' });
        if (r.broker) applyBroker(r.broker);
        toast(e.target.checked ? '数据模拟器已开启，正在向 Broker 注入演示数据' : '数据模拟器已关闭');
      } catch (err) {
        e.target.checked = !e.target.checked;
        toast(err.message, 'err');
      }
    });

    // 订阅
    $('btnAddSub').addEventListener('click', addSub);
    $('fAddSub').addEventListener('keydown', (e) => { if (e.key === 'Enter') addSub(); });

    async function addSub() {
      const topic = $('fAddSub').value.trim();
      if (!topic) return;
      try {
        S.subs = await api('/api/subscribe', { topic, qos: 0 });
        $('fAddSub').value = '';
        renderSubs();
        toast(`已订阅 ${topic}`);
      } catch (err) { toast(err.message, 'err'); }
    }

    // 主题搜索
    $('fTopicSearch').addEventListener('input', (e) => {
      S.topicFilter = e.target.value;
      renderTopics();
    });

    // 看板工具
    $('fWindow').addEventListener('change', async (e) => {
      const min = Math.max(0, Number(e.target.value) || 0);
      S.ui.chartWindowMin = min;
      // 全局默认只用管“没自己设过窗口”的卡片；单卡覆盖的不动
      const followed = S.cards.filter((c) => c.windowMin === null);
      // 窗口变了，之前「补过但没补到」的记录就作废：换个窗口该重新问一次库
      S.backfilled = new Set();
      for (const c of followed) markDirty(c);
      api('/api/ui', { ui: { chartWindowMin: min } })
        .catch((err) => console.warn('保存 X 轴范围失败', err.message));

      const msg = min > 0 ? `X 轴改为最近 ${windowLabel(min)}（${followed.length} 张卡片跟随）` : 'X 轴改为显示全部数据';
      const overridden = S.cards.length - followed.length;
      // 内存缓冲只有那么深，窗口比它长时曲线铺不满 —— 这是最容易懵的地方，直接说出来
      const short = min > 0 ? followed.filter((c) => {
        const cov = seriesCoverage(c);
        return cov && cov.span > 0 && cov.span < min * 60000 * 0.95;
      }) : [];
      const tail = [];
      if (overridden > 0) tail.push(`另有 ${overridden} 张卡片已单独设置，不受影响`);
      if (short.length > 0) tail.push(`有 ${short.length} 张卡片的缓冲不够长，正从历史库补数据`);
      toast(tail.length ? `${msg}；${tail.join('；')}` : msg, short.length > 0 ? 'warn' : undefined);

      if (min <= 0) return;
      // 缓冲不够长的补"跨度"，够长但点太稀的补"分辨率" —— 两种情况都交给 ensureCoverage，
      // 所以这里对每张跟随的卡片都跑一遍（它自己会判断要不要真的打接口）。
      let fixed = 0;
      let shortStill = 0;
      for (const c of followed) {
        const wasShort = short.includes(c);
        const added = await ensureCoverage(c).catch(() => 0);
        if (added > 0) fixed += 1;
        else if (wasShort) shortStill += 1;
      }
      if (fixed > 0) toast(`已从历史库给 ${fixed} 张卡片补上数据（曲线会自己重画）`);
      if (shortStill > 0) {
        toast(`还有 ${shortStill} 张卡片库里也只有这么多数据，曲线会短一些（等设备攒够就好）`, 'warn');
      }
    });

    $('fCols').addEventListener('change', (e) => {
      S.cols = Number(e.target.value);
      $('cardGrid').dataset.cols = S.cols;
      localStorage.setItem('mqtt-monitor-cols', String(S.cols));
      for (const c of S.cards) markDirty(c);
    });

    $('fAutoAdd').addEventListener('change', (e) => {
      S.autoAdd = e.target.checked;
      localStorage.setItem('mqtt-monitor-autoadd', S.autoAdd ? '1' : '0');
    });

    // 同前缀自动合并：勾上时立刻把现有看板并一遍（不用等刷新），取消勾选只是不再自动并，
    // 已经并好的分组卡保持原样 —— 想拆开就在那张卡的设置里取消「按前缀分组」
    $('fAutoGroup').addEventListener('change', (e) => {
      S.autoGroup = e.target.checked;
      localStorage.setItem('mqtt-monitor-autogroup', S.autoGroup ? '1' : '0');
      if (S.autoGroup) void reconcileAutoGroups().catch(() => 0);
    });

    $('btnSeedCards').addEventListener('click', () => {
      if (S.catalog.length === 0) { toast('没有可用的测点定义', 'warn'); return; }
      let added = 0;
      for (const def of S.catalog) {
        if (S.cardMap.has(def.topic)) continue;
        createCard(def, true);
        added += 1;
      }
      saveCards();
      renderTopics();
      toast(added ? `已生成 ${added} 张卡片` : '示例卡片都已存在');
      if (added) void reconcileAutoGroups().catch(() => 0);
    });

    $('btnClearCards').addEventListener('click', () => {
      if (S.cards.length === 0) return;
      if (!confirm(`确定清空看板上的 ${S.cards.length} 张卡片吗？（不影响订阅与数据接收）`)) return;
      for (const c of S.cards.slice()) removeCard(c);
      toast('看板已清空');
    });

    // 消息流水：点标题栏折叠/展开（点里面的输入框和按钮不折叠）
    $('logToggle').addEventListener('click', (e) => {
      if (e.target.closest('input, button')) return;
      const block = $('logBlock');
      block.dataset.open = block.dataset.open === 'true' ? 'false' : 'true';
      localStorage.setItem('mqtt-monitor-logopen', block.dataset.open);
    });

    // 日志工具
    $('btnLogPause').addEventListener('click', () => {
      S.logPaused = !S.logPaused;
      $('btnLogPause').textContent = S.logPaused ? '▶' : '⏸';
      $('btnLogPause').title = S.logPaused ? '继续滚动' : '暂停滚动（数据仍在接收）';
      toast(S.logPaused ? '流水已暂停（数据仍在接收）' : '流水已恢复');
    });
    $('btnLogClear').addEventListener('click', () => {
      S.logs = [];
      renderLogs(true);
      toast('流水已清空');
    });
    $('fLogFilter').addEventListener('input', (e) => {
      S.logFilter = e.target.value;
      renderLogs(true);
    });
    $('btnLogExport').addEventListener('click', exportCsv);

    // 顶栏
    $('btnPause').addEventListener('click', () => {
      S.paused = !S.paused;
      $('btnPause').textContent = S.paused ? '▶ 继续' : '⏸ 暂停';
      $('btnPause').classList.toggle('btn-primary', S.paused);
      if (!S.paused) { scheduleRender(); toast('画面已恢复刷新'); }
    });

    $('btnReset').addEventListener('click', async () => {
      const rows = S.history && S.history.rows ? `\n\n已落盘的 ${S.history.rows.toLocaleString()} 条历史也会一起删掉。` : '';
      if (!confirm(`清空历史数据与统计？\n\n看板卡片会保留，但曲线会从头开始。${rows}`)) return;
      try {
        const r = await api('/api/reset', {});
        toast(r.purgedRows ? `已清空（含落盘 ${r.purgedRows.toLocaleString()} 条）` : '已清空');
        refreshHistory();
      } catch (err) { toast(err.message, 'err'); }
    });

    $('btnTheme').addEventListener('click', toggleTheme);

    // ---------------- 历史数据（落盘） ----------------
    $('btnKeepDays').addEventListener('click', async () => {
      const days = Number($('fKeepDays').value);
      if (!Number.isFinite(days) || days <= 0) { toast('保留天数要填正数', 'warn'); return; }
      try {
        const r = await api('/api/history/keep-days', { keepDays: days });
        applyHistory(r.stats);
        toast(r.deleted > 0
          ? `保留 ${r.keepDays} 天，已清掉超期的 ${r.deleted.toLocaleString()} 条`
          : `已改为保留 ${r.keepDays} 天`);
      } catch (err) { toast(err.message, 'err'); }
    });

    $('btnHistExport').addEventListener('click', () => {
      const days = Number($('fKeepDays').value) || (S.history && S.history.keepDays) || 7;
      const url = `/api/history/export?days=${encodeURIComponent(days)}`;
      toast(`正在导出最近 ${days} 天的历史…`);
      const a = document.createElement('a');
      a.href = url;
      a.download = '';
      document.body.appendChild(a);
      a.click();
      a.remove();
    });

    $('btnHistClear').addEventListener('click', async () => {
      const rows = S.history && S.history.rows ? S.history.rows.toLocaleString() : '0';
      if (!confirm(`清空已落盘的 ${rows} 条历史数据？\n\n看板卡片会保留，但曲线会从头开始（删掉无法恢复）。`)) return;
      try {
        const r = await api('/api/reset', {});
        refreshHistory();
        toast(`已清空历史库（${(r.purgedRows || 0).toLocaleString()} 条）`);
      } catch (err) { toast(err.message, 'err'); }
    });

    // 下发
    $('btnPublish').addEventListener('click', async () => {
      const topic = $('fPubTopic').value.trim();
      if (!topic) { toast('请填写要下发的主题', 'warn'); return; }
      try {
        await api('/api/publish', {
          topic,
          payload: $('fPubPayload').value,
          qos: 0,
          retain: $('fPubRetain').checked,
        });
        toast(`已下发到 ${topic}`);
      } catch (err) { toast(err.message, 'err'); }
    });

    // 弹窗
    $('mSave').addEventListener('click', saveCardModal);
    $('mCancel').addEventListener('click', closeCardModal);
    // 改窗口时立刻重算提示（“窗口比缓冲还长”这类话要当场看见，不用等保存）
    $('mWindow').addEventListener('change', () => {
      if (S.editingCard) updateWindowNote(S.editingCard);
    });
    $('mFieldClear').addEventListener('click', () => {
      // 全部取消勾选 = 回到「自动：这个主题上报的测点/字段都画」
      const box = $('mFieldPick');
      box.querySelectorAll('input:checked').forEach((n) => {
        n.checked = false;
        n.closest('.fp-item')?.classList.remove('is-on');
        // 分组卡的「哪个主题」勾选框在 sticky 标题行（.fp-group）里，不是 .fp-item。
        // 清空 = 回到自动（全都画），所以是把"已排除"的压暗状态去掉，不是加上。
        n.closest('.fp-group.is-topic')?.classList.remove('is-off');
      });
      // 分组卡：每行是一个下拉，回到「自动」
      box.querySelectorAll('select').forEach((s) => { s.value = ''; });
      $('mField').value = '';
      updateFieldHint();
    });
    // 勾/取消「按前缀分组」时，把说明和主题占位符一起换掉，
    // 顺手把主题补成前缀（用户是从某张单主题卡点进来的话，少手敲一遍）
    $('mGroup').addEventListener('change', () => {
      const on = $('mGroup').checked;
      const cur = $('mTopic').value.trim();
      if (on && cur.includes('/')) $('mTopic').value = groupRoot(cur);
      $('mTopic').placeholder = on ? 'plc（主题前缀，不写点位）' : 'plc/DB1.DBW290';
      if (S.editingCard) renderFieldPicker(S.editingCard);
    });
    $('mDelete').addEventListener('click', () => {
      const card = S.editingCard;
      if (!card) return;
      removeCard(card);
      closeCardModal();
      toast('卡片已删除');
    });
    $('cardModal').addEventListener('click', (e) => {
      if (e.target === $('cardModal')) closeCardModal();
    });
    document.addEventListener('keydown', (e) => {
      if (e.key === 'Escape') closeCardModal();
    });
  }

  function toggleTheme() {
    S.theme = S.theme === 'dark' ? 'light' : 'dark';
    document.body.classList.toggle('theme-dark', S.theme === 'dark');
    $('btnTheme').textContent = S.theme === 'dark' ? '☀️' : '🌙';
    localStorage.setItem('mqtt-monitor-theme', S.theme);
    for (const c of S.cards) markDirty(c);
  }

  // ============================================================
  //  启动
  // ============================================================
  async function boot() {
    // 本地偏好
    S.theme = localStorage.getItem('mqtt-monitor-theme') || 'light';
    if (S.theme === 'dark') {
      document.body.classList.add('theme-dark');
      $('btnTheme').textContent = '☀️';
    }
    S.cols = Number(localStorage.getItem('mqtt-monitor-cols')) || 3;
    S.autoAdd = localStorage.getItem('mqtt-monitor-autoadd') !== '0';
    $('fCols').value = String(S.cols);
    $('cardGrid').dataset.cols = S.cols;
    $('fAutoAdd').checked = S.autoAdd;

    // 消息流水默认折叠，用户展开过就记住
    if (localStorage.getItem('mqtt-monitor-logopen') === 'true') {
      $('logBlock').dataset.open = 'true';
    }

    bindEvents();
    connectWs();

    // 拉初始状态
    try {
      const st = await api('/api/state');
      writeForm(st.connection);
      S.subs = st.subscriptions || [];
      S.ui = st.ui || S.ui;
      // 「同前缀自动合并」必须在 S.ui 拉到之后才能定：用户在界面上勾过就以他为准
      // （localStorage），没勾过才用配置里的 ui.autoGroup（默认 true）
      const lsGroup = localStorage.getItem('mqtt-monitor-autogroup');
      S.autoGroup = lsGroup === null ? (S.ui.autoGroup !== false) : lsGroup !== '0';
      $('fAutoGroup').checked = S.autoGroup;
      // 回填 X 轴范围下拉；存的值不在预设列表里（手改过 config）就退回“全部”，避免下拉空白
      syncWindowSelect();
      renderSubs();

      // 测点定义：用于自动上板时带上单位/上下限，也用于「生成示例看板」
      S.catalog = st.catalog || [];
      catalogMap = new Map(S.catalog.map((d) => [d.topic, d]));

      // 内置 Broker 台账
      if (st.brokerConfig) {
        $('fBrokerPort').value = st.brokerConfig.port || 1883;
        $('fBrokerUser').value = st.brokerConfig.username || '';
        $('fBrokerPass').value = st.brokerConfig.password || '';
      }
      if (st.brokerState) applyBroker(st.brokerState);
      if (st.historyState) applyHistory(st.historyState);

      applyStatus(st.snapshot || { status: 'disconnected' });
      applyTopicList(st.topics || []);

      // 没连上时自动展开「连接设置」，省得用户到处找
      if (S.mqttStatus !== 'connected') {
        const p = document.querySelector('.sidebar .panel[data-name="connect"]');
        if (p) p.dataset.open = 'true';
      }

      // 恢复看板卡片
      if (Array.isArray(st.cards) && st.cards.length > 0) {
        // 老配置里每张卡都写着 `points: 120` —— 那是上一版的默认值，不是用户主动设的上限。
        // 新语义下 120 会把曲线截断（「曲线显示不全」就是它干的），所以一次性迁移成 0（不限）。
        let migrated = 0;
        for (const def of st.cards) {
          if (Number(def.points) === 120) { def.points = 0; migrated += 1; }
        }

        // 重建期间 WS 进来的报文先缓存（自动上板会建出重复卡片，而且刚建好就被下面清掉了）
        S.restoringCards = true;
        S.cards = [];
        S.cardMap = new Map();
        $('cardGrid').innerHTML = '';
        for (const def of st.cards) createCard(def, true);
        S.restoringCards = false;
        if (migrated > 0) {
          console.log(`[迁移] ${migrated} 张卡片的「保留点数 120」已改为「不限」——曲线不再被 120 个点截断`);
          saveCards();
        }

        // 把重建期间攒下的报文灌进卡片（WS 连接时回放的"最近一条报文"就在这里面），
        // 然后才回填历史 —— 归并会把这些更新的点插到正确的位置上
        if (S.pendingMsgs.length) {
          const pending = S.pendingMsgs;
          S.pendingMsgs = [];
          ingestMessages(pending, { replay: true });
        }

        // 同前缀自动合并：老看板上"同一台设备的点位各占一张卡"的，在这里并成一张。
        // 必须放在回填**之前** —— 合并会重建曲线集合，先并完再回填，历史只拉一遍。
        try { await reconcileAutoGroups(); } catch (err) { console.warn('同前缀合并失败', err.message); }

        // 回填历史曲线，刷新页面后曲线不会断。
        // 取多少条由 **X 轴窗口** 决定（不是"点数上限"）：窗口是最近 15 分钟，就跟服务端要那 15 分钟，
        // 而不是只看内存缓冲里那几十个点 —— 后者正是用户说的「曲线显示不全」。
        for (const card of S.cards) {
          try {
            // 分组卡：前缀下每个成员主题各拉一次（backfillCard 内部就是干这个的）
            if (card.group) {
              if (await backfillCard(card, 0)) { updateCardDom(card); markDirty(card); }
              continue;
            }
            const win = effectiveWindowMin(card);
            const q = win > 0 ? `&windowMin=${win}` : '';
            const hist = await api(`/api/history?topic=${encodeURIComponent(card.topic)}&limit=${FETCH_MAX}&sample=1${q}`);
            if (!Array.isArray(hist) || !hist.length) continue;
            card.dbTruncated = hist.length >= FETCH_MAX;   // 顶到上限：库里还有更多，画面上是抽样结果
            // ⚠️ 这里必须**按时间归并**，不能逐条 pushSample（那是 append）：
            // 上面刚灌进去的实时点是最新的，它已经在数组开头了；
            // 再把更早的历史 append 在后头，ts 就不再递增 ——
            // 窗口裁剪、X 轴左端刻度、「覆盖多久」全都会算错。
            // 归并同时还能把重复的那条去掉。
            mergeHistory(card, hist);
            card.count = (card.count || 0) + hist.length;
            applyLatest(card, hist[hist.length - 1]);   // 大字/告警按最新那条来
            updateCardDom(card);
            markDirty(card);
          } catch { /* 历史取不到就等实时数据 */ }
        }
      }

      updateTopStats();
    } catch (err) {
      toast('初始化失败：' + err.message, 'err');
      console.error(err);
    }

    // 周期性兜底刷新（顶栏时长、离线判定）
    //  第一次扫描只记基线、不弹提示：页面刚打开时若本来就有一堆卡挂着，
    //  一次性弹十几条 toast 反而把真正的新掉线淹了。
    let staleScanReady = false;
    setInterval(() => {
      if (S.paused) return;
      updateTopStats();
      let needRender = false;
      const firstScan = !staleScanReady;
      for (const c of S.cards) {
        const stale = isStale(c);
        const wasStale = !!c._stale;
        const cardFlipped = stale !== wasStale;
        if (cardFlipped) {
          c._stale = stale;
          needRender = true;
          if (!firstScan && !S.paused) {
            if (stale) {
              const thr = staleThreshold(c);
              toast(`「${c.name}」${c.group ? '整卡' : ''}已无新数据${thr ? `（超 ${fmtGap(thr)}）` : ''}`, 'err');
            } else {
              toast(`「${c.name}」恢复收数`, 'ok');
            }
          }
        }

        // 分组卡还要盯"哪个成员掉线"：卡片级判定只看最新一条，看不见这件事
        if (c.group) {
          const gone = staleTopics(c);
          const prev = c._staleTopics || [];
          const added = gone.filter((t) => !prev.includes(t));
          const back = prev.filter((t) => !gone.includes(t));
          c._staleTopics = gone;
          if (added.length || back.length) {
            needRender = true;
            // 整卡刚好在这一拍翻转时上面那条已经说过了，别重复刷屏
            if (!firstScan && !cardFlipped) {
              const fmt = (arr) => `${arr.slice(0, 3).map(tailName).join('、')}${arr.length > 3 ? ` 等 ${arr.length} 个` : ''}`;
              if (added.length) toast(`「${c.name}」${fmt(added)} 掉线了`, 'err');
              if (back.length) toast(`「${c.name}」${fmt(back)} 恢复收数`, 'ok');
            }
          }
        }
      }
      staleScanReady = true;
      if (needRender) scheduleRender();
    }, 2000);
  }

  document.addEventListener('DOMContentLoaded', boot);

  // 暴露给浏览器控制台 / 二次开发使用：
  //   MQTTMonitor.state.cards                    当前看板卡片
  //   MQTTMonitor.createCard({topic:'x'})        用代码加卡片
  //   MQTTMonitor.api('/api/broker')             直接调后端接口
  //   MQTTMonitor.ingestMessages([{...}])        手动灌一条报文进去（不经过 broker，便于自检）
  window.MQTTMonitor = {
    state: S,
    api,
    toast,
    createCard,
    removeCard,
    saveCards,
    markDirty,
    renderTopics,
    shortName,
    ingestMessages,
    pickSamples,
    renderFieldPicker,
    collectFields,
    normalizeSeriesTs,   // 数据自检用：把曲线的 ts/data 归正序（控制台里排查"刻度乱"时很有用）
    mergeHistory,
    ensureCoverage,
    reportIntervalMs,    // 离线判定自检用：这条曲线实测的上报节拍
    staleThreshold,
    isStale,
    staleSummary,        // 离线判定自检用：{cards:整卡静默, partial:组里部分点位掉线}
    staleTopics,         // 分组卡里已掉线的成员主题
    topicStale,
    topicSilenceMs,      // 某个成员主题自己停发多久了
    topicThreshold,      // 某个成员主题自己的离线阈值（null = 节拍还没估出来，判不了）
    hoverMove,           // 悬停取点自检用：hoverMove(card, {clientX, clientY})
    hoverClear,
    nearestIdx,          // 单调数组里找最近下标（纯函数，方便单测）
    thinTicks,
    groupCardFor,        // 分组卡片自检用：这条主题归哪张分组卡管（null = 没卡管）
    groupCardForPrefix,  // 分组卡片自检用：前缀上认领它的卡（含已把它撤下来的，用来加回）
    groupMembers,        // 分组卡片自检用：这张卡当前收录了哪些主题
    reconcileAutoGroups, // 同前缀自动合并自检用
    parentTopic,
  };
})();

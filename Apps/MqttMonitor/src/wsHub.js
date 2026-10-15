'use strict';

/**
 * WebSocket 广播中心：把 MQTT 消息、连接状态、统计数据推给所有打开的浏览器页面。
 * 与 HTTP 服务共用同一端口（走 upgrade）。
 */

const { WebSocketServer } = require('ws');

class WsHub {
  constructor() {
    this.wss = null;
    this.clients = new Set();
    this.heartbeat = null;
    /** 新页面接入时用于补发初始状态的回调，返回 [{type, ...}] */
    this.initialProvider = null;
  }

  /** 注册初始状态提供者（页面刚打开时把错过的状态补上） */
  setInitialProvider(fn) {
    this.initialProvider = fn;
  }

  /** 挂到已有 http server 上 */
  attach(httpServer) {
    this.wss = new WebSocketServer({ server: httpServer, path: '/ws' });

    this.wss.on('connection', (socket, req) => {
      socket.isAlive = true;
      socket.on('pong', () => { socket.isAlive = true; });
      this.clients.add(socket);

      const ip = (req.socket.remoteAddress || '').replace('::ffff:', '');
      console.log(`[ws] 页面已连接 ${ip}（当前 ${this.clients.size} 个）`);

      // 新页面接入时，先把它错过的状态补上
      try {
        const frames = this.initialProvider ? this.initialProvider() : [];
        for (const frame of frames) {
          socket.send(JSON.stringify({ ts: Date.now(), ...frame }));
        }
      } catch (err) {
        console.error(`[ws] 初始状态下发失败: ${err.message}`);
      }

      socket.on('close', () => {
        this.clients.delete(socket);
        console.log(`[ws] 页面断开 ${ip}（剩余 ${this.clients.size} 个）`);
      });
      socket.on('error', () => this.clients.delete(socket));
    });

    // 30s 心跳，清掉半死连接（浏览器休眠、网线拔掉等）
    this.heartbeat = setInterval(() => {
      for (const socket of this.clients) {
        if (socket.isAlive === false) { socket.terminate(); this.clients.delete(socket); continue; }
        socket.isAlive = false;
        try { socket.ping(); } catch { /* ignore */ }
      }
    }, 30000);
    this.heartbeat.unref?.();
  }

  /** 广播一个对象（自动序列化） */
  broadcast(type, payload) {
    if (this.clients.size === 0) return;
    const frame = JSON.stringify({ type, ...payload });
    for (const socket of this.clients) {
      // 1 === OPEN
      if (socket.readyState === 1) {
        try { socket.send(frame); } catch { /* ignore */ }
      }
    }
  }

  close() {
    clearInterval(this.heartbeat);
    for (const socket of this.clients) { try { socket.close(); } catch { /* ignore */ } }
    this.clients.clear();
    this.wss?.close();
  }
}

module.exports = { WsHub };

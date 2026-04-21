// src/worker/market.worker.ts

// 记录所有连入的浏览器标签页 (MessagePort)
const ports = new Set<MessagePort>();

// ==========================================
// 1. 全市场 Ticker 组合流 (包含资金费率)
// ==========================================
let tickerWs: WebSocket | null = null;

function connectTickers() {
  if (tickerWs && tickerWs.readyState === WebSocket.OPEN) return;
  tickerWs = new WebSocket('wss://fstream.binance.com/stream?streams=!miniTicker@arr/!markPrice@arr@1s');
  
  tickerWs.onmessage = (e) => {
    // 收到数据后，像大喇叭一样广播给所有标签页
    broadcast({ type: 'TICKERS_DATA', payload: JSON.parse(e.data) });
  };
  
  tickerWs.onclose = () => setTimeout(connectTickers, 3000); // 断线重连
}

// ==========================================
// 2. 动态 K 线数据流 (带智能订阅管理)
// ==========================================
let klineWs: WebSocket | null = null;
const activeStreams = new Map<string, number>(); // 记录每个流被多少个 Tab 订阅

function connectKlineWs() {
  if (klineWs && klineWs.readyState === WebSocket.OPEN) return;
  klineWs = new WebSocket('wss://fstream.binance.com/stream');
  
  klineWs.onopen = () => {
    // 重连成功后，把目前积累的任务重新订阅一遍
    const streams = Array.from(activeStreams.keys());
    if (streams.length > 0) {
      klineWs?.send(JSON.stringify({ method: 'SUBSCRIBE', params: streams, id: Date.now() }));
    }
  };
  
  klineWs.onmessage = (e) => {
    broadcast({ type: 'KLINE_DATA', payload: JSON.parse(e.data) });
  };
  
  klineWs.onclose = () => setTimeout(connectKlineWs, 3000);
}

function handleSubscribe(stream: string) {
  const count = activeStreams.get(stream) || 0;
  activeStreams.set(stream, count + 1);
  
  // 只有第一个人看的时候，才向币安发请求
  if (count === 0 && klineWs?.readyState === WebSocket.OPEN) {
    klineWs.send(JSON.stringify({ method: 'SUBSCRIBE', params: [stream], id: Date.now() }));
  }
}

function handleUnsubscribe(stream: string) {
  const count = activeStreams.get(stream) || 0;
  if (count > 0) {
    activeStreams.set(stream, count - 1);
    // 最后一个人离开时，切断币安的数据流
    if (count - 1 === 0) {
      activeStreams.delete(stream);
      if (klineWs?.readyState === WebSocket.OPEN) {
        klineWs.send(JSON.stringify({ method: 'UNSUBSCRIBE', params: [stream], id: Date.now() }));
      }
    }
  }
}

// ==========================================
// 3. 通信与生命周期
// ==========================================
function broadcast(message: any) {
  ports.forEach(port => port.postMessage(message));
}

// 当一个新的浏览器 Tab 建立连接时触发
(self as any).onconnect = (e: MessageEvent) => {
  const port = e.ports[0];
  ports.add(port);

  // 监听从 Tab 发来的订阅指令
  port.onmessage = (event) => {
    const { type, stream } = event.data;
    if (type === 'SUBSCRIBE') handleSubscribe(stream);
    if (type === 'UNSUBSCRIBE') handleUnsubscribe(stream);
    if (type === 'DISCONNECT') {
      ports.delete(port); // Tab 关闭时清除连接
    }
  };

  port.start();
  
  // 确保底层长连接活着
  connectTickers();
  connectKlineWs();
};
// src/worker/market.worker.ts
import * as signalR from '@microsoft/signalr'; // 🌟 Worker 内核引入 SignalR

const connectedPorts = new Set<MessagePort>();

// ==========================================
// 状态管理
// ==========================================
let publicWs: WebSocket | null = null;
let signalRConnection: signalR.HubConnection | null = null;
let userDataWs: WebSocket | null = null;
let currentListenKey: string | null = null;

// 🌟 当前数据源 (默认走 C# 后端)
let currentDataSource: 'binance' | 'backend' = 'backend';

const activeSubscriptions = new Set<string>();
const defaultStreams = ['!miniTicker@arr', '!markPrice@arr@1s'];

// ==========================================
// 广播工具：向所有活跃的 UI 页面推送数据
// ==========================================
function broadcastToPorts(type: string, payload: any) {
  connectedPorts.forEach((port) => {
    port.postMessage({ type, payload });
  });
}

// 🌟 统一数据格式化：屏蔽底层数据源格式差异
function processMarketData(payload: any) {
  if (payload.stream && payload.data) {
    // 格式 A：组合流格式 (C# 转发或币安 /stream)
    const stream = payload.stream;
    if (stream.includes('miniTicker') || stream.includes('markPrice')) {
      broadcastToPorts('TICKERS_DATA', payload); 
    } else if (stream.includes('@kline_')) {
      broadcastToPorts('KLINE_DATA', payload);
    }
  } else {
    // 格式 B：原始流格式 (币安 /ws)
    if (Array.isArray(payload)) {
      if (payload[0]?.e === '24hrMiniTicker') broadcastToPorts('TICKERS_DATA', { stream: '!miniTicker@arr', data: payload });
      else if (payload[0]?.e === 'markPriceUpdate') broadcastToPorts('TICKERS_DATA', { stream: '!markPrice@arr', data: payload });
    } else if (payload.e === 'kline') {
      broadcastToPorts('KLINE_DATA', { stream: `${payload.s.toLowerCase()}@kline_${payload.k.i}`, data: payload });
    }
  }
}

// ==========================================
// 1. 公共数据流 (双引擎智能路由)
// ==========================================
async function connectPublicStream() {
  // 切换前先清理所有旧连接
  if (publicWs) { publicWs.onclose = null; publicWs.close(); publicWs = null; }
  if (signalRConnection) { await signalRConnection.stop(); signalRConnection = null; }

  const streamsToSubscribe = [...defaultStreams, ...Array.from(activeSubscriptions)];

  // 🔴 引擎 A: C# SignalR 中继
  if (currentDataSource === 'backend') {
    signalRConnection = new signalR.HubConnectionBuilder()
      .withUrl("http://localhost:5000/hubs/market") // 指向你的 C# 后端
      .withAutomaticReconnect([0, 2000, 5000, 10000])
      .build();

    signalRConnection.on("ReceiveMarketData", (rawJson: string) => {
      try { processMarketData(JSON.parse(rawJson)); } catch (e) {}
    });

    signalRConnection.onreconnected(() => {
      streamsToSubscribe.forEach(s => signalRConnection?.invoke("Subscribe", s).catch(console.error));
    });

    try {
      await signalRConnection.start();
      console.log('🚀 Worker: C# 后端 SignalR 连接成功');
      // 连上后，恢复所有订阅
      streamsToSubscribe.forEach(s => signalRConnection?.invoke("Subscribe", s).catch(console.error));
    } catch (err) {
      console.error('❌ Worker: SignalR 连接失败', err);
      setTimeout(connectPublicStream, 5000);
    }
  } 
  // 🔵 引擎 B: 币安原生 WebSocket
  else {
    publicWs = new WebSocket('wss://fstream.binance.com/ws');

    publicWs.onopen = () => {
      console.log('🌐 Worker: 币安直连 WebSocket 连接成功');
      if (streamsToSubscribe.length > 0) {
        publicWs?.send(JSON.stringify({ method: 'SUBSCRIBE', params: streamsToSubscribe, id: Date.now() }));
      }
    };

    publicWs.onmessage = (event) => {
      try { processMarketData(JSON.parse(event.data)); } catch (e) {}
    };

    publicWs.onclose = () => {
      console.warn('⚠️ Worker: 币安公共流断开，3秒后重连...');
      setTimeout(connectPublicStream, 3000);
    };
  }
}

// ==========================================
// 2. 私有数据流 (永远直连币安，不经过 C#)
// ==========================================
function connectUserDataStream() {
  if (!currentListenKey) return;
  if (userDataWs) { userDataWs.onclose = null; userDataWs.close(); }

  userDataWs = new WebSocket(`wss://fstream.binance.com/ws/${currentListenKey}`);
  userDataWs.onopen = () => console.log('🔐 Worker: 账户私有流直连成功');
  userDataWs.onmessage = (event) => {
    try { broadcastToPorts('ACCOUNT_DATA', JSON.parse(event.data)); } catch (err) {}
  };
  userDataWs.onclose = () => { setTimeout(connectUserDataStream, 5000); };
}

// ==========================================
// 3. UI 主线程通讯监听
// ==========================================
onconnect = (e: MessageEvent) => {
  const port = e.ports[0];
  connectedPorts.add(port);
  port.start();

  // 当有页面连接时，开启公共流
  if (!signalRConnection && !publicWs) {
    connectPublicStream();
  }

  port.onmessage = (event) => {
    const { type, stream, listenKey, source } = event.data;

    switch (type) {
      // 🌟 核心：接收 UI 切换数据源指令
      case 'SWITCH_SOURCE':
        if (source && source !== currentDataSource) {
          console.log(`🔄 Worker: 收到指令，切换数据源至 [${source}]`);
          currentDataSource = source;
          connectPublicStream(); // 重新走路由逻辑
        }
        break;

      case 'SUBSCRIBE':
        if (stream && !activeSubscriptions.has(stream)) {
          activeSubscriptions.add(stream);
          if (currentDataSource === 'backend' && signalRConnection?.state === signalR.HubConnectionState.Connected) {
            signalRConnection.invoke("Subscribe", stream).catch(console.error);
          } else if (currentDataSource === 'binance' && publicWs?.readyState === WebSocket.OPEN) {
            publicWs.send(JSON.stringify({ method: 'SUBSCRIBE', params: [stream], id: Date.now() }));
          }
        }
        break;

      case 'UNSUBSCRIBE':
        if (stream && activeSubscriptions.has(stream)) {
          activeSubscriptions.delete(stream);
          if (currentDataSource === 'backend' && signalRConnection?.state === signalR.HubConnectionState.Connected) {
            signalRConnection.invoke("Unsubscribe", stream).catch(console.error);
          } else if (currentDataSource === 'binance' && publicWs?.readyState === WebSocket.OPEN) {
            publicWs.send(JSON.stringify({ method: 'UNSUBSCRIBE', params: [stream], id: Date.now() }));
          }
        }
        break;

      case 'CONNECT_USER_DATA':
        if (listenKey) {
          currentListenKey = listenKey;
          connectUserDataStream();
        }
        break;

      case 'DISCONNECT':
        connectedPorts.delete(port);
        if (connectedPorts.size === 0) {
          if (publicWs) { publicWs.onclose = null; publicWs.close(); publicWs = null; }
          if (signalRConnection) { signalRConnection.stop(); signalRConnection = null; }
          if (userDataWs) { userDataWs.onclose = null; userDataWs.close(); userDataWs = null; }
          activeSubscriptions.clear();
        }
        break;
    }
  };
};
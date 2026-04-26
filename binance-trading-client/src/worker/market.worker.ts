// src/worker/market.worker.ts
import * as signalR from '@microsoft/signalr';

const connectedPorts = new Set<MessagePort>();

let publicWs: WebSocket | null = null;
let signalRConnection: signalR.HubConnection | null = null;
let userDataWs: WebSocket | null = null;
let currentListenKey: string | null = null;

let currentDataSource: 'binance' | 'backend' = 'backend';

const activeSubscriptions = new Set<string>();
const defaultStreams = ['!miniTicker@arr', '!markPrice@arr@1s'];

function broadcastToPorts(type: string, payload: any) {
  connectedPorts.forEach((port) => {
    port.postMessage({ type, payload });
  });
}

function processMarketData(payload: any) {
  if (payload.stream && payload.data) {
    const stream = payload.stream;
    if (stream.includes('miniTicker') || stream.includes('markPrice')) {
      broadcastToPorts('TICKERS_DATA', payload); 
    } else if (stream.includes('@kline_')) {
      broadcastToPorts('KLINE_DATA', payload);
    }
  } else {
    if (Array.isArray(payload)) {
      if (payload[0]?.e === '24hrMiniTicker') broadcastToPorts('TICKERS_DATA', { stream: '!miniTicker@arr', data: payload });
      else if (payload[0]?.e === 'markPriceUpdate') broadcastToPorts('TICKERS_DATA', { stream: '!markPrice@arr', data: payload });
    } else if (payload.e === 'kline') {
      broadcastToPorts('KLINE_DATA', { stream: `${payload.s.toLowerCase()}@kline_${payload.k.i}`, data: payload });
    }
  }
}

async function connectPublicStream() {
  if (publicWs) { publicWs.onclose = null; publicWs.close(); publicWs = null; }
  if (signalRConnection) { await signalRConnection.stop(); signalRConnection = null; }

  const streamsToSubscribe = [...defaultStreams, ...Array.from(activeSubscriptions)];

  if (currentDataSource === 'backend') {
    signalRConnection = new signalR.HubConnectionBuilder()
      .withUrl("http://localhost:5000/hubs/market")
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
      streamsToSubscribe.forEach(s => signalRConnection?.invoke("Subscribe", s).catch(console.error));
    } catch (err) {
      setTimeout(connectPublicStream, 5000);
    }
  } 
  else {
    // 🌟 币安新规：行情必须加 /market 前缀
    publicWs = new WebSocket('wss://fstream.binance.com/market/stream');

    publicWs.onopen = () => {
      console.log('🌐 Worker: 币安直连 /market 成功');
      if (streamsToSubscribe.length > 0) {
        publicWs?.send(JSON.stringify({ method: 'SUBSCRIBE', params: streamsToSubscribe, id: Date.now() }));
      }
    };

    publicWs.onmessage = (event) => {
      try { processMarketData(JSON.parse(event.data)); } catch (e) {}
    };

    publicWs.onclose = () => setTimeout(connectPublicStream, 3000);
  }
}

function connectUserDataStream() {
  if (!currentListenKey) return;
  if (userDataWs) { userDataWs.onclose = null; userDataWs.close(); }

  // 🌟 币安新规：私有流必须加 /private 前缀
  userDataWs = new WebSocket(`wss://fstream.binance.com/private/ws/${currentListenKey}`);
  
  userDataWs.onopen = () => console.log('🔐 Worker: 账户私有流直连成功');
  userDataWs.onmessage = (event) => {
    try { broadcastToPorts('ACCOUNT_DATA', JSON.parse(event.data)); } catch (err) {}
  };
  userDataWs.onclose = () => setTimeout(connectUserDataStream, 5000);
}

onconnect = (e: MessageEvent) => {
  const port = e.ports[0];
  connectedPorts.add(port);
  port.start();

  if (!signalRConnection && !publicWs) connectPublicStream();

  port.onmessage = (event) => {
    const { type, stream, listenKey, source } = event.data;

    switch (type) {
      case 'SWITCH_SOURCE':
        if (source && source !== currentDataSource) {
          currentDataSource = source;
          connectPublicStream(); 
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
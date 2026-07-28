import * as signalR from '@microsoft/signalr';

// ==========================================
// 1. 状态管理与连接池
// ==========================================
const connectedPorts = new Set<MessagePort>();

let publicWs: WebSocket | null = null;
let signalRConnection: signalR.HubConnection | null = null;
let userDataWs: WebSocket | null = null;
let currentListenKey: string | null = null;

let currentDataSource: 'binance' | 'backend' = 'backend';

// 记录所有存活的流
const activeSubscriptions = new Set<string>();
const defaultStreams = ['!miniTicker@arr', '!markPrice@arr@1s'];

// ==========================================
// 2. 纯函数计算逻辑 (来自原 peak.worker.js)
// ==========================================
function calculateMeanAndStdDev(data: number[]) {
  const n = data.length;
  if (n === 0) return { mean: 0, stdDev: 0 };
  let sum = 0;
  for (let i = 0; i < n; i++) sum += data[i];
  const mean = sum / n;
  let sumSqDiff = 0;
  for (let i = 0; i < n; i++) {
    const diff = data[i] - mean;
    sumSqDiff += diff * diff;
  }
  const stdDev = Math.sqrt(sumSqDiff / n);
  return { mean, stdDev };
}

function calculatePeaks(times: any[], highs: number[], lows: number[], leftLen = 5, rightLen = 5) {
  const peaks = [];
  const valleys = [];
  for (let i = leftLen; i < times.length - rightLen; i++) {
    let isPeak = true;
    let isValley = true;
    for (let j = i - leftLen; j <= i + rightLen; j++) {
      if (j === i) continue;
      if (highs[j] >= highs[i]) { isPeak = false; break; }
    }
    for (let j = i - leftLen; j <= i + rightLen; j++) {
      if (j === i) continue;
      if (lows[j] <= lows[i]) { isValley = false; break; }
    }
    if (isPeak) peaks.push(i);
    if (isValley) valleys.push(i);
  }
  return { peaks, valleys };
}

function calculateTrendLines(peaks: number[], valleys: number[], times: any[], highs: number[], lows: number[]) {
  const lines = [];
  if (peaks.length >= 2) {
    const p1 = peaks[peaks.length - 2];
    const p2 = peaks[peaks.length - 1];
    lines.push({ type: 'resistance', data: [{ time: times[p1], value: highs[p1] }, { time: times[p2], value: highs[p2] }] });
  }
  if (valleys.length >= 2) {
    const v1 = valleys[valleys.length - 2];
    const v2 = valleys[valleys.length - 1];
    lines.push({ type: 'support', data: [{ time: times[v1], value: lows[v1] }, { time: times[v2], value: lows[v2] }] });
  }
  return lines;
}

function calculateVolReversal(times: any[], opens: number[], closes: number[], volumes: number[], config: any) {
  const { period, mode, threshold } = config; 
  const markers = [];
  for (let i = period; i < times.length; i++) {
    const currentVol = volumes[i]; const open = opens[i]; const close = closes[i]; const time = times[i];
    const historyVols = volumes.slice(i - period, i);
    let isSpike = false; let debugText = "";

    if (mode === 'dynamic') {
      const { mean, stdDev } = calculateMeanAndStdDev(historyVols);
      if (stdDev < 0.000001) { isSpike = currentVol > mean * 2; } 
      else {
        const zScore = (currentVol - mean) / stdDev;
        isSpike = zScore >= threshold; 
      }
    } else {
      const { mean } = calculateMeanAndStdDev(historyVols);
      if (mean > 0) isSpike = currentVol >= (mean * threshold);
    }

    if (isSpike) {
      if (close > open) markers.push({ time: time, position: 'aboveBar', color: '#e91e63', shape: 'arrowDown', text: 'V-Short' + debugText, size: 1 });
      else if (close < open) markers.push({ time: time, position: 'belowBar', color: '#2196F3', shape: 'arrowUp', text: 'V-Long' + debugText, size: 1 });
    }
  }
  return markers;
}


// ==========================================
// 3. 通信与广播逻辑
// ==========================================
function broadcastToPorts(type: string, payload: any) {
  connectedPorts.forEach((port) => port.postMessage({ type, payload }));
}

function processMarketData(payload: any) {
  if (payload.stream && payload.data) {
    const stream = payload.stream;
    if (stream.includes('miniTicker') || stream.includes('markPrice')) {
      broadcastToPorts('TICKERS_DATA', payload);
    } else if (stream.includes('@kline_')) {
      broadcastToPorts('KLINE_DATA', payload);
    } else if (stream.includes('@aggTrade')) {
      broadcastToPorts('AGGTRADE_DATA', payload);
    } else if (stream.includes('@depth')) {
      console.log('Worker: broadcasting DEPTH_DATA for stream', stream);
      broadcastToPorts('DEPTH_DATA', payload);
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

  if (currentDataSource === 'backend') {
    signalRConnection = new signalR.HubConnectionBuilder()
      .withUrl(`${import.meta.env.VITE_API_BASE_URL}/hubs/market`)
      .withAutomaticReconnect([0, 2000, 5000, 10000])
      .build();

    signalRConnection.on("ReceiveMarketData", (rawJson: string) => {
      try { processMarketData(JSON.parse(rawJson)); } catch (e) { }
    });

    signalRConnection.on("ReceiveBackendLatency", (ms: number) => {
      broadcastToPorts('BACKEND_LATENCY', ms);
    });

    signalRConnection.on("ReceiveStrategyAlert", (alertData: any) => {
      broadcastToPorts('STRATEGY_ALERT', alertData);
    });

    // 🌟 新增：拦截 Heikin-Ashi 反转信号报警
    signalRConnection.on("ReceiveHaAlert", (alertData: any) => {
      broadcastToPorts('HA_ALERT', alertData);
    });

    signalRConnection.onreconnected(() => {
      console.log('🔄 Worker: SignalR 重连成功，延迟2秒后恢复订阅...');
      setTimeout(() => {
        const currentStreams = [...defaultStreams, ...Array.from(activeSubscriptions)];
        currentStreams.forEach(s => signalRConnection?.invoke("Subscribe", s).catch(console.error));
      }, 1000);
    });

    try {
      await signalRConnection.start();
      console.log('🚀 Worker: C# 后端 SignalR 连接成功');
      const currentStreams = [...defaultStreams, ...Array.from(activeSubscriptions)];
      currentStreams.forEach(s => signalRConnection?.invoke("Subscribe", s).catch(console.error));
    } catch (err) {
      setTimeout(connectPublicStream, 5000);
    }
  }
  else {
    publicWs = new WebSocket('wss://fstream.binance.com/market/stream');

    publicWs.onopen = () => {
      console.log('🌐 Worker: 币安直连 /market 成功');
      const currentStreams = [...defaultStreams, ...Array.from(activeSubscriptions)];
      if (currentStreams.length > 0) {
        publicWs?.send(JSON.stringify({ method: 'SUBSCRIBE', params: currentStreams, id: Date.now() }));
      }
    };

    publicWs.onmessage = (event) => {
      try { processMarketData(JSON.parse(event.data)); } catch (e) { }
    };

    publicWs.onclose = () => setTimeout(connectPublicStream, 3000);
  }
}

function connectUserDataStream() {
  if (!currentListenKey) return;
  if (userDataWs) { userDataWs.onclose = null; userDataWs.close(); }

  userDataWs = new WebSocket(`wss://fstream.binance.com/private/ws/${currentListenKey}`);

  userDataWs.onopen = () => console.log('🔐 Worker: 账户私有流直连成功');
  userDataWs.onmessage = (event) => {
    try { broadcastToPorts('ACCOUNT_DATA', JSON.parse(event.data)); } catch (err) { }
  };
  userDataWs.onclose = () => setTimeout(connectUserDataStream, 5000);
}

// ==========================================
// 4. SharedWorker 消息入口
// ==========================================
(self as any).onconnect = (e: MessageEvent) => {
  const port = e.ports[0];
  connectedPorts.add(port);
  port.start();

  if (!signalRConnection && !publicWs) connectPublicStream();

  port.onmessage = (event) => {
    // 🌟 核心：解析传入的 type 和可选的 msgId
    const { type, stream, listenKey, source, payload, msgId } = event.data;

    switch (type) {
      // --- 行情与订阅指令 ---
      case 'SWITCH_SOURCE':
        if (source && source !== currentDataSource) {
          currentDataSource = source;
          connectPublicStream();
        }
        break;

      case 'SUBSCRIBE':
        console.log('Worker: SUBSCRIBE request for stream:', stream, 'currentDataSource:', currentDataSource);
        if (stream && !activeSubscriptions.has(stream)) {
          activeSubscriptions.add(stream);
          if (currentDataSource === 'backend' && signalRConnection?.state === signalR.HubConnectionState.Connected) {
            console.log('Worker: Invoking Subscribe on backend Hub for:', stream);
            signalRConnection.invoke("Subscribe", stream).catch(console.error);
          } else if (currentDataSource === 'binance' && publicWs?.readyState === WebSocket.OPEN) {
            console.log('Worker: Sending SUBSCRIBE to Binance WS for:', stream);
            publicWs.send(JSON.stringify({ method: 'SUBSCRIBE', params: [stream], id: Date.now() }));
          }
        } else if (stream && activeSubscriptions.has(stream)) {
          console.log('Worker: Already subscribed to stream, checking connection to force re-subscribe if needed');
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

      // --- 🌟 繁重计算指令 (带 msgId 原路返回) ---
      case 'CALCULATE_PEAKS':
        try {
          const { times, highs, lows, leftLen = 5, rightLen = 5 } = payload;
          const { peaks, valleys } = calculatePeaks(times, highs, lows, leftLen, rightLen);
          const lines = calculateTrendLines(peaks, valleys, times, highs, lows);
          // 仅向发起请求的 port 发送 SUCCESS 和对应的 msgId
          port.postMessage({ type: 'SUCCESS', msgId, payload: { peaks, valleys, lines } });
        } catch (err: any) {
          port.postMessage({ type: 'ERROR', msgId, payload: err.message });
        }
        break;

      case 'CALCULATE_VOL_REVERSAL':
        try {
          const { times, opens, closes, volumes, config } = payload;
          const markers = calculateVolReversal(times, opens, closes, volumes, config);
          port.postMessage({ type: 'SUCCESS', msgId, payload: { markers } });
        } catch (err: any) {
          port.postMessage({ type: 'ERROR', msgId, payload: err.message });
        }
        break;
    }
  };
};
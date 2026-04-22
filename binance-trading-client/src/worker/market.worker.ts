// src/worker/market.worker.ts

// 记录所有连接到此 SharedWorker 的浏览器标签页 (UI主线程)
const connectedPorts = new Set<MessagePort>();

// ==========================================
// 状态管理
// ==========================================
let publicWs: WebSocket | null = null;
let userDataWs: WebSocket | null = null;
let currentListenKey: string | null = null;

// 记录所有活跃的公共订阅流 (如 ethusdt@kline_1m)
const activeSubscriptions = new Set<string>();

// 默认必须订阅的全局流 (24h滚动行情 和 标记价格)
const defaultStreams = ['!miniTicker@arr', '!markPrice@arr@1s'];

// ==========================================
// 广播工具：向所有活跃的 UI 页面推送数据
// ==========================================
function broadcastToPorts(type: string, payload: any) {
  connectedPorts.forEach((port) => {
    port.postMessage({ type, payload });
  });
}

// ==========================================
// 1. 公共数据流 (K线、盘口、全局行情)
// ==========================================
function connectPublicWs() {
  if (publicWs && (publicWs.readyState === WebSocket.CONNECTING || publicWs.readyState === WebSocket.OPEN)) {
    return;
  }

  publicWs = new WebSocket('wss://fstream.binance.com/ws');

  publicWs.onopen = () => {
    console.log('✅ Worker: 公共数据流连接成功');
    
    // 连接成功后，把所有需要的流一次性订阅上
    const streamsToSubscribe = [...defaultStreams, ...Array.from(activeSubscriptions)];
    if (streamsToSubscribe.length > 0) {
      publicWs?.send(JSON.stringify({
        method: 'SUBSCRIBE',
        params: streamsToSubscribe,
        id: Date.now()
      }));
    }
  };

  publicWs.onmessage = (event) => {
    try {
      const payload = JSON.parse(event.data);
      
      // 路由分发逻辑：由于币安返回的格式不一，这里做统一梳理
      if (Array.isArray(payload)) {
        // Tickers 数组 (!miniTicker@arr)
        if (payload[0]?.e === '24hrMiniTicker') {
          broadcastToPorts('TICKERS_DATA', { stream: '!miniTicker@arr', data: payload });
        } 
        // Mark Price 数组 (!markPrice@arr)
        else if (payload[0]?.e === 'markPriceUpdate') {
          broadcastToPorts('TICKERS_DATA', { stream: '!markPrice@arr', data: payload });
        }
      } 
      // K线数据
      else if (payload.e === 'kline') {
        broadcastToPorts('KLINE_DATA', payload);
      }
    } catch (e) {
      console.error('解析公共流数据失败:', e);
    }
  };

  publicWs.onclose = () => {
    console.warn('⚠️ Worker: 公共数据流已断开，3秒后重连...');
    setTimeout(connectPublicWs, 3000);
  };

  publicWs.onerror = (error) => {
    console.error('❌ Worker: 公共数据流错误', error);
  };
}

// ==========================================
// 2. 私有数据流 (账户、持仓、订单)
// ==========================================
function connectUserDataStream() {
  if (!currentListenKey) return;
  
  if (userDataWs) {
    userDataWs.onclose = null; // 关闭旧连接，防止触发自动重连逻辑
    userDataWs.close();
  }

  // 使用主线程传进来的 ListenKey 建立私有连接
  userDataWs = new WebSocket(`wss://fstream.binance.com/ws/${currentListenKey}`);

  userDataWs.onopen = () => {
    console.log('✅ Worker: 账户私有流连接成功');
  };

  userDataWs.onmessage = (event) => {
    try {
      console.log('private channel onmessage');
      const data = JSON.parse(event.data);
      // 将账户更新(余额/持仓)、订单更新等统一下发给所有 UI 页面
      broadcastToPorts('ACCOUNT_DATA', data);
    } catch (err) {
      console.error('解析私有流数据失败:', err);
    }
  };

  userDataWs.onclose = () => {
    console.warn('⚠️ Worker: 账户私有流已断开，5秒后尝试重连...');
    setTimeout(connectUserDataStream, 5000);
  };

  userDataWs.onerror = (error) => {
    console.error('❌ Worker: 账户私有流发生错误', error);
  };
}

// ==========================================
// 3. UI 页面 (主线程) 通讯监听入口
// ==========================================
onconnect = (e: MessageEvent) => {
  const port = e.ports[0];
  connectedPorts.add(port);
  port.start();

  // 当有任何一个页面连接时，确保公共 WS 是连着的
  connectPublicWs();

  port.onmessage = (event) => {
    const { type, stream, listenKey } = event.data;

    switch (type) {
      // 处理订阅新 K 线或盘口
      case 'SUBSCRIBE':
        if (stream && !activeSubscriptions.has(stream)) {
          activeSubscriptions.add(stream);
          if (publicWs && publicWs.readyState === WebSocket.OPEN) {
            publicWs.send(JSON.stringify({
              method: 'SUBSCRIBE',
              params: [stream],
              id: Date.now()
            }));
          }
        }
        break;

      // 处理取消订阅
      case 'UNSUBSCRIBE':
        if (stream && activeSubscriptions.has(stream)) {
          activeSubscriptions.delete(stream);
          if (publicWs && publicWs.readyState === WebSocket.OPEN) {
            publicWs.send(JSON.stringify({
              method: 'UNSUBSCRIBE',
              params: [stream],
              id: Date.now()
            }));
          }
        }
        break;

      // 接收主线程传来的鉴权 Key，开启私有流
      case 'CONNECT_USER_DATA':
        if (listenKey) {
          currentListenKey = listenKey;
          connectUserDataStream();
        }
        break;

      // 当一个页面被关闭时
      case 'DISCONNECT':
        connectedPorts.delete(port);
        // 🌟 极限优化策略：如果用户把所有交易页签都关了，我们彻底关闭底层 WS 节省内存
        if (connectedPorts.size === 0) {
          if (publicWs) {
            publicWs.onclose = null; // 屏蔽自动重连
            publicWs.close();
            publicWs = null;
          }
          if (userDataWs) {
            userDataWs.onclose = null;
            userDataWs.close();
            userDataWs = null;
          }
          activeSubscriptions.clear();
        }
        break;
    }
  };
};
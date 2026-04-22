import { defineStore } from 'pinia'
import { ref, reactive } from 'vue'

export const useMarketStore = defineStore('market', () => {
  // ==========================================
  // 1. 核心数据源与基础状态
  // ==========================================
  const marketTickers = reactive<Record<string, any>>({});
  const latestKlines = reactive<Record<string, any>>({});
  // 在 market.ts 中新增：
  const symbolConfigs = ref<Record<string, { leverage: number, marginType: string }>>({});

  const currentSymbol = ref('BTCUSDT');
  const setCurrentSymbol = (symbol: string) => {
    if (currentSymbol.value === symbol) return;
    currentSymbol.value = symbol;
  };

  const usdtBalance = ref(0.00);

  // ==========================================
  // 2. 精度规则与 Click-to-Fill
  // ==========================================
  const symbolRules = ref<Record<string, { tickSize: string, stepSize: string }>>({});
  const fetchExchangeInfo = async () => {
    if (Object.keys(symbolRules.value).length > 0) return;
    try {
      const res = await fetch('http://localhost:5000/api/market/exchangeInfo');
      const data = await res.json();
      const rules: Record<string, { tickSize: string, stepSize: string }> = {};
      data.symbols.forEach((s: any) => {
        const priceFilter = s.filters.find((f: any) => f.filterType === 'PRICE_FILTER');
        const lotSize = s.filters.find((f: any) => f.filterType === 'LOT_SIZE');
        rules[s.symbol] = {
          tickSize: priceFilter?.tickSize || '0.01',
          stepSize: lotSize?.stepSize || '0.001'
        };
      });
      symbolRules.value = rules;
      console.log('✅ 币安精度规则加载完成!');
    } catch (e) {
      console.error('❌ 获取交易规则失败:', e);
    }
  };

  const fetchInitialPositions = async () => {
    try {
      // 调用后端我们之前补全的 positionRisk 接口
      const res = await fetch('http://localhost:5000/api/account/positionRisk');
      if (!res.ok) return;

      const riskData = await res.json();

      // 过滤出所有持仓量不为 0 的项目
      const activePositions = riskData
        .filter((r: any) => parseFloat(r.positionAmt) !== 0)
        .map((r: any) => {
          const amount = parseFloat(r.positionAmt);
          return {
            symbol: r.symbol,
            amount: amount,
            entryPrice: parseFloat(r.entryPrice),
            unrealizedPnL: parseFloat(r.unRealizedProfit),
            marginType: r.marginType,
            leverage: parseInt(r.leverage),
            side: amount > 0 ? 'LONG' : 'SHORT'
          };
        });

      // 🌟 将拉取到的全量数据存入响应式数组
      positions.value = activePositions;
      console.log(`✅ 已同步初始仓位: ${activePositions.length} 个`);
    } catch (e) {
      console.error('❌ 拉取初始仓位失败:', e);
    }
  };

  // 在 market.ts 中：
  const fetchInitialRiskConfig = async () => {
    try {
      const res = await fetch('http://localhost:5000/api/account/positionRisk');
      const riskData = await res.json();

      // 把拉取到的所有币种杠杆存进字典
      riskData.forEach((r: any) => {
        symbolConfigs.value[r.symbol] = {
          leverage: parseInt(r.leverage),
          marginType: r.marginType === 'cross' ? 'cross' : 'isolated'
        };

        // 如果有持仓，顺便给持仓也附加上杠杆
        const pos = positions.value.find(p => p.symbol === r.symbol && p.side === (parseFloat(r.positionAmt) > 0 ? 'LONG' : 'SHORT'));
        if (pos) pos.leverage = parseInt(r.leverage);
      });
    } catch (e) {
      console.error('获取初始风控参数失败', e);
    }
  };

  const clickedPrice = ref(0);
  const setClickedPrice = (price: number) => { clickedPrice.value = price; };

  // ==========================================
  // 3. 多窗口同步状态 (光标与画线)
  // ==========================================
  const isSyncEnabled = ref(false);
  const toggleSync = () => { isSyncEnabled.value = !isSyncEnabled.value; };

  // 🌟 全局时间戳，用于跨币种对齐 X 轴
  const globalCrosshairTime = ref(0);
  const crosshairData = reactive<Record<string, { price: number, time: number, sourceId: string }>>({});

  const updateGlobalCrosshair = (time: number, price: number, symbol: string, sourceId: string) => {
    globalCrosshairTime.value = time;
    crosshairData[symbol] = { price, time, sourceId };
  };
  const setCrosshair = (symbol: string, price: number, time: number, sourceId: string) => { crosshairData[symbol] = { price, time, sourceId }; };
  const clearCrosshair = (symbol: string, sourceId: string) => { crosshairData[symbol] = { price: 0, time: 0, sourceId }; };

  // 🌟 复杂画线(Overlay)同步总线
  const lastOverlayEvent = ref<{
    action: 'add' | 'clear';
    symbol: string;
    sourceId: string;
    data?: any;
  } | null>(null);

  const broadcastOverlay = (payload: any) => {
    lastOverlayEvent.value = payload;
  };

  const globalLines = reactive<Record<string, number[]>>({});
  const addGlobalLine = (symbol: string, price: number) => { if (!globalLines[symbol]) globalLines[symbol] = []; globalLines[symbol].push(price); };
  const clearGlobalLines = (symbol: string) => { globalLines[symbol] = []; };

  const globalChartType = reactive<Record<string, { type: string, sourceId: string }>>({});
  const setGlobalChartType = (symbol: string, type: string, sourceId: string) => { globalChartType[symbol] = { type, sourceId }; };

  // ==========================================
  // 4. SharedWorker 与 WS 状态心跳
  // ==========================================
  let worker: SharedWorker | null = null;
  const mySubscriptions = new Set<string>();

  // 🌟 WS 状态灯与心跳
  const wsStatus = ref<'DISCONNECTED' | 'CONNECTING' | 'CONNECTED'>('DISCONNECTED');
  let lastDataTimestamp = 0;

  const initWorker = () => {
    if (worker) return;
    wsStatus.value = 'CONNECTING';

    worker = new SharedWorker(new URL('../worker/market.worker.ts', import.meta.url), {
      type: 'module',
      name: 'MarketDataWorker'
    });

    worker.port.onmessage = (event) => {
      const { type, payload } = event.data;
      if (type === 'TICKERS_DATA') handleTickersData(payload);
      if (type === 'KLINE_DATA') handleKlineData(payload);
      if (type === 'ACCOUNT_DATA') handleAccountData(payload);
    };

    worker.port.start();

    // 🌟 每 2 秒检查一次心跳，超过 5 秒无数据视为断开
    setInterval(() => {
      const now = Date.now();
      if (lastDataTimestamp !== 0 && now - lastDataTimestamp > 5000) {
        wsStatus.value = 'DISCONNECTED';
      }
    }, 2000);

    window.addEventListener('beforeunload', () => {
      mySubscriptions.forEach(stream => worker?.port.postMessage({ type: 'UNSUBSCRIBE', stream }));
      worker?.port.postMessage({ type: 'DISCONNECT' });
    });
  };

  // 1. 新增持仓列表状态
  // ==========================================
  // 🌟 5. 私有数据流 (User Data) - HTTP 鉴权与下发
  // ==========================================
  const positions = ref<any[]>([]); // 持仓列表
  let listenKeyTimer: ReturnType<typeof setInterval> | null = null;

  // 获取 ListenKey 并通知 Worker 连接
  const connectUserDataStream = async () => {
    try {
      // 🌟 1. 核心修复：先通过 REST API 拿到初始余额
      // 这样下单控件和仓位控件在加载瞬间就有数据了
      const infoRes = await fetch('http://localhost:5000/api/account/info');
      if (infoRes.ok) {
        const accountData = await infoRes.json();
        // 找到 USDT 资产的可用余额
        const usdtAsset = accountData.assets?.find((a: any) => a.asset === 'USDT');
        if (usdtAsset) {
          // 使用 walletBalance (钱包余额) 或 availableBalance (可用余额)
          usdtBalance.value = parseFloat(usdtAsset.availableBalance || '0');
        }
      }
      // 1. 从 C# 后端获取 listenKey
      const res = await fetch('http://localhost:5000/api/account/listenKey', { method: 'POST' });
      const data = await res.json();
      const listenKey = data.listenKey;

      if (!listenKey) throw new Error('无法获取 ListenKey');

      // 2. 确保 Worker 已经初始化
      initWorker();

      // 3. 将 ListenKey 发送给 Worker，让 Worker 去建立 WS 连接
      worker?.port.postMessage({
        type: 'CONNECT_USER_DATA',
        listenKey: listenKey
      });

      // 4. 启动 HTTP 保活定时器 (每 30 分钟一次)
      startListenKeyKeepAlive();

    } catch (e) {
      console.error('❌ 初始化私有流失败:', e);
      // 失败则 5 秒后重试
      setTimeout(connectUserDataStream, 5000);
    }
    // WS 连上后，拉取一次静态底座数据
    await fetchInitialRiskConfig();
    await fetchInitialPositions();
  };

  // 维持 ListenKey 寿命
  const startListenKeyKeepAlive = () => {
    if (listenKeyTimer) clearInterval(listenKeyTimer);
    listenKeyTimer = setInterval(async () => {
      try {
        await fetch('http://localhost:5000/api/account/listenKey', { method: 'PUT' });
        console.log('💓 主线程: ListenKey 保活请求已发送');
      } catch (e) {
        console.error('💔 主线程: ListenKey 保活失败:', e);
      }
    }, 28 * 60 * 1000);
  };


  // ==========================================
  // 🌟 6. 历史仓位/成交记录管理 (已适配币安 API 限制)
  // ==========================================
  const positionHistory = ref<any[]>([]);
  const isLoadingHistory = ref(false);

  // 从 C# 后端拉取历史记录
  const fetchPositionHistory = async (symbol?: string, limit: number = 50) => {
    // 🚨 应对币安限制：如果没有明确指定币种，强制使用全局当前币种
    const targetSymbol = symbol || currentSymbol.value;

    if (!targetSymbol) return;

    isLoadingHistory.value = true;
    try {
      // 必须带上 symbol 才能成功请求后端
      const url = `http://localhost:5000/api/account/trades?limit=${limit}&symbol=${targetSymbol}`;

      const res = await fetch(url, { method: 'GET' });
      if (res.ok) {
        const data = await res.json();
        // 币安返回的直接就是历史成交数组
        positionHistory.value = data;
      } else {
        console.error('后端返回错误:', await res.text());
      }
    } catch (e) {
      console.error('获取历史记录失败:', e);
    } finally {
      isLoadingHistory.value = false;
    }
  };

  const handleTickersData = (payload: any) => {
    lastDataTimestamp = Date.now();
    if (wsStatus.value !== 'CONNECTED') wsStatus.value = 'CONNECTED';

    const stream = payload.stream;
    const data = payload.data;

    if (stream === '!miniTicker@arr') {
      for (const item of data) {
        const symbol = item.s;
        if (!symbol.endsWith('USDT')) continue;
        if (!marketTickers[symbol]) marketTickers[symbol] = { fundingRate: 0 };

        marketTickers[symbol].lastPrice = parseFloat(item.c);
        const openPrice = parseFloat(item.o);
        marketTickers[symbol].volume = parseFloat(item.q);
        marketTickers[symbol].priceChangePercent = ((marketTickers[symbol].lastPrice - openPrice) / openPrice) * 100;
      }
    } else if (stream.startsWith('!markPrice@arr')) {
      for (const item of data) {
        const symbol = item.s;
        if (!symbol.endsWith('USDT')) continue;
        if (!marketTickers[symbol]) marketTickers[symbol] = { lastPrice: 0, priceChangePercent: 0, volume: 0 };
        marketTickers[symbol].fundingRate = parseFloat(item.r) * 100;
      }
    }
  };

  // 在你原有的 handleAccountData 逻辑下方，补充之前讨论的逻辑：
  const handleAccountData = (payload: any) => {
    // 🌟 1. 拦截账户配置更新 (杠杆变化)
    if (payload.e === 'ACCOUNT_CONFIG_UPDATE') {
      const ac = payload.ac; // ac = account config
      if (ac && ac.s) {
        // ac.s: 币种, ac.l: 杠杆倍数
        if (!symbolConfigs.value[ac.s]) {
          symbolConfigs.value[ac.s] = { leverage: 1, marginType: 'cross' };
        }

        // 更新内存中的杠杆
        if (ac.l) symbolConfigs.value[ac.s].leverage = parseInt(ac.l);

        // 同步更新现有持仓列表里的杠杆显示，做到无缝渲染
        const pos = positions.value.find(p => p.symbol === ac.s);
        if (pos) pos.leverage = parseInt(ac.l);

        console.log(`🔧 ${ac.s} 杠杆已通过 WS 自动更新为: ${ac.l}x`);
      }
      return;
    }
    if (payload.e === 'ACCOUNT_UPDATE') {
      const balances = payload.a?.B;
      if (balances && Array.isArray(balances)) {
        const usdtAsset = balances.find((b: any) => b.a === 'USDT');
        if (usdtAsset) usdtBalance.value = parseFloat(usdtAsset.cw || usdtAsset.wb || '0');
      }

      const posData = payload.a?.P;
      if (posData && Array.isArray(posData)) {
        posData.forEach((p: any) => {
          const amount = parseFloat(p.pa);
          const existingIdx = positions.value.findIndex(pos => pos.symbol === p.s);

          if (amount !== 0) {
            const currentLeverage = symbolConfigs.value[p.s]?.leverage || 1;
            const newPos = {
              symbol: p.s,
              amount: amount,
              entryPrice: parseFloat(p.ep),
              unrealizedPnL: parseFloat(p.up),
              leverage: currentLeverage, // 把杠杆缝合进去
              marginType: p.mt,
              side: amount > 0 ? 'LONG' : 'SHORT',
              updateTime: payload.E || Date.now()
            };
            if (existingIdx > -1) positions.value[existingIdx] = newPos;
            else positions.value.push(newPos);
          } else if (existingIdx > -1) {
            positions.value.splice(existingIdx, 1);
          }
        });
      }
    }
  };

  const handleKlineData = (payload: any) => {
    lastDataTimestamp = Date.now();
    if (wsStatus.value !== 'CONNECTED') wsStatus.value = 'CONNECTED';

    const realData = payload.data ? payload.data : payload;
    if (realData && realData.e === 'kline') {
      const key = `${realData.s}_${realData.k.i}`;
      latestKlines[key] = {
        time: realData.k.t,
        open: parseFloat(realData.k.o),
        high: parseFloat(realData.k.h),
        low: parseFloat(realData.k.l),
        close: parseFloat(realData.k.c),
        volume: parseFloat(realData.k.q),
        isFinal: realData.k.x
      };
    }
  };

  const connectAllTickers = () => initWorker();
  const connectWs = () => initWorker();

  const subscribeKline = (symbol: string, interval: string) => {
    initWorker();
    const streamName = `${symbol.toLowerCase()}@kline_${interval}`;
    mySubscriptions.add(streamName);
    worker?.port.postMessage({ type: 'SUBSCRIBE', stream: streamName });
  }

  const unsubscribeKline = (symbol: string, interval: string) => {
    if (!worker) return;
    const streamName = `${symbol.toLowerCase()}@kline_${interval}`;
    mySubscriptions.delete(streamName);
    worker.port.postMessage({ type: 'UNSUBSCRIBE', stream: streamName });
  }

  return {
    marketTickers, latestKlines, connectAllTickers, connectWs, subscribeKline, unsubscribeKline,
    isSyncEnabled, toggleSync, crosshairData, setCrosshair, clearCrosshair,
    globalLines, addGlobalLine, clearGlobalLines, globalChartType, setGlobalChartType,
    usdtBalance, currentSymbol, setCurrentSymbol,
    symbolRules, fetchExchangeInfo, clickedPrice, setClickedPrice,
    lastOverlayEvent, broadcastOverlay, wsStatus, globalCrosshairTime, updateGlobalCrosshair,
    positions, connectUserDataStream, positionHistory, isLoadingHistory, fetchPositionHistory
  }
})
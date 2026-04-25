import { defineStore } from 'pinia'
import { ref, reactive } from 'vue'

export const useMarketStore = defineStore('market', () => {
  // ==========================================
  // 1. 核心数据源与基础状态
  // ==========================================
  const marketTickers = reactive<Record<string, any>>({});
  const latestKlines = reactive<Record<string, any>>({});
  const symbolConfigs = ref<Record<string, { leverage: number, marginType: string }>>({});

  const currentSymbol = ref('BTCUSDT');
  const setCurrentSymbol = (symbol: string) => {
    if (currentSymbol.value === symbol) return;
    currentSymbol.value = symbol;
  };

  const usdtBalance = ref(0.00);
  
  // 🌟 数据源状态 (UI 驱动 Worker 切换)
  const dataSource = ref<'binance' | 'backend'>('backend');

  // ==========================================
  // 2. 精度规则与 Click-to-Fill (保持原样)
  // ==========================================
  const symbolRules = ref<Record<string, { tickSize: string, stepSize: string }>>({});
  const fetchExchangeInfo = async () => { /* 略，同你原有代码 */ };
  
  const positions = ref<any[]>([]);
  const fetchInitialPositions = async () => { /* 略，同你原有代码 */ };
  const fetchInitialRiskConfig = async () => { /* 略，同你原有代码 */ };
  const clickedPrice = ref(0);
  const setClickedPrice = (price: number) => { clickedPrice.value = price; };

  // ==========================================
  // 3. 多窗口同步状态 (保持原样)
  // ==========================================
  const isSyncEnabled = ref(false);
  const toggleSync = () => { isSyncEnabled.value = !isSyncEnabled.value; };
  const globalCrosshairTime = ref(0);
  const crosshairData = reactive<Record<string, { price: number, time: number, sourceId: string }>>({});
  const updateGlobalCrosshair = (time: number, price: number, symbol: string, sourceId: string) => { globalCrosshairTime.value = time; crosshairData[symbol] = { price, time, sourceId }; };
  const setCrosshair = (symbol: string, price: number, time: number, sourceId: string) => { crosshairData[symbol] = { price, time, sourceId }; };
  const clearCrosshair = (symbol: string, sourceId: string) => { crosshairData[symbol] = { price: 0, time: 0, sourceId }; };
  const lastOverlayEvent = ref<any>(null);
  const broadcastOverlay = (payload: any) => { lastOverlayEvent.value = payload; };
  const globalLines = reactive<Record<string, number[]>>({});
  const addGlobalLine = (symbol: string, price: number) => { if (!globalLines[symbol]) globalLines[symbol] = []; globalLines[symbol].push(price); };
  const clearGlobalLines = (symbol: string) => { globalLines[symbol] = []; };
  const globalChartType = reactive<Record<string, { type: string, sourceId: string }>>({});
  const setGlobalChartType = (symbol: string, type: string, sourceId: string) => { globalChartType[symbol] = { type, sourceId }; };

  // ==========================================
  // 🌟 4. SharedWorker 调度与心跳 (完全恢复你的架构)
  // ==========================================
  let worker: SharedWorker | null = null;
  const mySubscriptions = new Set<string>();

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
    
    // 初始化时告诉 worker 当前配置的数据源
    worker.port.postMessage({ type: 'SWITCH_SOURCE', source: dataSource.value });

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

  // 🌟 暴露给 UI 的切换通道方法
  const switchDataSource = (source: 'binance' | 'backend') => {
    if (dataSource.value === source) return;
    dataSource.value = source;
    worker?.port.postMessage({ type: 'SWITCH_SOURCE', source });
  };

  // ==========================================
  // 5. 私有数据流初始化 (保持原样)
  // ==========================================
  let listenKeyTimer: ReturnType<typeof setInterval> | null = null;

  const connectUserDataStream = async () => {
    try {
      const infoRes = await fetch('http://localhost:5000/api/account/info');
      if (infoRes.ok) {
        const accountData = await infoRes.json();
        const usdtAsset = accountData.assets?.find((a: any) => a.asset === 'USDT');
        if (usdtAsset) usdtBalance.value = parseFloat(usdtAsset.availableBalance || '0');
      }

      const res = await fetch('http://localhost:5000/api/account/listenKey', { method: 'POST' });
      const data = await res.json();
      const listenKey = data.listenKey;

      if (!listenKey) throw new Error('无法获取 ListenKey');

      initWorker();
      worker?.port.postMessage({ type: 'CONNECT_USER_DATA', listenKey: listenKey });

      if (listenKeyTimer) clearInterval(listenKeyTimer);
      listenKeyTimer = setInterval(async () => {
        try { await fetch('http://localhost:5000/api/account/listenKey', { method: 'PUT' }); } 
        catch (e) { console.error('ListenKey 保活失败'); }
      }, 28 * 60 * 1000);

    } catch (e) {
      setTimeout(connectUserDataStream, 5000);
    }
    await fetchInitialRiskConfig();
    await fetchInitialPositions();
  };

  // ==========================================
  // 6. 数据处理路由 (解析 Worker 传来的清洗后数据)
  // ==========================================
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
        // Worker 已经统一了数据，我们直接使用
        marketTickers[symbol].priceChangePercent = item.P !== undefined ? parseFloat(item.P) : ((marketTickers[symbol].lastPrice - openPrice) / openPrice) * 100;
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

  const handleKlineData = (payload: any) => {
    lastDataTimestamp = Date.now();
    if (wsStatus.value !== 'CONNECTED') wsStatus.value = 'CONNECTED';

    const realData = payload.data ? payload.data : payload;
    if (realData && realData.e === 'kline') {
      const key = `${realData.s}_${realData.k.i}`;
      latestKlines[key] = {
        time: realData.k.t, open: parseFloat(realData.k.o), high: parseFloat(realData.k.h),
        low: parseFloat(realData.k.l), close: parseFloat(realData.k.c), volume: parseFloat(realData.k.q),
        isFinal: realData.k.x
      };
    }
  };

  const handleAccountData = (payload: any) => {
    // 逻辑和之前完全一致，直接解析 payload 更新 positions 和 balances
    if (payload.e === 'ACCOUNT_CONFIG_UPDATE') {
      const ac = payload.ac; 
      if (ac && ac.s) {
        if (!symbolConfigs.value[ac.s]) symbolConfigs.value[ac.s] = { leverage: 1, marginType: 'cross' };
        if (ac.l) symbolConfigs.value[ac.s].leverage = parseInt(ac.l);
        const pos = positions.value.find(p => p.symbol === ac.s);
        if (pos) pos.leverage = parseInt(ac.l);
      }
      return;
    }
    if (payload.e === 'ACCOUNT_UPDATE') {
      const balances = payload.a?.B;
      if (balances) {
        const usdtAsset = balances.find((b: any) => b.a === 'USDT');
        if (usdtAsset) usdtBalance.value = parseFloat(usdtAsset.cw || usdtAsset.wb || '0');
      }
      const posData = payload.a?.P;
      if (posData) {
        posData.forEach((p: any) => {
          const amount = parseFloat(p.pa);
          const existingIdx = positions.value.findIndex(pos => pos.symbol === p.s);
          if (amount !== 0) {
            const currentLeverage = symbolConfigs.value[p.s]?.leverage || 1;
            const newPos = { symbol: p.s, amount, entryPrice: parseFloat(p.ep), unrealizedPnL: parseFloat(p.up), leverage: currentLeverage, marginType: p.mt, side: amount > 0 ? 'LONG' : 'SHORT', updateTime: payload.E || Date.now() };
            if (existingIdx > -1) positions.value[existingIdx] = newPos;
            else positions.value.push(newPos);
          } else if (existingIdx > -1) {
            positions.value.splice(existingIdx, 1);
          }
        });
      }
    }
  };

  // ==========================================
  // 7. 对外接口暴露
  // ==========================================
  const positionHistory = ref<any[]>([]);
  const isLoadingHistory = ref(false);
  const fetchPositionHistory = async (symbol?: string, limit: number = 50) => { /* 略 */ };

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
    positions, connectUserDataStream, positionHistory, isLoadingHistory, fetchPositionHistory,
    dataSource, switchDataSource // 🌟 暴露切换开关
  }
})
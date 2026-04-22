import { defineStore } from 'pinia'
import { ref, reactive } from 'vue'

export const useMarketStore = defineStore('market', () => {
  // ==========================================
  // 1. 核心数据源与基础状态
  // ==========================================
  const marketTickers = reactive<Record<string, any>>({});
  const latestKlines = reactive<Record<string, any>>({});
  
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

  const handleAccountData = (payload: any) => {
    if (payload.e === 'ACCOUNT_UPDATE') {
      const balances = payload.a?.B; 
      if (balances && Array.isArray(balances)) {
        const usdtAsset = balances.find((b: any) => b.a === 'USDT');
        if (usdtAsset) {
          usdtBalance.value = parseFloat(usdtAsset.cw || usdtAsset.wb || '0');
        }
      }
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
    lastOverlayEvent, broadcastOverlay, wsStatus, globalCrosshairTime, updateGlobalCrosshair
  }
})
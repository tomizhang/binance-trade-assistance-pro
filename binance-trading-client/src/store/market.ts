import { defineStore } from 'pinia'
import { ref, reactive } from 'vue'

export const useMarketStore = defineStore('market', () => {
  // 在 useMarketStore 的开头部分增加：
  const wsStatus = ref<'DISCONNECTED' | 'CONNECTING' | 'CONNECTED'>('DISCONNECTED');

    // ==========================================
  // 核心数据源 (保持不变，确保 Vue 视图无缝兼容)
  // ==========================================
  const marketTickers = reactive<Record<string, any>>({})
  const latestKlines = reactive<Record<string, any>>({})
  // 🌟 1. 新增：全局当前选中的交易对 (默认 BTCUSDT)
  const currentSymbol = ref('BTCUSDT');

  // 多窗口同步状态 (保持不变)
  const isSyncEnabled = ref(false)
  const toggleSync = () => { isSyncEnabled.value = !isSyncEnabled.value }
  const crosshairData = reactive<Record<string, { price: number, time: number, sourceId: string }>>({})
  const setCrosshair = (symbol: string, price: number, time: number, sourceId: string) => { crosshairData[symbol] = { price, time, sourceId } }
  const clearCrosshair = (symbol: string, sourceId: string) => { crosshairData[symbol] = { price: 0, time: 0, sourceId } }
  const globalLines = reactive<Record<string, number[]>>({})
  const addGlobalLine = (symbol: string, price: number) => { if (!globalLines[symbol]) globalLines[symbol] = []; globalLines[symbol].push(price) }
  const clearGlobalLines = (symbol: string) => { globalLines[symbol] = [] }
  const globalChartType = reactive<Record<string, { type: string, sourceId: string }>>({})
  const setGlobalChartType = (symbol: string, type: string, sourceId: string) => { globalChartType[symbol] = { type, sourceId } }

  // ==========================================
  // 🌟 补充 1：用于 Click-to-Fill 的点击价格状态
  // ==========================================
  const clickedPrice = ref(0);
  const setClickedPrice = (price: number) => {
    clickedPrice.value = price;
  };

  // ==========================================
  // 🌟 补充 2：用于 klinecharts 复杂画线(Overlay)的同步总线
  // ==========================================
  const lastOverlayEvent = ref<{
    action: 'add' | 'clear';
    symbol: string;
    sourceId: string;
    data?: any;
  } | null>(null);

  const broadcastOverlay = (payload: any) => {
    lastOverlayEvent.value = payload;
  };

  // ==========================================
  // 🌟 新增：SharedWorker 调度中心
  // ==========================================
  let worker: SharedWorker | null = null
  const mySubscriptions = new Set<string>() // 记录当前 Tab 订阅的流，用于关闭时清理

  // 🌟 新增：账户余额状态
  const usdtBalance = ref(0.00);

  const initWorker = () => {
    if (worker) return;
    worker = new SharedWorker(new URL('../worker/market.worker.ts', import.meta.url), {
      type: 'module',
      name: 'MarketDataWorker'
    });

    worker.port.onmessage = (event) => {
      const { type, payload } = event.data;
      if (type === 'TICKERS_DATA') handleTickersData(payload);
      if (type === 'KLINE_DATA') handleKlineData(payload);
      // 🌟 拦截账户数据
      if (type === 'ACCOUNT_DATA') handleAccountData(payload);
    };

    worker.port.start();

    // 当用户关闭当前标签页或刷新时，告诉 Worker 撤销我的订阅
    window.addEventListener('beforeunload', () => {
      mySubscriptions.forEach(stream => {
        worker?.port.postMessage({ type: 'UNSUBSCRIBE', stream });
      });
      worker?.port.postMessage({ type: 'DISCONNECT' });
    });
  };

  // 🌟 2. 新增：切换交易对的方法
  const setCurrentSymbol = (symbol: string) => {
    if (currentSymbol.value === symbol) return;
    currentSymbol.value = symbol;

    // (可选) 如果你以后做了 K线和盘口的 WebSocket 订阅管理，
    // 可以直接在这里触发：断开旧币种 -> 订阅新币种 的逻辑
    // connectWs(symbol); 
  };

  // 🌟 核心：解析币安复杂的账户推送结构
  const handleAccountData = (payload: any) => {
    // 判断是不是账户更新事件
    if (payload.e === 'ACCOUNT_UPDATE') {
      const balances = payload.a?.B; // a: account, B: balances 数组
      if (balances && Array.isArray(balances)) {
        // 找出 USDT 的资产信息
        const usdtAsset = balances.find((b: any) => b.a === 'USDT');
        if (usdtAsset) {
          // cw 是跨仓钱包余额 (Cross Wallet Balance)，wb 是钱包余额 (Wallet Balance)
          usdtBalance.value = parseFloat(usdtAsset.cw || usdtAsset.wb || '0');
        }
      }
    }
    // 如果你要做订单历史流，还可以在这里拦截 payload.e === 'ORDER_TRADE_UPDATE'
  };

  // ==========================================
  // 数据解析器 (逻辑从原来的 WS 里原封不动搬过来)
  // ==========================================
  const handleTickersData = (payload: any) => {
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

  // ==========================================
  // 对外暴露的方法 (变成向 Worker 发送指令)
  // ==========================================
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

  // 🌟 1. 新增：存储各个币种的精度规则
  const symbolRules = ref<Record<string, { tickSize: string, stepSize: string }>>({});

  // 🌟 2. 新增：从 C# 后端拉取币安规则
  const fetchExchangeInfo = async () => {
    // 如果已经加载过了，就不重复拉取
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

  return {
    symbolRules, fetchExchangeInfo,
    marketTickers, latestKlines, connectAllTickers, connectWs, subscribeKline, unsubscribeKline,
    isSyncEnabled, toggleSync, crosshairData, setCrosshair, clearCrosshair,
    globalLines, addGlobalLine, clearGlobalLines, globalChartType, setGlobalChartType,
    usdtBalance,// 🌟 暴露出余额给组件用
    currentSymbol,
    setCurrentSymbol,
    lastOverlayEvent,
    broadcastOverlay,
    wsStatus
  }
})
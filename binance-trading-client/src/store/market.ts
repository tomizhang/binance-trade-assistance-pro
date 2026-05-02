import { defineStore } from 'pinia'
import { ref, reactive, computed } from 'vue'
import { useToast } from '@/utils/useToast'; 
// 🌟 1. 引入全局通知 Store
import { useNotificationStore } from '@/store/notification'; 

export const useMarketStore = defineStore('market', () => {
  const toast = useToast();

  const marketTickers = reactive<Record<string, any>>({});
  const latestKlines = reactive<Record<string, any>>({});
  const symbolConfigs = ref<Record<string, { leverage: number, marginType: string }>>({});
  const backendLatency = ref(0); 
  const currentSymbol = ref('BTCUSDT');
  const setCurrentSymbol = (symbol: string) => {
    if (currentSymbol.value === symbol) return;
    currentSymbol.value = symbol;
  };

  const usdtBalance = ref(0.00);
  const dataSource = ref<'binance' | 'backend'>('backend');
  const positions = ref<any[]>([]); 

  const symbolRules = ref<Record<string, { tickSize: string, stepSize: string }>>({});
  const fetchExchangeInfo = async () => {
    if (Object.keys(symbolRules.value).length > 0) return;
    try {
      const res = await fetch(`${import.meta.env.VITE_API_BASE_URL}/api/market/exchangeInfo`);
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
      isInitialized.value = true;
      console.log('✅ 币安精度规则加载完成!');
    } catch (e) {
      console.error('❌ 获取交易规则失败:', e);
    }
  };

  const openOrders = ref<any[]>([]);

  const fetchOpenOrders = async (symbol?: string) => {
    try {
      let url = `${import.meta.env.VITE_API_BASE_URL}/api/order/openOrders`;
      if (symbol) url += `?symbol=${symbol}`;

      const res = await fetch(url);
      const data = await res.json();

      if (res.ok) {
        const rawOrders = Array.isArray(data) ? data : (data.data || []);

        openOrders.value = rawOrders.map((o: any) => ({
          ...o,
          orderId: o.algoId || o.orderId || o.clientOrderId, 
          type: o.orderType || o.type,                       
          stopPrice: o.triggerPrice || o.stopPrice,          
          origQty: o.origQty || o.quantity                   
        }));
      }
    } catch (error) {
      console.error('获取挂单失败:', error);
    }
  };

  const isInitialized = ref(false); 

  const isReady = computed(() => {
    const hasRules = Object.keys(symbolRules.value).length > 0;
    const hasConfigs = Object.keys(symbolConfigs.value).length > 0;
    const hasBalance = usdtBalance.value !== 0; 
    const isConnected = wsStatus.value === 'CONNECTED';

    return hasRules && hasConfigs && isConnected;
  });

  const fetchInitialPositions = async () => {
    try {
      const res = await fetch(`${import.meta.env.VITE_API_BASE_URL}/api/account/positionRisk`);
      if (!res.ok) return;

      const riskData = await res.json();

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

      positions.value = activePositions;
      console.log(`✅ 已同步初始仓位: ${activePositions.length} 个`);
    } catch (e) {
      console.error('❌ 拉取初始仓位失败:', e);
    }
  };

  const fetchInitialRiskConfig = async () => {
    try {
      const res = await fetch(`${import.meta.env.VITE_API_BASE_URL}/api/account/positionRisk`);
      const riskData = await res.json();

      riskData.forEach((r: any) => {
        symbolConfigs.value[r.symbol] = {
          leverage: parseInt(r.leverage),
          marginType: r.marginType === 'cross' ? 'cross' : 'isolated'
        };

        const pos = positions.value.find(p => p.symbol === r.symbol && p.side === (parseFloat(r.positionAmt) > 0 ? 'LONG' : 'SHORT'));
        if (pos) pos.leverage = parseInt(r.leverage);
      });
    } catch (e) {
      console.error('获取初始风控参数失败', e);
    }
  };
  const clickedPrice = ref(0);
  const setClickedPrice = (price: number) => { clickedPrice.value = price; };

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

  const strategyAlerts = ref<any[]>([]);

  let worker: SharedWorker | null = null;

  const mySubscriptions = new Map<string, number>();
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
      
      // 🌟 在这里动态获取通知 Store，避免 Pinia 的提前初始化问题
      const notificationStore = useNotificationStore();

      if (type === 'TICKERS_DATA') handleTickersData(payload);
      else if (type === 'KLINE_DATA') handleKlineData(payload);
      else if (type === 'ACCOUNT_DATA') handleAccountData(payload);
      else if (type === 'BACKEND_LATENCY') backendLatency.value = payload;
      else if (type === 'STRATEGY_ALERT') {
        console.log('🚨 [前端雷达] 捕获主力异动:', payload);

        // 原有逻辑：存入历史记录
        strategyAlerts.value.unshift(payload);
        if (strategyAlerts.value.length > 50) strategyAlerts.value.pop();

        // 原有逻辑：触发全局极速弹窗 Toast
        const symbol = payload.symbol;
        const signalType = payload.signalType; 
        const volX = payload.volMultiplier.toFixed(1);
        const oiPct = payload.oiChange.toFixed(2);

        if (signalType === 'StrongLong') {
          toast.success(`🔥 狙击提醒: ${symbol} 主力爆量做多! (量:${volX}x, OI:+${oiPct}%)`, 8000);
        } else if (signalType === 'StrongShort') {
          toast.error(`🩸 狙击提醒: ${symbol} 主力爆量砸盘! (量:${volX}x, OI:+${oiPct}%)`, 8000);
        } else if (signalType === 'ShortCovering') {
          toast.info(`⚠️ 注意反转: ${symbol} 空头被动平仓拉升 (量:${volX}x, OI:${oiPct}%)`, 5000);
        } else if (signalType === 'LongLiquidation') {
          toast.info(`⚠️ 注意反转: ${symbol} 多头连环踩踏下跌 (量:${volX}x, OI:${oiPct}%)`, 5000 );
        }

        // 🌟 新增逻辑：同步写入右侧全局通知抽屉
        notificationStore.addAlert({
          type: 'STRATEGY_ALERT',
          title: `🚨 主力异动: ${symbol}`,
          content: `检测到 ${signalType}，量能放大 ${volX} 倍！`,
          timestamp: payload.timestamp || Date.now(),
          symbol: symbol
        });
      }
      else if (type === 'HA_ALERT') {
        // 🌟 新增 HA_ALERT 处理逻辑
        console.log(`%c 🚨 [HA反转警报] ${payload.symbol} ${payload.timeframe}`, 'color: #d29922; font-weight: bold;');

        // 写入右侧全局通知抽屉
        notificationStore.addAlert({
          type: 'HA_REVERSAL',
          title: `📡 HA 趋势反转: ${payload.symbol}`,
          content: `${payload.symbol} 在 ${payload.timeframe} 级别发生了平均 K 线方向反转，请留意波段机会！\n 📡 趋势反转: ${payload.symbol} ${payload.timeframe} 级别转为${payload.direction}，建议 ${payload.action} !`,
          timestamp: payload.timestamp || Date.now(),
          symbol: payload.symbol
        });

        // 可选：同样弹出一个 Toast 提示
        // toast.warning(`📡 趋势反转: ${payload.symbol} ${payload.timeframe} 级别 HA 反转!`, 6000);
      }
    };

    worker.port.start();
    worker.port.postMessage({ type: 'SWITCH_SOURCE', source: dataSource.value });

    setInterval(() => {
      const now = Date.now();
      if (lastDataTimestamp !== 0 && now - lastDataTimestamp > 5000) {
        wsStatus.value = 'DISCONNECTED';
      }
    }, 2000);

    window.addEventListener('beforeunload', () => {
      mySubscriptions.forEach((count, stream) => {
        if (count > 0) worker?.port.postMessage({ type: 'UNSUBSCRIBE', stream });
      });
      worker?.port.postMessage({ type: 'DISCONNECT' });
    });
  };

  const switchDataSource = (source: 'binance' | 'backend') => {
    if (dataSource.value === source) return;
    dataSource.value = source;
    worker?.port.postMessage({ type: 'SWITCH_SOURCE', source });
  };

  let listenKeyTimer: ReturnType<typeof setInterval> | null = null;

  const connectUserDataStream = async () => {
    try {
      await refreshAccountBalance();
      const infoRes = await fetch(`${import.meta.env.VITE_API_BASE_URL}/api/account/info`);
      if (infoRes.ok) {
        const accountData = await infoRes.json();
        const usdtAsset = accountData.assets?.find((a: any) => a.asset === 'USDT');
        if (usdtAsset) usdtBalance.value = parseFloat(usdtAsset.availableBalance || '0');
      }

      const res = await fetch(`${import.meta.env.VITE_API_BASE_URL}/api/account/listenKey`, { method: 'POST' });
      const data = await res.json();
      const listenKey = data.listenKey;

      if (!listenKey) throw new Error('无法获取 ListenKey');

      initWorker();
      worker?.port.postMessage({ type: 'CONNECT_USER_DATA', listenKey: listenKey });

      if (listenKeyTimer) clearInterval(listenKeyTimer);
      listenKeyTimer = setInterval(async () => {
        try { fetch(`${import.meta.env.VITE_API_BASE_URL}/api/account/listenKey`, { method: 'PUT' }); }
        catch (e) { console.error('ListenKey 保活失败'); }
      }, 28 * 60 * 1000);
    } catch (e) {
      setTimeout(connectUserDataStream, 5000);
    }
    await fetchInitialRiskConfig();
    await fetchInitialPositions();
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
      refreshAccountBalance();
      fetchOpenOrders();
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

  const positionHistory = ref<any[]>([]);
  const isLoadingHistory = ref(false);
  const fetchPositionHistory = async (symbol?: string, limit: number = 50) => {
    const targetSymbol = symbol || currentSymbol.value;

    if (!targetSymbol) return;

    isLoadingHistory.value = true;
    try {
      const url = `${import.meta.env.VITE_API_BASE_URL}/api/account/trades?limit=${limit}&symbol=${targetSymbol}`;

      const res = await fetch(url, { method: 'GET' });
      if (res.ok) {
        const data = await res.json();
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

  const connectAllTickers = () => initWorker();
  const connectWs = () => initWorker();

  const subscribeKline = (symbol: string, interval: string) => {
    initWorker();
    const streamName = `${symbol.toLowerCase()}@kline_${interval}`;
    const currentCount = mySubscriptions.get(streamName) || 0;

    mySubscriptions.set(streamName, currentCount + 1);

    if (currentCount === 0) {
      worker?.port.postMessage({ type: 'SUBSCRIBE', stream: streamName });
    }
  };

  const unsubscribeKline = (symbol: string, interval: string) => {
    if (!worker) return;
    const streamName = `${symbol.toLowerCase()}@kline_${interval}`;
    const currentCount = mySubscriptions.get(streamName) || 0;

    if (currentCount > 0) {
      const newCount = currentCount - 1;
      if (newCount === 0) {
        mySubscriptions.delete(streamName);
        worker.port.postMessage({ type: 'UNSUBSCRIBE', stream: streamName });
      } else {
        mySubscriptions.set(streamName, newCount);
      }
    }
  };

  const baseAvailableBalance = ref(0); 
  const baseTotalUnrealizedPnl = ref(0); 

  const refreshAccountBalance = async () => {
    const res = await fetch(`${import.meta.env.VITE_API_BASE_URL}/api/account/info`);
    const accountData = await res.json();
    const usdtAsset = accountData.assets?.find((a: any) => a.asset === 'USDT');

    if (usdtAsset) {
      baseAvailableBalance.value = parseFloat(usdtAsset.availableBalance || '0');
      baseTotalUnrealizedPnl.value = parseFloat(usdtAsset.crossUnPnl || '0');
    }
  };

  const dynamicUsdtBalance = computed(() => {
    let currentTotalCrossPnl = 0;
    positions.value.forEach(pos => {
      if (pos.marginType === 'cross') {
        const currentPrice = marketTickers[pos.symbol]?.lastPrice || pos.entryPrice;
        const amount = Math.abs(pos.amount);
        const pnl = pos.side === 'LONG'
          ? (currentPrice - pos.entryPrice) * amount
          : (pos.entryPrice - currentPrice) * amount;
        currentTotalCrossPnl += pnl;
      }
    });

    const realTimeBalance = baseAvailableBalance.value + (currentTotalCrossPnl - baseTotalUnrealizedPnl.value);

    return Math.max(0, realTimeBalance);
  });

  return {
    marketTickers, latestKlines, connectAllTickers, connectWs, subscribeKline, unsubscribeKline,
    isSyncEnabled, toggleSync, crosshairData, setCrosshair, clearCrosshair,
    globalLines, addGlobalLine, clearGlobalLines, globalChartType, setGlobalChartType,
    usdtBalance, currentSymbol, setCurrentSymbol,
    symbolRules, fetchExchangeInfo, clickedPrice, setClickedPrice,
    lastOverlayEvent, broadcastOverlay, wsStatus, globalCrosshairTime, updateGlobalCrosshair,
    positions, connectUserDataStream, positionHistory, isLoadingHistory, fetchPositionHistory,
    dataSource, switchDataSource, symbolConfigs, dynamicUsdtBalance, backendLatency, isReady,
    isInitialized, openOrders,
    fetchOpenOrders,strategyAlerts
  }
})
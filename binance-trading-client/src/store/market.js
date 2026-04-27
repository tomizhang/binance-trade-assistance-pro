import { defineStore } from 'pinia';
import { ref, reactive, computed } from 'vue';
export const useMarketStore = defineStore('market', () => {
    const marketTickers = reactive({});
    const latestKlines = reactive({});
    const symbolConfigs = ref({});
    const backendLatency = ref(0); // 🌟 新增：后端到币安的延迟
    const currentSymbol = ref('BTCUSDT');
    const setCurrentSymbol = (symbol) => {
        if (currentSymbol.value === symbol)
            return;
        currentSymbol.value = symbol;
    };
    const usdtBalance = ref(0.00);
    const dataSource = ref('backend');
    const positions = ref([]); // 持仓列
    const symbolRules = ref({});
    const fetchExchangeInfo = async () => {
        if (Object.keys(symbolRules.value).length > 0)
            return;
        try {
            const res = await fetch('http://localhost:5000/api/market/exchangeInfo');
            const data = await res.json();
            const rules = {};
            data.symbols.forEach((s) => {
                const priceFilter = s.filters.find((f) => f.filterType === 'PRICE_FILTER');
                const lotSize = s.filters.find((f) => f.filterType === 'LOT_SIZE');
                rules[s.symbol] = {
                    tickSize: priceFilter?.tickSize || '0.01',
                    stepSize: lotSize?.stepSize || '0.001'
                };
            });
            symbolRules.value = rules;
            console.log('✅ 币安精度规则加载完成!');
        }
        catch (e) {
            console.error('❌ 获取交易规则失败:', e);
        }
    };
    const fetchInitialPositions = async () => {
        try {
            // 调用后端我们之前补全的 positionRisk 接口
            const res = await fetch('http://localhost:5000/api/account/positionRisk');
            if (!res.ok)
                return;
            const riskData = await res.json();
            // 过滤出所有持仓量不为 0 的项目
            const activePositions = riskData
                .filter((r) => parseFloat(r.positionAmt) !== 0)
                .map((r) => {
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
        }
        catch (e) {
            console.error('❌ 拉取初始仓位失败:', e);
        }
    };
    // 在 market.ts 中：
    const fetchInitialRiskConfig = async () => {
        try {
            const res = await fetch('http://localhost:5000/api/account/positionRisk');
            const riskData = await res.json();
            // 把拉取到的所有币种杠杆存进字典
            riskData.forEach((r) => {
                symbolConfigs.value[r.symbol] = {
                    leverage: parseInt(r.leverage),
                    marginType: r.marginType === 'cross' ? 'cross' : 'isolated'
                };
                // 如果有持仓，顺便给持仓也附加上杠杆
                const pos = positions.value.find(p => p.symbol === r.symbol && p.side === (parseFloat(r.positionAmt) > 0 ? 'LONG' : 'SHORT'));
                if (pos)
                    pos.leverage = parseInt(r.leverage);
            });
        }
        catch (e) {
            console.error('获取初始风控参数失败', e);
        }
    };
    const clickedPrice = ref(0);
    const setClickedPrice = (price) => { clickedPrice.value = price; };
    const isSyncEnabled = ref(false);
    const toggleSync = () => { isSyncEnabled.value = !isSyncEnabled.value; };
    const globalCrosshairTime = ref(0);
    const crosshairData = reactive({});
    const updateGlobalCrosshair = (time, price, symbol, sourceId) => { globalCrosshairTime.value = time; crosshairData[symbol] = { price, time, sourceId }; };
    const setCrosshair = (symbol, price, time, sourceId) => { crosshairData[symbol] = { price, time, sourceId }; };
    const clearCrosshair = (symbol, sourceId) => { crosshairData[symbol] = { price: 0, time: 0, sourceId }; };
    const lastOverlayEvent = ref(null);
    const broadcastOverlay = (payload) => { lastOverlayEvent.value = payload; };
    const globalLines = reactive({});
    const addGlobalLine = (symbol, price) => { if (!globalLines[symbol])
        globalLines[symbol] = []; globalLines[symbol].push(price); };
    const clearGlobalLines = (symbol) => { globalLines[symbol] = []; };
    const globalChartType = reactive({});
    const setGlobalChartType = (symbol, type, sourceId) => { globalChartType[symbol] = { type, sourceId }; };
    // ==========================================
    // 🌟 核心升级：Worker 调度与引用计数
    // ==========================================
    let worker = null;
    // 使用 Map 记录流的订阅次数
    const mySubscriptions = new Map();
    const wsStatus = ref('DISCONNECTED');
    let lastDataTimestamp = 0;
    const initWorker = () => {
        if (worker)
            return;
        wsStatus.value = 'CONNECTING';
        worker = new SharedWorker(new URL('../worker/market.worker.ts', import.meta.url), {
            type: 'module',
            name: 'MarketDataWorker'
        });
        worker.port.onmessage = (event) => {
            const { type, payload } = event.data;
            if (type === 'TICKERS_DATA')
                handleTickersData(payload);
            if (type === 'KLINE_DATA')
                handleKlineData(payload);
            if (type === 'ACCOUNT_DATA')
                handleAccountData(payload);
            if (type === 'BACKEND_LATENCY')
                backendLatency.value = payload;
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
            // 页面关闭时，只清理有计数的订阅
            mySubscriptions.forEach((count, stream) => {
                if (count > 0)
                    worker?.port.postMessage({ type: 'UNSUBSCRIBE', stream });
            });
            worker?.port.postMessage({ type: 'DISCONNECT' });
        });
    };
    const switchDataSource = (source) => {
        if (dataSource.value === source)
            return;
        dataSource.value = source;
        worker?.port.postMessage({ type: 'SWITCH_SOURCE', source });
    };
    let listenKeyTimer = null;
    const connectUserDataStream = async () => {
        try {
            await refreshAccountBalance();
            const infoRes = await fetch(`${import.meta.env.VITE_API_BASE_URL}/api/account/info`);
            if (infoRes.ok) {
                const accountData = await infoRes.json();
                const usdtAsset = accountData.assets?.find((a) => a.asset === 'USDT');
                if (usdtAsset)
                    usdtBalance.value = parseFloat(usdtAsset.availableBalance || '0');
            }
            const res = await fetch(`${import.meta.env.VITE_API_BASE_URL}/api/account/listenKey`, { method: 'POST' });
            const data = await res.json();
            const listenKey = data.listenKey;
            if (!listenKey)
                throw new Error('无法获取 ListenKey');
            initWorker();
            worker?.port.postMessage({ type: 'CONNECT_USER_DATA', listenKey: listenKey });
            if (listenKeyTimer)
                clearInterval(listenKeyTimer);
            listenKeyTimer = setInterval(async () => {
                try {
                    fetch(`${import.meta.env.VITE_API_BASE_URL}/api/account/listenKey`, { method: 'PUT' });
                }
                catch (e) {
                    console.error('ListenKey 保活失败');
                }
            }, 28 * 60 * 1000);
        }
        catch (e) {
            setTimeout(connectUserDataStream, 5000);
        }
        await fetchInitialRiskConfig();
        await fetchInitialPositions();
    };
    const handleTickersData = (payload) => {
        lastDataTimestamp = Date.now();
        if (wsStatus.value !== 'CONNECTED')
            wsStatus.value = 'CONNECTED';
        const stream = payload.stream;
        const data = payload.data;
        if (stream === '!miniTicker@arr') {
            for (const item of data) {
                const symbol = item.s;
                if (!symbol.endsWith('USDT'))
                    continue;
                if (!marketTickers[symbol])
                    marketTickers[symbol] = { fundingRate: 0 };
                marketTickers[symbol].lastPrice = parseFloat(item.c);
                const openPrice = parseFloat(item.o);
                marketTickers[symbol].volume = parseFloat(item.q);
                marketTickers[symbol].priceChangePercent = item.P !== undefined ? parseFloat(item.P) : ((marketTickers[symbol].lastPrice - openPrice) / openPrice) * 100;
            }
        }
        else if (stream.startsWith('!markPrice@arr')) {
            for (const item of data) {
                const symbol = item.s;
                if (!symbol.endsWith('USDT'))
                    continue;
                if (!marketTickers[symbol])
                    marketTickers[symbol] = { lastPrice: 0, priceChangePercent: 0, volume: 0 };
                marketTickers[symbol].fundingRate = parseFloat(item.r) * 100;
            }
        }
    };
    const handleKlineData = (payload) => {
        lastDataTimestamp = Date.now();
        if (wsStatus.value !== 'CONNECTED')
            wsStatus.value = 'CONNECTED';
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
    const handleAccountData = (payload) => {
        if (payload.e === 'ACCOUNT_CONFIG_UPDATE') {
            const ac = payload.ac;
            if (ac && ac.s) {
                if (!symbolConfigs.value[ac.s])
                    symbolConfigs.value[ac.s] = { leverage: 1, marginType: 'cross' };
                if (ac.l)
                    symbolConfigs.value[ac.s].leverage = parseInt(ac.l);
                const pos = positions.value.find(p => p.symbol === ac.s);
                if (pos)
                    pos.leverage = parseInt(ac.l);
            }
            return;
        }
        if (payload.e === 'ACCOUNT_UPDATE') {
            const balances = payload.a?.B;
            if (balances) {
                const usdtAsset = balances.find((b) => b.a === 'USDT');
                if (usdtAsset)
                    usdtBalance.value = parseFloat(usdtAsset.cw || usdtAsset.wb || '0');
            }
            refreshAccountBalance();
            const posData = payload.a?.P;
            if (posData) {
                posData.forEach((p) => {
                    const amount = parseFloat(p.pa);
                    const existingIdx = positions.value.findIndex(pos => pos.symbol === p.s);
                    if (amount !== 0) {
                        const currentLeverage = symbolConfigs.value[p.s]?.leverage || 1;
                        const newPos = { symbol: p.s, amount, entryPrice: parseFloat(p.ep), unrealizedPnL: parseFloat(p.up), leverage: currentLeverage, marginType: p.mt, side: amount > 0 ? 'LONG' : 'SHORT', updateTime: payload.E || Date.now() };
                        if (existingIdx > -1)
                            positions.value[existingIdx] = newPos;
                        else
                            positions.value.push(newPos);
                    }
                    else if (existingIdx > -1) {
                        positions.value.splice(existingIdx, 1);
                    }
                });
            }
        }
    };
    const positionHistory = ref([]);
    const isLoadingHistory = ref(false);
    const fetchPositionHistory = async (symbol, limit = 50) => {
        // 🚨 应对币安限制：如果没有明确指定币种，强制使用全局当前币种
        const targetSymbol = symbol || currentSymbol.value;
        if (!targetSymbol)
            return;
        isLoadingHistory.value = true;
        try {
            // 必须带上 symbol 才能成功请求后端
            const url = `http://localhost:5000/api/account/trades?limit=${limit}&symbol=${targetSymbol}`;
            const res = await fetch(url, { method: 'GET' });
            if (res.ok) {
                const data = await res.json();
                // 币安返回的直接就是历史成交数组
                positionHistory.value = data;
            }
            else {
                console.error('后端返回错误:', await res.text());
            }
        }
        catch (e) {
            console.error('获取历史记录失败:', e);
        }
        finally {
            isLoadingHistory.value = false;
        }
    };
    const connectAllTickers = () => initWorker();
    const connectWs = () => initWorker();
    // 🌟 核心升级：基于 Map 的订阅逻辑
    const subscribeKline = (symbol, interval) => {
        initWorker();
        const streamName = `${symbol.toLowerCase()}@kline_${interval}`;
        const currentCount = mySubscriptions.get(streamName) || 0;
        mySubscriptions.set(streamName, currentCount + 1);
        // 只有第一个窗口订阅时，才真正发送网络请求
        if (currentCount === 0) {
            worker?.port.postMessage({ type: 'SUBSCRIBE', stream: streamName });
        }
    };
    // 🌟 核心升级：基于 Map 的退订逻辑
    const unsubscribeKline = (symbol, interval) => {
        if (!worker)
            return;
        const streamName = `${symbol.toLowerCase()}@kline_${interval}`;
        const currentCount = mySubscriptions.get(streamName) || 0;
        if (currentCount > 0) {
            const newCount = currentCount - 1;
            if (newCount === 0) {
                // 最后一个窗口关闭时，才真正发起退订
                mySubscriptions.delete(streamName);
                worker.port.postMessage({ type: 'UNSUBSCRIBE', stream: streamName });
            }
            else {
                mySubscriptions.set(streamName, newCount);
            }
        }
    };
    // 1. 记录从 REST API 获取时的基础状态
    const baseAvailableBalance = ref(0); // 接口拉取那一刻的“静态可用余额”
    const baseTotalUnrealizedPnl = ref(0); // 接口拉取那一刻的“全仓总未实现盈亏”
    // 2. 获取接口数据的逻辑 (refreshAccountBalance)
    const refreshAccountBalance = async () => {
        const res = await fetch(`${import.meta.env.VITE_API_BASE_URL}/api/account/info`);
        const accountData = await res.json();
        const usdtAsset = accountData.assets?.find((a) => a.asset === 'USDT');
        if (usdtAsset) {
            baseAvailableBalance.value = parseFloat(usdtAsset.availableBalance || '0');
            baseTotalUnrealizedPnl.value = parseFloat(usdtAsset.crossUnPnl || '0');
        }
    };
    // 3. 🌟 创造一个动态计算的可用余额 (暴露给 OrderModule.vue 使用)
    const dynamicUsdtBalance = computed(() => {
        // 算出当前的实时全仓总盈亏
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
        // 实时可用余额 = 基础可用余额 + (当前实时盈亏 - 基础盈亏)
        const realTimeBalance = baseAvailableBalance.value + (currentTotalCrossPnl - baseTotalUnrealizedPnl.value);
        // 余额不能小于 0
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
        dataSource, switchDataSource, symbolConfigs, dynamicUsdtBalance, backendLatency
    };
});

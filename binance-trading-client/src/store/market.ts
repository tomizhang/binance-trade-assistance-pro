import { defineStore } from 'pinia'
import { ref, reactive } from 'vue'

const BINANCE_WS_BASE = 'wss://fstream.binance.com/stream'

export const useMarketStore = defineStore('market', () => {
    const ws = ref<WebSocket | null>(null)

    // 使用 Record 记录每个流的“观看人数” (引用计数)
    const activeStreams = reactive<Record<string, number>>({})
    // 全局图表类型配置：{ 交易对: 'standard' | 'heikinAshi' }
    const globalChartType = reactive<Record<string, { type: string, sourceId: string }>>({})

    const setGlobalChartType = (symbol: string, type: string, sourceId: string) => {
        globalChartType[symbol] = { type, sourceId }
    }
    const latestKlines = reactive<Record<string, any>>({})

    const sendSubscribeMsg = (streams: string[]) => {
        if (ws.value?.readyState === WebSocket.OPEN && streams.length > 0) {
            console.log('📡 [WS 发送订阅]', streams)
            ws.value.send(JSON.stringify({ method: 'SUBSCRIBE', params: streams, id: Date.now() }))
        }
    }

    const connectWs = () => {
        if (ws.value && (ws.value.readyState === WebSocket.OPEN || ws.value.readyState === WebSocket.CONNECTING)) return

        ws.value = new WebSocket(BINANCE_WS_BASE)

        ws.value.onopen = () => {
            console.log('🟢 [WS 连接成功] 🔗')
            // 重连时，只订阅那些有人看的流
            const streamsToSubscribe = Object.keys(activeStreams).filter(k => activeStreams[k] > 0)
            if (streamsToSubscribe.length > 0) {
                sendSubscribeMsg(streamsToSubscribe)
            }
        }

        ws.value.onmessage = (event) => {
            const payload = JSON.parse(event.data)
            const realData = payload.data ? payload.data : payload

            if (realData && realData.e === 'kline') {
                const key = `${realData.s}_${realData.k.i}`
                latestKlines[key] = {
                    time: realData.k.t,
                    open: parseFloat(realData.k.o),
                    high: parseFloat(realData.k.h),
                    low: parseFloat(realData.k.l),
                    close: parseFloat(realData.k.c),
                    isFinal: realData.k.x
                }
            }
        }

        ws.value.onclose = () => {
            console.warn('🔴 [WS 断开] 3秒后重连...')
            setTimeout(connectWs, 3000)
        }
    }

    const subscribeKline = (symbol: string, interval: string) => {
        const streamName = `${symbol.toLowerCase()}@kline_${interval}`

        if (!activeStreams[streamName]) activeStreams[streamName] = 0
        activeStreams[streamName] += 1 // 观看人数 + 1

        // 第一个人进来时，向币安发指令
        if (activeStreams[streamName] === 1 && ws.value?.readyState === WebSocket.OPEN) {
            sendSubscribeMsg([streamName])
        }
    }

    const unsubscribeKline = (symbol: string, interval: string) => {
        const streamName = `${symbol.toLowerCase()}@kline_${interval}`

        if (activeStreams[streamName] && activeStreams[streamName] > 0) {
            activeStreams[streamName] -= 1 // 观看人数 - 1

            // 最后一个人离开时，切断币安数据流
            if (activeStreams[streamName] === 0) {
                console.log('🚫 [WS 取消订阅]', streamName)
                if (ws.value?.readyState === WebSocket.OPEN) {
                    ws.value.send(JSON.stringify({ method: 'UNSUBSCRIBE', params: [streamName], id: Date.now() }))
                }
                delete activeStreams[streamName]
            }
        }
    }

    // 1. 全局同步开关
    const isSyncEnabled = ref(false)
    const toggleSync = () => { isSyncEnabled.value = !isSyncEnabled.value }

    // 2. 十字光标坐标流：{ 交易对: { 价格, 时间, 来源ID } }
    const crosshairData = reactive<Record<string, { price: number, time: number, sourceId: string }>>({})
    const setCrosshair = (symbol: string, price: number, time: number, sourceId: string) => {
        crosshairData[symbol] = { price, time, sourceId }
    }
    const clearCrosshair = (symbol: string, sourceId: string) => {
        crosshairData[symbol] = { price: 0, time: 0, sourceId }
    }

    // 3. 画线同步：{ 交易对: [价格1, 价格2...] }
    const globalLines = reactive<Record<string, number[]>>({})
    const addGlobalLine = (symbol: string, price: number) => {
        if (!globalLines[symbol]) globalLines[symbol] = []
        globalLines[symbol].push(price)
    }
    const clearGlobalLines = (symbol: string) => {
        globalLines[symbol] = []
    }

    // 1. 在顶部状态区加入
    // 全市场行情数据：{ BTCUSDT: { lastPrice, priceChangePercent, volume, fundingRate } }
    // 🌟 1. 确保 marketTickers 在顶部被正确声明
    const marketTickers = reactive<Record<string, any>>({})
    let tickerWs: WebSocket | null = null

    // 🌟 2. 替换为组合流连接函数
    const connectAllTickers = () => {
        if (tickerWs && (tickerWs.readyState === WebSocket.OPEN || tickerWs.readyState === WebSocket.CONNECTING)) return;

        // 使用组合流：同时拉取 24hr miniTicker 和 markPrice (含资金费率，每秒推送)
        tickerWs = new WebSocket('wss://fstream.binance.com/stream?streams=!miniTicker@arr/!markPrice@arr@1s');

        tickerWs.onopen = () => console.log('🟢 [全市场流] 已连接 (含资金费率)');

        tickerWs.onmessage = (event) => {
            const payload = JSON.parse(event.data);
            const stream = payload.stream;
            const data = payload.data;

            // 解析价格、涨跌幅和成交额
            if (stream === '!miniTicker@arr') {
                for (const item of data) {
                    const symbol = item.s;
                    if (!symbol.endsWith('USDT')) continue;
                    if (!marketTickers[symbol]) marketTickers[symbol] = { fundingRate: 0 };

                    marketTickers[symbol].lastPrice = parseFloat(item.c);
                    const openPrice = parseFloat(item.o);
                    // 使用 item.q (USDT成交额) 比 item.v (币成交量) 更有参考价值
                    marketTickers[symbol].volume = parseFloat(item.q);
                    marketTickers[symbol].priceChangePercent = ((marketTickers[symbol].lastPrice - openPrice) / openPrice) * 100;
                }
            }
            // 解析资金费率
            else if (stream.startsWith('!markPrice@arr')) {
                for (const item of data) {
                    const symbol = item.s;
                    if (!symbol.endsWith('USDT')) continue;
                    if (!marketTickers[symbol]) marketTickers[symbol] = { lastPrice: 0, priceChangePercent: 0, volume: 0 };

                    // item.r 是资金费率 (Funding Rate)，乘以 100 转换为百分比
                    marketTickers[symbol].fundingRate = parseFloat(item.r) * 100;
                }
            }
        };

        tickerWs.onclose = () => {
            setTimeout(connectAllTickers, 3000);
        };
    }

    return {
        connectWs, subscribeKline, unsubscribeKline, latestKlines,
        isSyncEnabled, toggleSync,
        crosshairData, setCrosshair, clearCrosshair,
        globalLines, addGlobalLine, clearGlobalLines,
        globalChartType, setGlobalChartType,
        marketTickers, connectAllTickers
    }
})


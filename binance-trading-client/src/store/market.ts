import { defineStore } from 'pinia'
import { ref, reactive } from 'vue'

const BINANCE_WS_BASE = 'wss://fstream.binance.com/stream'

export const useMarketStore = defineStore('market', () => {
    const ws = ref<WebSocket | null>(null)

    // 使用 Record 记录每个流的“观看人数” (引用计数)
    const activeStreams = reactive<Record<string, number>>({})
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

    return {
        connectWs, subscribeKline, unsubscribeKline, latestKlines,
        isSyncEnabled, toggleSync,
        crosshairData, setCrosshair, clearCrosshair,
        globalLines, addGlobalLine, clearGlobalLines
    }
})
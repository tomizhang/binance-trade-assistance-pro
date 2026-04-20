// src/api/market.ts
import axios from 'axios'

export const MarketAPI = {
  /**
   * 从币安 U本位合约接口获取历史 K 线
   * @param symbol 交易对，如 BTCUSDT
   * @param interval 周期，如 1m, 5m, 1h
   * @param limit 限制数量，最大 1500
   */
  getHistoricalKlines: async (symbol: string, interval: string, limit: number = 500) => {
    try {
      // 因为配置了 Vite 代理，这里直接请求 /fapi 即可
      const response = await axios.get('/fapi/v1/klines', {
        params: { symbol, interval, limit }
      })

      // 将币安的嵌套数组格式映射为 lightweight-charts 需要的格式
      const klineData = response.data.map((item: any[]) => ({
        time: Math.floor(item[0] / 1000), // 必须是秒级时间戳
        open: parseFloat(item[1]),
        high: parseFloat(item[2]),
        low: parseFloat(item[3]),
        close: parseFloat(item[4]),
        // volume: parseFloat(item[5]) // 如果后续要画成交量柱子，保留这个字段
      }))

      return klineData
    } catch (error) {
      console.error('获取历史 K 线失败:', error)
      return []
    }
  }
}
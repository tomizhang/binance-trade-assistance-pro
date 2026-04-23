// src/api/market.ts
import axios from 'axios'

export const MarketAPI = {
  getHistoricalKlines: async (symbol: string, interval: string, limit: number = 1000, endTime?: number) => {
    try {
      const params: any = { symbol, interval, limit }
      // 🌟 新增：如果传入了截止时间，就带上这个参数
      if (endTime) {
        params.endTime = endTime;
      }

      const response = await axios.get('/fapi/v1/klines', { params })

      return [];
      return response.data.map((item: any[]) => ({
        time: Math.floor(item[0] / 1000), 
        open: parseFloat(item[1]),
        high: parseFloat(item[2]),
        low: parseFloat(item[3]),
        close: parseFloat(item[4]),
        volume: parseFloat(item[5]), // 🚨 核心修复：把币安的成交量数据拿出来！
      }))
    } catch (error) {
      console.error('获取历史 K 线失败:', error)
      return []
    }
  }
}
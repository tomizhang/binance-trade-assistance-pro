// src/api/market.ts
import axios from 'axios'
import { useMarketStore } from '@/store/market'

export const MarketAPI = {
  getHistoricalKlines: async (symbol: string, interval: string, limit: number = 1000, endTime?: number) => {
    try {
      const params: any = { symbol, interval, limit }
      // 🌟 新增：如果传入了截止时间，就带上这个参数
      if (endTime) {
        params.endTime = endTime;
      }

      // 🌟 动态获取当前选中的数据路线
      const marketStore = useMarketStore();
      let targetUrl = '';

      if (marketStore.dataSource === 'backend') {
        // 路线 1：C# 中继模式 (使用环境变量拼接完整路径)
        targetUrl = `${import.meta.env.VITE_API_BASE_URL}/api/market/klines`;
      } else {
        // 路线 2：直连币安模式
        // 如果你的本地浏览器遇到跨域问题(CORS)，可以将这行改回原来借助 Vite 代理的 '/fapi/v1/klines'
        targetUrl = 'https://fapi.binance.com/fapi/v1/klines'; 
      }

      const response = await axios.get(targetUrl, { params })
      
      var result = response.data.map((item: any[]) => ({
        time: Math.floor(item[0] / 1000), 
        open: parseFloat(item[1]),
        high: parseFloat(item[2]),
        low: parseFloat(item[3]),
        close: parseFloat(item[4]),
        volume: parseFloat(item[5]), // 🚨 核心修复：把币安的成交量数据拿出来！
      }))
      
      return result;
    } catch (error) {
      console.error(`获取历史 K 线失败 (路线: ${useMarketStore().dataSource}):`, error)
      return []
    }
  }
}
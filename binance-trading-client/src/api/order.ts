// src/api/order.ts

const API_BASE = import.meta.env.VITE_API_BASE_URL || '';

export const OrderAPI = {
  /**
   * 极速下单 / 挂单接口
   */
  placeOrder: async (payload: {
    symbol: string;
    side: 'BUY' | 'SELL';
    type: string;
    quantity: number;
    price?: number;
    stopPrice?: number;
    reduceOnly?: boolean;
  }) => {
    const res = await fetch(`${API_BASE}/api/order/place-ws`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    });
    
    const data = await res.json();
    if (!res.ok || data.error) {
      throw new Error(data.error?.msg || '下单指令被后端或交易所拒绝');
    }
    return data;
  },

  /**
   * 撤销指定的挂单
   */
  cancelOrder: async (symbol: string, orderId: string) => {
    const res = await fetch(`${API_BASE}/api/order/cancel-ws`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ symbol, orderId })
    });
    
    const data = await res.json();
    if (!res.ok || data.error) {
      throw new Error(data.error?.msg || '撤单指令被拒');
    }
    return data;
  }
};
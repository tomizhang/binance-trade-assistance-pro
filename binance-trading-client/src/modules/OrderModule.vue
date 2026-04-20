<template>
  <div class="order-module">
    <div class="order-types">
      <button class="active">市价</button>
      <button>限价</button>
    </div>
    <div class="order-inputs">
      <label>可用余额: -- USDT</label>
      <input type="number" placeholder="下单数量 (USD)" />
    </div>
    <div class="order-actions">
      <button class="btn-buy" @click="submitOrder('BUY')">买入/做多</button>
      <button class="btn-sell" @click="submitOrder('SELL')">卖出/做空</button>
    </div>
  </div>
</template>

<script setup lang="ts">
const submitOrder = (side: string) => {
  console.log(`准备提交 ${side} 订单至 C# 后端 API...`)
  // TODO: 调用 src/api/trade.ts 发送 REST 请求
}
</script>

<style scoped>
.order-module { padding: 10px; display: flex; flex-direction: column; gap: 15px; height: 100%; }
.order-types button { flex: 1; padding: 5px; background: #21262d; color: #c9d1d9; border: 1px solid #30363d; cursor: pointer;}
.order-types .active { background: #30363d; font-weight: bold; }
.order-inputs { display: flex; flex-direction: column; gap: 5px; }
.order-inputs input { background: #0d1117; border: 1px solid #30363d; color: #fff; padding: 8px; }
.order-actions { display: flex; gap: 10px; margin-top: auto;}
.btn-buy { flex: 1; background-color: #238636; color: white; border: none; padding: 10px; cursor: pointer;}
.btn-sell { flex: 1; background-color: #da3633; color: white; border: none; padding: 10px; cursor: pointer;}
</style>
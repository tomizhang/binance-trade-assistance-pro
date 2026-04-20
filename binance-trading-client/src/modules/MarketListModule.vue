<template>
  <div class="market-list-module">
    <div class="list-header">
      <span>币种 (永续)</span>
      <span>最新价</span>
    </div>
    <ul class="list-content">
      <li v-for="item in markets" :key="item.symbol" @dblclick="handleDoubleClick(item.symbol)">
        <span class="symbol">{{ item.symbol }}</span>
        <span class="price">{{ item.price }}</span>
      </li>
    </ul>
  </div>
</template>

<script setup lang="ts">
import { ref } from 'vue'

// 触发事件给父组件 (Dashboard)
const emit = defineEmits(['add-kline'])

// 模拟币种列表 (后续可以从币安 API 拉取全量 ticker)
const markets = ref([
  { symbol: 'BTCUSDT', price: '65000.00' },
  { symbol: 'ETHUSDT', price: '3500.00' },
  { symbol: 'SOLUSDT', price: '150.00' }
])

const handleDoubleClick = (symbol: string) => {
  emit('add-kline', symbol)
}
</script>

<style scoped>
.market-list-module { display: flex; flex-direction: column; height: 100%; }
.list-header { display: flex; justify-content: space-between; padding: 10px; font-size: 12px; color: #8b949e; border-bottom: 1px solid #30363d; }
.list-content { list-style: none; padding: 0; margin: 0; overflow-y: auto; }
.list-content li { display: flex; justify-content: space-between; padding: 10px; cursor: pointer; border-bottom: 1px solid #21262d; }
.list-content li:hover { background-color: #30363d; }
.symbol { font-weight: bold; }
</style>
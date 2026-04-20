<template>
  <div class="trading-dashboard">
    <aside class="sidebar-left">
      <MarketListModule @add-kline="addKlinePanel" />
    </aside>

    <main class="grid-workspace">
      <GridLayout
        v-model:layout="layout"
        :col-num="24"
        :row-height="30"
        :is-draggable="true"
        :is-resizable="true"
        :vertical-compact="true"
        :use-css-transforms="true"
        drag-allow-from=".panel-header"
        drag-ignore-from=".tv-lightweight-charts, .chart-container, button, a, input"
      >
        <GridItem
          v-for="item in layout"
          :key="item.i"
          :x="item.x"
          :y="item.y"
          :w="item.w"
          :h="item.h"
          :i="item.i"
        >
          <div class="panel-container">
            <div class="panel-header">
              <span class="panel-title">{{ item.title }}</span>
              <div class="panel-actions">
                <button @click="removePanel(item.i)">✕</button>
              </div>
            </div>
            
            <div class="panel-content">
              <component :is="getComponentByType(item.type)" :symbol="item.symbol" />
            </div>
          </div>
        </GridItem>
      </GridLayout>
    </main>
  </div>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { GridLayout, GridItem } from 'grid-layout-plus'

// 引入真实的业务组件
import KlineModule from '@/modules/KlineModule.vue'
import OrderModule from '@/modules/OrderModule.vue'
import PositionModule from '@/modules/PositionModule.vue'
import MarketListModule from '@/modules/MarketListModule.vue'

// 动态匹配组件类型
const getComponentByType = (type: string) => {
  switch (type) {
    case 'kline': return KlineModule
    case 'order': return OrderModule
    case 'position': return PositionModule
    default: return 'div'
  }
}

// 布局数据状态
const layout = ref([
  { x: 0, y: 0, w: 16, h: 14, i: 'kline-btc', type: 'kline', symbol: 'BTCUSDT', title: 'BTC/USDT 永续' },
  { x: 16, y: 0, w: 8, h: 14, i: 'order-panel', type: 'order', title: '下单面板' },
  { x: 0, y: 14, w: 24, h: 8, i: 'position-panel', type: 'position', title: '仓位与挂单' }
])

// 侧边栏双击添加新 K 线面板逻辑
const addKlinePanel = (symbol: string) => {
  const newId = `kline-${symbol}-${Date.now()}`
  layout.value.push({
    x: 0, y: 0, w: 12, h: 12, i: newId, type: 'kline', symbol: symbol, title: `${symbol} 永续`
  })
}

// 移除面板
const removePanel = (id: string) => {
  layout.value = layout.value.filter(item => item.i !== id)
}
</script>

<style scoped>
.trading-dashboard {
  display: flex;
  height: 100%;
  width: 100%;
  background-color: #0d1117; 
  color: #c9d1d9;
}

.sidebar-left {
  width: 250px;
  background-color: #161b22;
  border-right: 1px solid #30363d;
  display: flex;
  flex-direction: column;
}

.grid-workspace {
  flex: 1;
  overflow-y: auto;
  padding: 10px;
}

.panel-container {
  background-color: #161b22;
  border: 1px solid #30363d;
  border-radius: 4px;
  display: flex;
  flex-direction: column;
  height: 100%; /* 确保容器充满 GridItem */
  overflow: hidden;
}

.panel-header {
  height: 30px;
  background-color: #21262d;
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 0 10px;
  cursor: move; /* 提示可拖拽 */
  user-select: none;
  flex-shrink: 0;
}

.panel-title {
  font-size: 13px;
  font-weight: bold;
}

.panel-actions button {
  background: none;
  border: none;
  color: #8b949e;
  cursor: pointer;
  font-size: 14px;
}

.panel-actions button:hover {
  color: #ff7b72;
}

.panel-content {
  flex: 1;
  overflow: hidden; /* 防止内部组件撑爆容器 */
  position: relative;
}
</style>
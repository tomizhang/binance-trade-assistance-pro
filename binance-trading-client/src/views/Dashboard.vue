<template>
  <div class="trading-dashboard">
    <aside class="sidebar-left" v-show="isSidebarVisible">
      <MarketListModule 
        @add-kline="addKlinePanel" 
        @collapse="handleCollapse" 
      />
    </aside>

    <main class="grid-workspace">
      <button 
        v-if="!isSidebarVisible" 
        class="expand-sidebar-btn" 
        @click="handleExpand"
        title="展开行情列表"
      >
        <svg viewBox="0 0 24 24" width="16" height="16" stroke="currentColor" stroke-width="2" fill="none"><rect x="3" y="3" width="18" height="18" rx="2" ry="2"></rect><line x1="9" y1="3" x2="9" y2="21"></line></svg>
      </button>

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
          :x="item.x" :y="item.y" :w="item.w" :h="item.h" :i="item.i"
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
import { ref, nextTick } from 'vue'
import { GridLayout, GridItem } from 'grid-layout-plus'

import KlineModule from '@/modules/KlineModule.vue'
import OrderModule from '@/modules/OrderModule.vue'
import PositionModule from '@/modules/PositionModule.vue'
import MarketListModule from '@/modules/MarketListModule.vue'

const isSidebarVisible = ref(true)

// 🌟 核心修复：强制网格重绘的逻辑
const triggerGridResize = () => {
  // 等待 Vue DOM 更新完毕后，发送一个全局的 resize 事件
  nextTick(() => {
    setTimeout(() => {
      window.dispatchEvent(new Event('resize'));
    }, 150); // 给 150ms 延迟，确保 display:none 完全生效，避免网格库计算到错误的值
  });
};

const handleCollapse = () => {
  isSidebarVisible.value = false;
  triggerGridResize(); // 收起时重绘网格
};

const handleExpand = () => {
  isSidebarVisible.value = true;
  triggerGridResize(); // 展开时重绘网格
};

const getComponentByType = (type: string) => {
  switch (type) {
    case 'kline': return KlineModule
    case 'order': return OrderModule
    case 'position': return PositionModule
    default: return 'div'
  }
}

const layout = ref([
  // { x: 0, y: 0, w: 16, h: 14, i: 'kline-btc', type: 'kline', symbol: 'BTCUSDT', title: 'BTC/USDT 永续' },
  { x: 16, y: 0, w: 8, h: 14, i: 'order-panel', type: 'order', title: '下单面板' },
  { x: 0, y: 14, w: 24, h: 8, i: 'position-panel', type: 'position', title: '仓位与挂单' }
])

const addKlinePanel = (symbol: string) => {
  const newId = `kline-${symbol}-${Date.now()}`
  layout.value.push({ x: 0, y: 0, w: 12, h: 12, i: newId, type: 'kline', symbol: symbol, title: `${symbol} 永续` })
}

const removePanel = (id: string) => {
  layout.value = layout.value.filter(item => item.i !== id)
}
</script>

<style scoped>
.trading-dashboard { 
  display: flex; 
  height: 100vh; 
  width: 100vw; 
  background-color: #0d1117; 
  color: #c9d1d9; 
  overflow: hidden;
}

/* 🌟 宽度调整：从 280px 增加到 320px，视觉更舒展 */
.sidebar-left { 
  width: 320px; 
  flex-shrink: 0; 
  background-color: #161b22; 
  border-right: 1px solid #30363d; 
  display: flex; 
  flex-direction: column; 
}

.grid-workspace { 
  flex: 1; 
  position: relative; 
  overflow-y: auto; 
  padding: 10px; 
}

.expand-sidebar-btn {
  position: absolute;
  top: 10px;
  left: 0;
  z-index: 50;
  background-color: #21262d;
  color: #8b949e;
  border: 1px solid #30363d;
  border-left: none;
  padding: 6px 8px;
  border-radius: 0 6px 6px 0;
  cursor: pointer;
  transition: all 0.2s;
  display: flex;
  align-items: center;
  justify-content: center;
}
.expand-sidebar-btn:hover {
  color: #58a6ff;
  background-color: #30363d;
}

.panel-container { background-color: #161b22; border: 1px solid #30363d; border-radius: 4px; display: flex; flex-direction: column; height: 100%; overflow: hidden; }
.panel-header { height: 30px; background-color: #21262d; display: flex; justify-content: space-between; align-items: center; padding: 0 10px; cursor: move; user-select: none; flex-shrink: 0; }
.panel-title { font-size: 13px; font-weight: bold; }
.panel-actions button { background: none; border: none; color: #8b949e; cursor: pointer; font-size: 14px; }
.panel-actions button:hover { color: #ff7b72; }
.panel-content { flex: 1; overflow: hidden; position: relative; }
</style>
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
            
          drag-allow-from=".drag-handle"
            
          drag-ignore-from=".no-drag, .custom-slider, button, input, select, textarea, .chart-wrapper"
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
              <button class="size-btn" @click="updateGridWidth(item.i, 12)" :class="{ active: item.w === 12 }" title="50% 宽度">🌓</button>
              <button class="size-btn" @click="updateGridWidth(item.i, 24)" :class="{ active: item.w === 24 }" title="100% 宽度">🌕</button>
              <span class="action-divider">|</span>
              <button class="close-btn" @click="removePanel(item.i)" title="关闭面板">✕</button>
            </div>
          </div>
          
          <div 
            class="panel-content no-drag"
            @mousedown.stop
            @touchstart.stop
            @pointerdown.stop
          >
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
import FourierModule from '@/modules/FourierModule.vue';

const isSidebarVisible = ref(true)

const triggerGridResize = () => {
  nextTick(() => {
    setTimeout(() => {
      window.dispatchEvent(new Event('resize'));
    }, 150); 
  });
};

const handleCollapse = () => {
  isSidebarVisible.value = false;
  triggerGridResize(); 
};

const handleExpand = () => {
  isSidebarVisible.value = true;
  triggerGridResize(); 
};

const getComponentByType = (type: string) => {
  switch (type) {
    case 'kline': return KlineModule
    case 'order': return OrderModule
    case 'position': return PositionModule
    case 'fourier': return FourierModule
    default: return 'div'
  }
}

const layout = ref([
  // { x: 0, y: 0, w: 16, h: 14, i: 'kline-btc', type: 'kline', symbol: 'BTCUSDT', title: 'BTC/USDT 永续' },
  // { i: 'fourier-main', x: 0, y: 12, w: 16, h: 12, type: 'fourier', title: '频域分析' },
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

const updateGridWidth = (id: string, newWidth: number) => {
  const index = layout.value.findIndex(item => item.i === id);
  if (index !== -1) {
    layout.value[index].w = newWidth;
    layout.value = [...layout.value];
    triggerGridResize();
  }
}

// 🌟 新增：面板克隆逻辑
const duplicatePanel = (id: string) => {
  const targetItem = layout.value.find(item => item.i === id);
  if (targetItem) {
    // 拷贝属性，生成新 ID，并利用 y: 999 让网格自动将其放置在最底部
    const clonedItem = {
      ...targetItem,
      i: `${targetItem.type}-clone-${Date.now()}`,
      y: 999, 
    };
    
    layout.value.push(clonedItem);
    
    // 触发图表重新适配
    triggerGridResize();
  }
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

.panel-actions { display: flex; align-items: center; gap: 4px; }
.action-icon-btn { background: transparent; border: none; color: #8b949e; padding: 2px 4px; font-size: 12px; cursor: pointer; transition: 0.2s; border-radius: 4px; }
.action-icon-btn:hover { background: #30363d; color: #c9d1d9; }
.action-icon-btn.active { color: #58a6ff; font-weight: bold; background: rgba(88, 166, 255, 0.1); }

.action-divider { color: #30363d; margin: 0 4px; font-size: 12px; }

.close-btn { background: none; border: none; color: #8b949e; cursor: pointer; font-size: 14px; padding: 2px 6px; border-radius: 4px; transition: 0.2s; }
.close-btn:hover { color: #ff7b72; background: rgba(255, 123, 114, 0.1); }

.panel-content { flex: 1; overflow: hidden; position: relative; }
</style>
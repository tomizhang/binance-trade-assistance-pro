<template>
  <div class="trading-dashboard">
    <transition name="fade">
      <div v-if="!marketStore.isReady" class="initialization-overlay">
        <div class="loader-content">
          <div class="terminal-header">
            <span class="pulse-icon">⚡</span>
            <span class="title">SYSTEM BOOT SEQUENCE</span>
          </div>
          <div class="status-list">
            <div class="status-item" :class="{ 'done': marketStore.wsStatus === 'CONNECTED' }">
              <span class="dot"></span> 建立行情数据中枢 (WebSocket) ... {{ marketStore.wsStatus === 'CONNECTED' ? '[完成]' : '[同步中]' }}
            </div>
            <div class="status-item" :class="{ 'done': Object.keys(marketStore.symbolRules).length > 0 }">
              <span class="dot"></span> 同步标的交易规范 (Exchange Info) ... {{ Object.keys(marketStore.symbolRules).length > 0 ? '[完成]' : '[同步中]' }}
            </div>
            <div class="status-item" :class="{ 'done': Object.keys(marketStore.symbolConfigs).length > 0 }">
              <span class="dot"></span> 获取账户风控参数 (User Config) ... {{ Object.keys(marketStore.symbolConfigs).length > 0 ? '[完成]' : '[同步中]' }}
            </div>
            <div class="status-item" :class="{ 'done': marketStore.dynamicUsdtBalance > 0 }">
              <span class="dot"></span> 校验全仓可用保证金 (Margin Check) ... {{ marketStore.dynamicUsdtBalance > 0 ? '[OK]' : '[等待数据]' }}
            </div>
          </div>
          <div class="boot-bar"><div class="boot-fill" :style="{ width: bootProgress + '%' }"></div></div>
          <p class="loading-hint">正在验证加密签名并初始化交易指令集...</p>
        </div>
      </div>
    </transition>

    <aside class="sidebar-left" v-show="isSidebarVisible">
      <MarketListModule @add-kline="addKlinePanel" @collapse="handleCollapse" />
    </aside>

    <button v-if="!isSidebarVisible" class="expand-sidebar-btn" @click="handleExpand" title="展开行情列表">
      <svg viewBox="0 0 24 24" width="16" height="16" stroke="currentColor" stroke-width="2" fill="none"><rect x="3" y="3" width="18" height="18" rx="2" ry="2"></rect><line x1="9" y1="3" x2="9" y2="21"></line></svg>
    </button>

    <!-- 🌟 修改了这里：点击铃铛按钮时，触发 Store 的抽屉控制方法 -->


    <main class="grid-workspace">
      <GridLayout
        v-model:layout="layout"
        :col-num="24"
        :row-height="30"
        :is-draggable="true"
        :is-resizable="true"
        :vertical-compact="true"
        :use-css-transforms="true"
        drag-allow-from=".drag-handle, .panel-header"
        drag-ignore-from=".no-drag, .custom-slider, button, input, select, textarea, .chart-wrapper"
      >
        <GridItem v-for="item in layout" :key="item.i" :x="item.x" :y="item.y" :w="item.w" :h="item.h" :i="item.i">
          <div class="panel-container">
            <div class="panel-header drag-handle">
              <span class="panel-title">{{ item.title }}</span>
              <div class="panel-actions">
                <button v-if="isFocused(item.symbol)" class="action-icon-btn">🟢</button>
                <button class="action-icon-btn" @click="duplicatePanel(item.i)" title="复制面板">📋</button>
                <button class="action-icon-btn" @click="updateGridWidth(item.i, 11)" :class="{ active: item.w === 11 }" title="50% 宽度">🌓</button>
                <button class="action-icon-btn" @click="updateGridWidth(item.i, 22)" :class="{ active: item.w === 22 }" title="100% 宽度">🌕</button>
                <span class="action-divider">|</span>
                <button class="close-btn" @click="removePanel(item.i)" title="关闭面板">✕</button>
              </div>
            </div>
            <div class="panel-content no-drag" @mousedown.stop @touchstart.stop @pointerdown.stop>
              <component 
                :is="getComponentByType(item.type)" 
                :symbol="item.symbol" 
                @duplicate="addKlinePanel" 
                @openChart="handleOpenChart" 
              />
            </div>
          </div>
        </GridItem>
      </GridLayout>
    </main>

    <!-- 🌟 完全去除了 :visible 传参，由 NotificationSidebar 内部使用 Store 进行状态管理 -->
    <NotificationSidebar  @openChart="handleOpenChart" />
  </div>
</template>

<script setup lang="ts">
import { ref, computed, nextTick, onMounted, onUnmounted, watch } from 'vue'
import { GridLayout, GridItem } from 'grid-layout-plus'
import { useMarketStore } from '@/store/market'
// 🌟 引入通知 Store
import { useNotificationStore } from '@/store/notification'

import KlineModule from '@/modules/KlineModule.vue'
import OrderModule from '@/modules/OrderModule.vue'
import PositionModule from '@/modules/PositionModule.vue'
import MarketListModule from '@/modules/MarketListModule.vue'
import FourierModule from '@/modules/FourierModule.vue'

import NotificationSidebar from '@/modules/NotificationSidebar.vue'

const marketStore = useMarketStore()
const notificationStore = useNotificationStore() // 🌟 初始化 Store

const isSidebarVisible = ref(true)
const hasNewAlerts = ref(false)

// 🌟 监听全局 Store 里的未读消息数，使铃铛按钮闪烁
watch(() => notificationStore.unreadCount, (newVal, oldVal) => {
  if (newVal > oldVal) {
    hasNewAlerts.value = true;
    setTimeout(() => { hasNewAlerts.value = false; }, 2000);
  }
});

const isFocused = (symbol: any) => { return marketStore.currentSymbol === symbol } 

const bootProgress = computed(() => {
  let p = 0;
  if (marketStore.wsStatus === 'CONNECTED') p += 25;
  if (Object.keys(marketStore.symbolRules).length > 0) p += 25;
  if (Object.keys(marketStore.symbolConfigs).length > 0) p += 25;
  if (marketStore.dynamicUsdtBalance > 0) p += 25;
  return p;
});

const triggerGridResize = () => {
  nextTick(() => { setTimeout(() => { window.dispatchEvent(new Event('resize')); }, 150); });
};

const handleCollapse = () => { isSidebarVisible.value = false; triggerGridResize(); };
const handleExpand = () => { isSidebarVisible.value = true; triggerGridResize(); };

const getComponentByType = (type: string) => {
  switch (type) {
    case 'kline': return KlineModule
    case 'order': return OrderModule
    case 'position': return PositionModule
    case 'fourier': return FourierModule
    default: return 'div'
  }
}

interface LayoutItem { x: number; y: number; w: number; h: number; i: string; type: string; title: string; symbol?: string; }

const layout = ref<LayoutItem[]>([
  { x: 16, y: 0, w: 8, h: 14, i: 'order-panel', type: 'order', title: '下单面板' },
  { x: 0, y: 14, w: 24, h: 8, i: 'position-panel', type: 'position', title: '仓位与挂单' }
])

const addKlinePanel = (symbol: string) => {
  const newId = `kline-${symbol}-${Date.now()}`
  layout.value.push({ x: 0, y: 0, w: 11, h: 12, i: newId, type: 'kline', symbol: symbol, title: `${symbol} 永续` })
}

const handleOpenChart = (symbol: string) => {
  const chartExists = layout.value.some(item => item.symbol === symbol && item.type === 'kline');
  if (!chartExists) {
    addKlinePanel(symbol);
  }
  marketStore.setCurrentSymbol(symbol);
}

const removePanel = (id: string) => { layout.value = layout.value.filter(item => item.i !== id) }

const updateGridWidth = (id: string, newWidth: number) => {
  const index = layout.value.findIndex(item => item.i === id);
  if (index !== -1) {
    layout.value[index].w = newWidth;
    layout.value = [...layout.value];
    triggerGridResize();
  }
}

const duplicatePanel = (id: string) => {
  const targetItem = layout.value.find(item => item.i === id);
  if (targetItem) {
    const clonedItem = { ...targetItem, i: `${targetItem.type}-clone-${Date.now()}`, y: 999 };
    layout.value.push(clonedItem);
    triggerGridResize();
  }
}

const handleGlobalAddPanel = (e: any) => {
  const { type, title, symbol } = e.detail;
  layout.value.push({
    x: (layout.value.length * 4) % 24, y: 999,
    w: type === 'position' ? 24 : 8,
    h: type === 'kline' ? 14 : 10,
    i: `${type}-${Date.now()}`, type, title, symbol
  });
  triggerGridResize();
};

onMounted(() => { 
  window.addEventListener('add-panel', handleGlobalAddPanel); 
  if (typeof marketStore.initAccountSignalR === 'function') {
    marketStore.initAccountSignalR();
  }
});

onUnmounted(() => { 
  window.removeEventListener('add-panel', handleGlobalAddPanel); 
  if (typeof marketStore.closeAccountSignalR === 'function') {
    marketStore.closeAccountSignalR();
  }
});
</script>

<style scoped>
/* 初始化遮罩层样式 */
.initialization-overlay {
  position: fixed; top: 0; left: 0; right: 0; bottom: 0; background: #0d1117; z-index: 9999;
  display: flex; align-items: center; justify-content: center; color: #c9d1d9; font-family: 'Courier New', Courier, monospace;
}
.loader-content { width: 450px; }
.terminal-header { display: flex; align-items: center; gap: 12px; margin-bottom: 30px; border-bottom: 1px solid #30363d; padding-bottom: 10px; }
.pulse-icon { color: #58a6ff; font-size: 20px; animation: blink 1s infinite; }
.terminal-header .title { font-size: 16px; font-weight: bold; letter-spacing: 2px; color: #58a6ff; }
.status-list { display: flex; flex-direction: column; gap: 12px; margin-bottom: 30px; }
.status-item { display: flex; align-items: center; gap: 10px; font-size: 13px; color: #8b949e; transition: color 0.3s; }
.status-item.done { color: #2ea043; }
.status-item .dot { width: 6px; height: 6px; border-radius: 50%; background: #30363d; }
.status-item.done .dot { background: #2ea043; box-shadow: 0 0 8px #2ea043; }
.boot-bar { height: 4px; background: #21262d; border-radius: 2px; overflow: hidden; margin-bottom: 15px; }
.boot-fill { height: 100%; background: linear-gradient(90deg, #1f6feb, #58a6ff); transition: width 0.4s ease; }
.loading-hint { font-size: 11px; color: #484f58; text-align: center; }
@keyframes blink { 0%, 100% { opacity: 1; } 50% { opacity: 0.3; } }
.fade-leave-active { transition: opacity 0.8s ease; }
.fade-leave-to { opacity: 0; }

/* 🌟 基础架构样式 */
.trading-dashboard { 
  display: flex; height: 100vh; width: 100vw; background-color: #0d1117; 
  overflow: hidden; position: relative; 
}
.sidebar-left { width: 320px; flex-shrink: 0; background-color: #161b22; border-right: 1px solid #30363d; display: flex; flex-direction: column; }
.grid-workspace { flex: 1; position: relative; overflow-y: auto; padding: 10px; }

/* 🌟 展开行情按钮 */
.expand-sidebar-btn {
  position: absolute; left: 0; top: 15px; z-index: 100;
  background: #21262d; border: 1px solid #30363d; border-left: none; color: #8b949e;
  padding: 8px 6px 8px 10px; border-radius: 0 6px 6px 0; cursor: pointer;
  display: flex; align-items: center; justify-content: center;
  box-shadow: 4px 0 12px rgba(0,0,0,0.5); transition: all 0.2s ease;
}
.expand-sidebar-btn:hover { background: #30363d; color: #58a6ff; border-color: #58a6ff; }

/* 🌟 右侧通知铃铛按钮 */
.notification-toggle-btn {
  position: absolute; right: 20px; top: 10px; z-index: 110;
  background: #21262d; border: 1px solid #30363d; color: #c9d1d9;
  padding: 8px 12px; border-radius: 6px; cursor: pointer;
  display: flex; align-items: center; gap: 8px; transition: all 0.2s;
  box-shadow: 0 4px 12px rgba(0,0,0,0.3);
}
.notification-toggle-btn:hover { background: #30363d; border-color: #58a6ff; }
.notification-toggle-btn.has-unread { animation: shake 0.5s ease-in-out; border-color: #f85149; }
.notification-toggle-btn .badge {
  background: #f85149; color: white; font-size: 10px; padding: 2px 6px; border-radius: 10px; font-weight: bold;
}

@keyframes shake {
  0%, 100% { transform: translateX(0); }
  25% { transform: translateX(-4px); }
  75% { transform: translateX(4px); }
}

/* 面板通用样式 */
.panel-container { background-color: #161b22; border: 1px solid #30363d; border-radius: 4px; display: flex; flex-direction: column; height: 100%; overflow: hidden; }
.panel-header { height: 30px; background-color: #21262d; display: flex; justify-content: space-between; align-items: center; padding: 0 10px; cursor: move; flex-shrink: 0; }
.panel-title { font-size: 13px; font-weight: bold; color: #8b949e; }
.action-icon-btn { background: transparent; border: none; color: #8b949e; padding: 2px 4px; font-size: 12px; cursor: pointer; border-radius: 4px; }
.action-icon-btn.active { color: #58a6ff; background: rgba(88, 166, 255, 0.1); }
.action-icon-btn:hover { color: #c9d1d9; }
.close-btn { background: transparent; border: none; color: #8b949e; padding: 2px 4px; font-size: 12px; cursor: pointer; border-radius: 4px; }
.close-btn:hover { color: #f85149; }
.action-divider { color: #30363d; margin: 0 4px; }
.panel-content { flex: 1; overflow: hidden; position: relative; }
</style>
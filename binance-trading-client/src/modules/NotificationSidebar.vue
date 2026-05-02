<template>
  <!-- 遮罩层 (点击空白处收起) -->
  <div 
    class="sidebar-overlay" 
    v-if="notificationStore.isSidebarOpen" 
    @click="handleClose"
  ></div>

  <!-- 右侧抽屉本体 -->
  <div class="notification-sidebar" :class="{ 'is-open': notificationStore.isSidebarOpen }">
    <div class="sidebar-header">
      <h3>
        🔔 消息中心 
        <span class="badge" v-if="notificationStore.unreadCount > 0">{{ notificationStore.unreadCount }}</span>
      </h3>
      <div class="header-actions">
        <button class="clear-btn" @click="notificationStore.clearAll" title="清空全部">🗑️</button>
        <button class="close-btn" @click="handleClose">✕</button>
      </div>
    </div>

    <div class="sidebar-content">
      <div v-if="notificationStore.alerts.length === 0" class="empty-state">
        没有任何报警消息
      </div>
      
      <div 
        v-for="alert in notificationStore.alerts" 
        :key="alert.id" 
        class="alert-card"
        :class="getAlertClass(alert.type)"
        @click="jumpToChart(alert.symbol)"
      >
        <div class="card-header">
          <span class="card-title">{{ alert.title }}</span>
          <span class="card-time">{{ formatTime(alert.timestamp) }}</span>
        </div>
        <div class="card-body">
          {{ alert.content }}
        </div>
        <!-- 未读红点提示 -->
        <div class="unread-dot" v-if="!alert.isRead"></div>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { useNotificationStore } from '@/store/notification';
import { useMarketStore } from '@/store/market'; 

// 🌟 核心：显式声明抛给父组件的自定义事件，彻底消除 Vue 警告
const emit = defineEmits(['close', 'openChart']);

const notificationStore = useNotificationStore();
const marketStore = useMarketStore();

// 格式化时间戳 HH:mm:ss
const formatTime = (ts: number) => {
  const d = new Date(ts);
  return `${String(d.getHours()).padStart(2,'0')}:${String(d.getMinutes()).padStart(2,'0')}:${String(d.getSeconds()).padStart(2,'0')}`;
};

// 根据报警类型匹配左侧边框颜色
const getAlertClass = (type: string) => {
  switch (type) {
    case 'HA_REVERSAL': return 'alert-ha';
    case 'STRATEGY_ALERT': return 'alert-strategy';
    default: return 'alert-default';
  }
};

// 触发关闭逻辑
const handleClose = () => {
  notificationStore.toggleSidebar(false); // 更新全局 Store 状态
  emit('close'); // 触发父组件的 @close 事件
};

// 点击报警卡片直接跳转图表
const jumpToChart = (symbol?: string) => {
  if (symbol) {
    marketStore.setCurrentSymbol(symbol); // 更新行情全局焦点
    emit('openChart', symbol); // 触发父组件的 @openChart 事件
    
    // 如果你希望点击卡片后抽屉自动收起，可以解除下面这行的注释
    // handleClose(); 
  }
};
</script>

<style scoped>
/* 遮罩层 */
.sidebar-overlay {
  position: fixed; top: 0; left: 0; width: 100vw; height: 100vh;
  background: rgba(0, 0, 0, 0.4); z-index: 9998;
}

/* 抽屉容器 */
.notification-sidebar {
  position: fixed; top: 0; right: -350px; width: 350px; height: 100vh;
  background: #0d1117; border-left: 1px solid #30363d;
  box-shadow: -8px 0 24px rgba(0,0,0,0.5); z-index: 9999;
  display: flex; flex-direction: column; transition: right 0.3s cubic-bezier(0.4, 0, 0.2, 1);
}
.notification-sidebar.is-open {
  right: 0;
}

/* 头部区域 */
.sidebar-header {
  display: flex; justify-content: space-between; align-items: center;
  padding: 16px; border-bottom: 1px solid #21262d; background: #161b22;
}
.sidebar-header h3 {
  margin: 0; color: #c9d1d9; font-size: 16px; display: flex; align-items: center; gap: 8px;
}
.badge {
  background: #f85149; color: white; font-size: 12px; padding: 2px 6px;
  border-radius: 10px; font-weight: bold;
}
.header-actions button {
  background: transparent; border: none; color: #8b949e; font-size: 16px; cursor: pointer; transition: 0.2s;
}
.header-actions button:hover { color: #c9d1d9; }
.close-btn:hover { color: #f85149 !important; }

/* 列表区域 */
.sidebar-content {
  flex: 1; overflow-y: auto; padding: 12px; display: flex; flex-direction: column; gap: 10px;
}
.sidebar-content::-webkit-scrollbar { width: 6px; }
.sidebar-content::-webkit-scrollbar-thumb { background: #30363d; border-radius: 3px; }

.empty-state {
  text-align: center; color: #8b949e; margin-top: 50px; font-size: 14px;
}

/* 卡片样式 */
.alert-card {
  position: relative; padding: 12px; border-radius: 6px; border: 1px solid #30363d;
  background: #161b22; cursor: pointer; transition: transform 0.1s, border-color 0.2s;
}
.alert-card:hover {
  transform: translateY(-2px); border-color: #58a6ff;
}

.card-header {
  display: flex; justify-content: space-between; margin-bottom: 6px; align-items: center;
}
.card-title { font-weight: bold; font-size: 13px; color: #e6edf3; }
.card-time { font-size: 11px; color: #8b949e; font-family: monospace; }
.card-body { font-size: 12px; color: #8b949e; line-height: 1.4; }

.unread-dot {
  position: absolute; top: 12px; right: 12px; width: 6px; height: 6px;
  background: #f85149; border-radius: 50%;
}

/* 🌟 特殊卡片颜色定制 */
.alert-ha {
  border-left: 3px solid #d29922; /* HA 趋势反转用金色提示 */
}
.alert-strategy {
  border-left: 3px solid #f85149; /* 量价异动用红色提示 */
}
.alert-default {
  border-left: 3px solid #58a6ff; /* 系统普通消息用蓝色提示 */
}
</style>
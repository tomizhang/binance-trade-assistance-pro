<template>
  <header class="top-navbar">
    <div class="nav-left">
      <div class="logo">
        <span class="logo-icon">⚡</span>
        <span class="logo-text">QUANT TERMINAL</span>
      </div>
      <div class="nav-links">
        <a href="#" class="active">交易面板</a>
        <a href="#">策略回测</a>
        <a href="#">资产管理</a>
      </div>
    </div>

    <div class="nav-right">
      <div class="global-symbol">
        当前标的: <span>{{ marketStore.currentSymbol }}</span>
      </div>

      <div class="data-source-toggle">
  <span style="color: #8b949e; font-size: 12px; margin-right: 8px;">行情数据源:</span>
  <button 
    :class="{ active: marketStore.dataSource === 'backend' }" 
    @click="marketStore.switchDataSource('backend')"
  >C# 中继</button>
  
  <button 
    :class="{ active: marketStore.dataSource === 'binance' }" 
    @click="marketStore.switchDataSource('binance')"
  >直连币安</button>
</div>

      <div class="ws-status" :class="statusClass" :title="statusText">
        <span class="pulse-dot"></span>
        <span class="status-label">{{ statusText }}</span>
      </div>
      
      <div class="user-profile">
        <div class="avatar">admin</div>
      </div>
    </div>
  </header>
</template>

<script setup lang="ts">
import { computed } from 'vue';
import { useMarketStore } from '@/store/market';

const marketStore = useMarketStore();

const statusClass = computed(() => {
  switch (marketStore.wsStatus) {
    case 'CONNECTED': return 'status-connected';
    case 'CONNECTING': return 'status-connecting';
    default: return 'status-disconnected';
  }
});

const statusText = computed(() => {
  switch (marketStore.wsStatus) {
    case 'CONNECTED': return 'WS 已连接';
    case 'CONNECTING': return 'WS 连接中...';
    default: return 'WS 断开';
  }
});
</script>

<style scoped>
.top-navbar {
  height: 50px;
  background-color: #0d1117;
  border-bottom: 1px solid #30363d;
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 0 20px;
  color: #c9d1d9;
  font-size: 13px;
  flex-shrink: 0; /* 防止被 flex 容器压缩 */
}

.nav-left, .nav-right {
  display: flex;
  align-items: center;
  gap: 20px;
}

.logo {
  display: flex;
  align-items: center;
  gap: 8px;
  font-weight: 900;
  font-size: 16px;
  letter-spacing: 1px;
  color: #e6edf3;
}

.logo-icon { color: #58a6ff; }

.nav-links {
  display: flex;
  gap: 15px;
  margin-left: 20px;
}

.nav-links a {
  text-decoration: none;
  color: #8b949e;
  font-weight: bold;
  transition: color 0.2s;
}

.nav-links a:hover, .nav-links a.active {
  color: #e6edf3;
}

.global-symbol {
  color: #8b949e;
  font-weight: bold;
}
.global-symbol span {
  color: #e6edf3;
  background: #21262d;
  padding: 2px 6px;
  border-radius: 4px;
  margin-left: 5px;
}

/* 🌟 WS 状态灯样式 */
.ws-status {
  display: flex;
  align-items: center;
  gap: 8px;
  background: #161b22;
  border: 1px solid #30363d;
  padding: 4px 10px;
  border-radius: 12px;
  font-weight: bold;
}

.pulse-dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
}

.status-connected .pulse-dot {
  background-color: #2ea043;
  box-shadow: 0 0 8px rgba(46, 160, 67, 0.8);
  animation: pulse-green 2s infinite;
}

.status-connecting .pulse-dot {
  background-color: #d29922;
  box-shadow: 0 0 8px rgba(210, 153, 34, 0.8);
  animation: pulse-yellow 1s infinite;
}

.status-disconnected .pulse-dot {
  background-color: #f85149;
  box-shadow: 0 0 8px rgba(248, 81, 73, 0.8);
}

.status-label {
  color: #8b949e;
}
.status-connected .status-label { color: #2ea043; }
.status-disconnected .status-label { color: #f85149; }

@keyframes pulse-green {
  0% { transform: scale(0.95); box-shadow: 0 0 0 0 rgba(46, 160, 67, 0.7); }
  70% { transform: scale(1); box-shadow: 0 0 0 6px rgba(46, 160, 67, 0); }
  100% { transform: scale(0.95); box-shadow: 0 0 0 0 rgba(46, 160, 67, 0); }
}

@keyframes pulse-yellow {
  0% { opacity: 1; }
  50% { opacity: 0.5; }
  100% { opacity: 1; }
}

.user-profile .avatar {
  background: #1f6feb;
  color: white;
  width: 32px;
  height: 32px;
  border-radius: 50%;
  display: flex;
  align-items: center;
  justify-content: center;
  font-weight: bold;
  cursor: pointer;
}
</style>
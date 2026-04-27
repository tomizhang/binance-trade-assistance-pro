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
      <div class="global-symbol" title="全局联动标的">
        当前标的: <span>{{ marketStore.currentSymbol }}</span>
      </div>

      <div class="route-switcher">
        <span class="switcher-label">数据路线</span>
        <div class="segmented-control">
          <div 
            class="segment-item" 
            :class="{ active: marketStore.dataSource === 'backend' }" 
            @click="marketStore.switchDataSource('backend')"
          >
            <span class="icon">⚡</span> C# 中继
          </div>
          <div 
            class="segment-item" 
            :class="{ active: marketStore.dataSource === 'binance' }" 
            @click="marketStore.switchDataSource('binance')"
          >
            <span class="icon">🌐</span> 直连币安
          </div>
        </div>
      </div>

      <div class="network-group">
        <div class="ws-status" :class="statusClass" :title="statusText">
          <span class="pulse-dot"></span>
          <span class="status-label">{{ statusText }}</span>
        </div>
        
        <div class="latency-indicator" v-if="marketStore.wsStatus === 'CONNECTED'">
          <span class="signal-icon">📶</span>
          
          <template v-if="marketStore.dataSource === 'backend'">
            <div class="latency-detail">
              <span class="part" title="前端到中继延迟" :class="getLatencyColor(frontToBackend)">前 {{ frontToBackend }}ms</span>
              <span class="divider">-</span>
              <span class="part" title="中继到币安延迟" :class="getLatencyColor(marketStore.backendLatency)">后 {{ marketStore.backendLatency }}ms</span>
            </div>
          </template>
          
          <template v-else>
            <span class="latency-text" :class="getLatencyColor(frontToBinance)">{{ frontToBinance }} ms</span>
          </template>
        </div>
      </div>
      
      <div class="user-profile">
        <div class="avatar">admin</div>
      </div>
    </div>
  </header>
</template>

<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted } from 'vue';
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
    case 'CONNECTED': return '已连接';
    case 'CONNECTING': return '连接中';
    default: return '已断开';
  }
});

// ==========================================
// 🌟 真实网络延迟测量逻辑
// ==========================================
const frontToBackend = ref(0);
const frontToBinance = ref(0);
let pingTimer: ReturnType<typeof setInterval> | null = null;

const measureLatency = async () => {
  if (marketStore.wsStatus !== 'CONNECTED') return;

  if (marketStore.dataSource === 'backend') {
    // 测算: 前端到本地 C# 中继的真实延迟 (极轻量级 HEAD 请求抓取纯网络 RTT)
    const start = Date.now();
    try {
      await fetch(`${import.meta.env.VITE_API_BASE_URL}/ping`, { method: 'GET' }).catch(() => {});
      frontToBackend.value = Date.now() - start;
    } catch (e) {}
  } else {
    // 测算: 前端直连币安的真实延迟
    const start = Date.now();
    try {
      await fetch('/fapi/v1/ping', { method: 'GET' });
      frontToBinance.value = Date.now() - start;
    } catch (e) {}
  }
};

const getLatencyColor = (ms: number) => {
  if (ms === 0) return 'level-good'; // 初始化
  if (ms < 50) return 'level-excellent';
  if (ms < 150) return 'level-good';
  return 'level-poor';
};

onMounted(() => {
  measureLatency();
  pingTimer = setInterval(measureLatency, 2000); // 每 2 秒测一次真实延迟
});

onUnmounted(() => {
  if (pingTimer) clearInterval(pingTimer);
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
  flex-shrink: 0; 
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

/* 胶囊式切换器样式 */
.route-switcher {
  display: flex;
  align-items: center;
  gap: 8px;
}
.switcher-label {
  color: #8b949e;
  font-size: 12px;
}
.segmented-control {
  display: flex;
  background: #010409;
  border: 1px solid #30363d;
  border-radius: 6px;
  padding: 2px;
  user-select: none;
}
.segment-item {
  padding: 4px 12px;
  font-size: 12px;
  color: #8b949e;
  cursor: pointer;
  border-radius: 4px;
  transition: all 0.2s ease;
  display: flex;
  align-items: center;
  gap: 4px;
}
.segment-item:hover {
  color: #c9d1d9;
}
.segment-item.active {
  background: #21262d;
  color: #e6edf3;
  font-weight: bold;
  box-shadow: 0 1px 3px rgba(0,0,0,0.2);
}

/* 🌟 网络状态监控组 */
.network-group {
  display: flex;
  align-items: center;
  background: #161b22;
  border: 1px solid #30363d;
  border-radius: 6px;
  overflow: hidden;
}

.ws-status {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 4px 10px;
  font-weight: bold;
}

/* 🌟 真实延迟显示器样式 */
.latency-indicator {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 4px 10px;
  border-left: 1px solid #30363d;
  font-family: monospace;
  font-size: 11px;
}

.latency-detail {
  display: flex;
  align-items: center;
  gap: 4px;
}

.part { font-weight: bold; transition: color 0.3s; }
.divider { color: #30363d; }
.latency-text { font-weight: bold; transition: color 0.3s; }

.level-excellent { color: #2ea043; }
.level-good { color: #d29922; }
.level-poor { color: #f85149; }

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

.status-label { color: #8b949e; font-size: 12px; }
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
  width: 30px;
  height: 30px;
  border-radius: 50%;
  display: flex;
  align-items: center;
  justify-content: center;
  font-weight: bold;
  cursor: pointer;
  font-size: 12px;
}
</style>
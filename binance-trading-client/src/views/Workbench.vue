<template>
  <div class="workbench-container">
    <!-- Header Banner -->
    <header class="workbench-header">
      <div class="header-bg-gradient"></div>
      <div class="header-content">
        <div class="welcome-text">
          <h1>QUANT TERMINAL 工作台</h1>
          <p class="subtitle">极速量化交易中枢。当前处于就绪挂起状态，已延迟高频行情与账户流连接，保障系统能耗与网络额度。</p>
        </div>
        <div class="system-time-card">
          <span class="label">终端运行时间 (LOCAL)</span>
          <span class="time">{{ formattedTime }}</span>
          <span class="date">{{ formattedDate }}</span>
        </div>
      </div>
    </header>

    <!-- Main Navigation Grid -->
    <div class="workbench-grid">
      <!-- Quick Launch Trading Card -->
      <div class="workbench-card launch-card" @click="enterTrading">
        <div class="card-glow"></div>
        <div class="card-icon-wrapper">
          <span class="card-icon">⚡</span>
        </div>
        <div class="card-body">
          <h3>实时交易系统</h3>
          <p>模块化网格布局，支持多币种K线联动、频域傅里叶分析、主力大单雷达、可用保证金动态风控及极速下单。</p>
          <button class="btn btn-primary glowing-btn">
            启动交易面板 <span class="arrow">→</span>
          </button>
        </div>
      </div>

      <!-- Strategy Backtest Card -->
      <div class="workbench-card normal-card" @click="layoutStore.setTab('backtest')">
        <div class="card-icon-wrapper">
          <span class="card-icon">📊</span>
        </div>
        <div class="card-body">
          <h3>策略历史回测</h3>
          <p>秒级K线高保真模拟交易，支持 VReversal 反转等策略多参数寻优，输出图形化资金曲线与全面盈亏评估指标。</p>
          <button class="btn btn-secondary">
            打开回测中心 <span class="arrow">→</span>
          </button>
        </div>
      </div>

      <!-- Assets & Risk Control Card -->
      <div class="workbench-card normal-card" @click="layoutStore.setTab('assets')">
        <div class="card-icon-wrapper">
          <span class="card-icon">🛡️</span>
        </div>
        <div class="card-body">
          <h3>资产管理与风控</h3>
          <p>直观呈现各仓位名义总价值、维持保证金率、多头空头连环平仓风险，并集成了资金划转与多路数据缓存管理。</p>
          <button class="btn btn-secondary">
            进入资产看板 <span class="arrow">→</span>
          </button>
        </div>
      </div>
    </div>

    <!-- Bottom Status & System Health Info -->
    <div class="system-info-section">
      <!-- System Logs & Console -->
      <div class="logs-panel">
        <div class="panel-header">
          <span class="terminal-dots">
            <span class="tdot red"></span>
            <span class="tdot yellow"></span>
            <span class="tdot green"></span>
          </span>
          <h4>系统日志控制台 (CONSOLE)</h4>
        </div>
        <div class="logs-container" ref="logContainer">
          <div v-for="(log, index) in systemLogs" :key="index" class="log-line">
            <span class="log-time">[{{ log.time }}]</span>
            <span class="log-level" :class="log.type">{{ log.level }}</span>
            <span class="log-message">{{ log.message }}</span>
          </div>
        </div>
      </div>

      <!-- Health check status -->
      <div class="status-panel">
        <h4>网关连通性监测</h4>
        <div class="status-list">
          <div class="status-row">
            <span class="status-name">本地 C# 转发中继</span>
            <span class="status-value" :class="backendOnline ? 'online' : 'offline'">
              ● {{ backendOnline ? `在线 (${backendLatency}ms)` : '离线' }}
            </span>
          </div>
          <div class="status-row">
            <span class="status-name">币安 API 接口网关</span>
            <span class="status-value warning">● 就绪 (待挂载)</span>
          </div>
          <div class="status-row">
            <span class="status-name">账户 WebSocket 通道</span>
            <span class="status-value warning">● 未连接 (待激活)</span>
          </div>
          <div class="status-row">
            <span class="status-name">高频策略引擎</span>
            <span class="status-value online">● 运行中</span>
          </div>
        </div>

        <div class="performance-chart-mini">
          <div class="chart-header">近期大盘走势 (BTCUSDT Mock)</div>
          <svg class="mini-chart-svg" viewBox="0 0 300 80">
            <defs>
              <linearGradient id="chart-grad" x1="0" y1="0" x2="0" y2="1">
                <stop offset="0%" stop-color="#58a6ff" stop-opacity="0.3"/>
                <stop offset="100%" stop-color="#58a6ff" stop-opacity="0"/>
              </linearGradient>
            </defs>
            <path d="M 0,65 Q 25,40 50,55 T 100,35 T 150,58 T 200,30 T 250,45 T 300,20 L 300,80 L 0,80 Z" fill="url(#chart-grad)" />
            <path d="M 0,65 Q 25,40 50,55 T 100,35 T 150,58 T 200,30 T 250,45 T 300,20" fill="none" stroke="#58a6ff" stroke-width="2" />
            <circle cx="300" cy="20" r="3" fill="#58a6ff" class="pulse-point" />
          </svg>
        </div>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted, onUnmounted, nextTick } from 'vue';
import { useLayoutStore } from '@/store/layout';
import { useMarketStore } from '@/store/market';

const layoutStore = useLayoutStore();
const marketStore = useMarketStore();

const formattedTime = ref('');
const formattedDate = ref('');
const backendOnline = ref(false);
const backendLatency = ref(0);
const logContainer = ref<HTMLDivElement | null>(null);

interface SystemLog {
  time: string;
  level: string;
  type: 'info' | 'success' | 'warning' | 'error';
  message: string;
}

const systemLogs = ref<SystemLog[]>([]);

// Format system times
const updateTime = () => {
  const now = new Date();
  formattedTime.value = now.toTimeString().split(' ')[0];
  
  const year = now.getFullYear();
  const month = String(now.getMonth() + 1).padStart(2, '0');
  const day = String(now.getDate()).padStart(2, '0');
  formattedDate.value = `${year}-${month}-${day}`;
};

// Push a new log to the terminal console
const addLog = (level: string, type: 'info' | 'success' | 'warning' | 'error', message: string) => {
  const now = new Date();
  const timeStr = now.toTimeString().split(' ')[0];
  systemLogs.value.push({
    time: timeStr,
    level,
    type,
    message
  });
  if (systemLogs.value.length > 50) {
    systemLogs.value.shift();
  }
  nextTick(() => {
    if (logContainer.value) {
      logContainer.value.scrollTop = logContainer.value.scrollHeight;
    }
  });
};

// Check connection to the local server
const checkBackendConnection = async () => {
  const start = Date.now();
  try {
    const res = await fetch(`${import.meta.env.VITE_API_BASE_URL}/ping`, { method: 'GET' });
    if (res.ok) {
      if (!backendOnline.value) {
        addLog('SYSTEM', 'success', '本地 C# 转发中继服务器连接成功。');
      }
      backendOnline.value = true;
      backendLatency.value = Date.now() - start;
    } else {
      if (backendOnline.value) {
        addLog('SYSTEM', 'error', '本地 C# 转发中继服务器响应异常，可能正在重启。');
      }
      backendOnline.value = false;
    }
  } catch (e) {
    if (backendOnline.value) {
      addLog('SYSTEM', 'error', '无法连接至本地 C# 中继服务器，网络套接字失效。');
    }
    backendOnline.value = false;
  }
};

const enterTrading = () => {
  addLog('ACTION', 'info', '正在调度进入交易系统，拉取交易所规范与初始化网络通道...');
  layoutStore.setTab('dashboard');
};

let clockTimer: ReturnType<typeof setInterval> | null = null;
let pingTimer: ReturnType<typeof setInterval> | null = null;
let simulatedLogTimer: ReturnType<typeof setInterval> | null = null;

onMounted(() => {
  updateTime();
  clockTimer = setInterval(updateTime, 1000);

  // Initial logs
  addLog('SYSTEM', 'info', 'Quant Terminal 终端内核初始化成功。');
  addLog('CONFIG', 'info', '加载本地缓存配置文件... OK');
  addLog('NET', 'warning', '网络就绪：当前处于省流挂起状态。交易所通信已休眠。');

  checkBackendConnection();
  pingTimer = setInterval(checkBackendConnection, 5000);

  // Generate some realistic running messages
  const sampleMessages = [
    { level: 'CORE', type: 'info', message: '风控校验规则加载完毕，杠杆上限已设置为 20x。' },
    { level: 'STRATEGY', type: 'success', message: 'VReversal 策略反转因子权重计算就绪。' },
    { level: 'CLIENT', type: 'info', message: '系统空闲中，检测到 C# 中继服务已在后台监听接口。' },
    { level: 'SAFETY', type: 'info', message: '多重签名校验通过，API 密钥哈希安全对齐。' }
  ];

  let msgIdx = 0;
  simulatedLogTimer = setInterval(() => {
    if (msgIdx < sampleMessages.length) {
      const msg = sampleMessages[msgIdx++];
      addLog(msg.level, msg.type as any, msg.message);
    } else {
      // Periodic check
      addLog('HEARTBEAT', 'info', `系统自检：本地线程池运行正常，当前中继延迟: ${backendLatency.value}ms。`);
    }
  }, 10000);
});

onUnmounted(() => {
  if (clockTimer) clearInterval(clockTimer);
  if (pingTimer) clearInterval(pingTimer);
  if (simulatedLogTimer) clearInterval(simulatedLogTimer);
});
</script>

<style scoped>
.workbench-container {
  display: flex;
  flex-direction: column;
  height: calc(100vh - 50px);
  width: 100%;
  box-sizing: border-box;
  background-color: #010409;
  padding: 24px;
  overflow-y: auto;
  gap: 24px;
}

/* Header Banner Styling */
.workbench-header {
  position: relative;
  background: #161b22;
  border: 1px solid #30363d;
  border-radius: 12px;
  padding: 30px;
  overflow: hidden;
  display: flex;
  justify-content: space-between;
  align-items: center;
  box-shadow: 0 4px 24px rgba(0, 0, 0, 0.2);
}

.header-bg-gradient {
  position: absolute;
  top: 0;
  left: 0;
  right: 0;
  bottom: 0;
  background: linear-gradient(135deg, rgba(31, 111, 235, 0.1) 0%, rgba(88, 166, 255, 0) 60%);
  pointer-events: none;
}

.header-content {
  position: relative;
  z-index: 2;
  display: flex;
  width: 100%;
  justify-content: space-between;
  align-items: center;
  flex-wrap: wrap;
  gap: 20px;
}

.welcome-text h1 {
  font-size: 26px;
  font-weight: 800;
  margin: 0 0 8px 0;
  background: linear-gradient(90deg, #e6edf3, #58a6ff);
  -webkit-background-clip: text;
  -webkit-text-fill-color: transparent;
  letter-spacing: 1px;
}

.welcome-text .subtitle {
  color: #8b949e;
  font-size: 14px;
  margin: 0;
  max-width: 700px;
  line-height: 1.6;
}

.system-time-card {
  background: rgba(33, 38, 45, 0.8);
  border: 1px solid #30363d;
  border-radius: 8px;
  padding: 12px 20px;
  display: flex;
  flex-direction: column;
  align-items: flex-end;
  min-width: 180px;
  backdrop-filter: blur(8px);
}

.system-time-card .label {
  font-size: 11px;
  color: #8b949e;
  font-weight: bold;
  letter-spacing: 1px;
  margin-bottom: 4px;
}

.system-time-card .time {
  font-size: 24px;
  font-family: monospace;
  font-weight: bold;
  color: #58a6ff;
  text-shadow: 0 0 10px rgba(88, 166, 255, 0.3);
}

.system-time-card .date {
  font-size: 12px;
  color: #484f58;
  font-family: monospace;
  margin-top: 2px;
}

/* Workbench Grid Styling */
.workbench-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(320px, 1fr));
  gap: 24px;
  width: 100%;
}

.workbench-card {
  position: relative;
  background: #161b22;
  border: 1px solid #30363d;
  border-radius: 12px;
  padding: 24px;
  cursor: pointer;
  display: flex;
  gap: 16px;
  transition: all 0.3s cubic-bezier(0.25, 0.8, 0.25, 1);
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.1);
  overflow: hidden;
}

.workbench-card:hover {
  transform: translateY(-4px);
  border-color: #58a6ff;
  box-shadow: 0 8px 30px rgba(88, 166, 255, 0.15);
}

.card-glow {
  position: absolute;
  top: -50%;
  left: -50%;
  width: 200%;
  height: 200%;
  background: radial-gradient(circle, rgba(31, 111, 235, 0.15) 0%, rgba(0, 0, 0, 0) 70%);
  pointer-events: none;
  opacity: 0;
  transition: opacity 0.3s;
}

.workbench-card:hover .card-glow {
  opacity: 1;
}

.card-icon-wrapper {
  font-size: 28px;
  display: flex;
  align-items: flex-start;
  padding-top: 2px;
}

.card-body {
  display: flex;
  flex-direction: column;
  flex: 1;
}

.card-body h3 {
  font-size: 18px;
  margin: 0 0 10px 0;
  color: #f0f6fc;
  font-weight: 700;
}

.card-body p {
  font-size: 13px;
  color: #8b949e;
  line-height: 1.6;
  margin: 0 0 20px 0;
  flex: 1;
}

.btn {
  padding: 8px 16px;
  border-radius: 6px;
  font-weight: bold;
  font-size: 13px;
  cursor: pointer;
  border: 1px solid transparent;
  transition: all 0.2s ease;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: max-content;
}

.btn-primary {
  background-color: #238636;
  color: #ffffff;
}

.btn-primary:hover {
  background-color: #2ea043;
}

.btn-secondary {
  background-color: #21262d;
  color: #c9d1d9;
  border-color: #30363d;
}

.btn-secondary:hover {
  background-color: #30363d;
  border-color: #8b949e;
}

.glowing-btn {
  box-shadow: 0 0 12px rgba(46, 160, 67, 0.3);
  animation: pulse-border 2s infinite;
}

.arrow {
  margin-left: 6px;
  transition: transform 0.2s ease;
}

.workbench-card:hover .arrow {
  transform: translateX(4px);
}

.launch-card {
  border-color: rgba(31, 111, 235, 0.4);
  background: linear-gradient(180deg, #161b22 0%, rgba(22, 27, 34, 0.8) 100%);
}

/* Bottom info styling */
.system-info-section {
  display: flex;
  gap: 24px;
  width: 100%;
  flex-wrap: wrap;
  flex: 1;
  min-height: 250px;
}

.logs-panel {
  flex: 2;
  min-width: 320px;
  background: #161b22;
  border: 1px solid #30363d;
  border-radius: 12px;
  display: flex;
  flex-direction: column;
  overflow: hidden;
  box-shadow: 0 4px 12px rgba(0,0,0,0.15);
}

.logs-panel .panel-header {
  background: #21262d;
  padding: 12px 18px;
  display: flex;
  align-items: center;
  gap: 12px;
  border-bottom: 1px solid #30363d;
}

.terminal-dots {
  display: flex;
  gap: 6px;
}

.tdot {
  width: 10px;
  height: 10px;
  border-radius: 50%;
}

.tdot.red { background: #f85149; }
.tdot.yellow { background: #d29922; }
.tdot.green { background: #2ea043; }

.logs-panel h4 {
  margin: 0;
  font-size: 12px;
  font-family: monospace;
  color: #8b949e;
  letter-spacing: 1px;
}

.logs-container {
  padding: 16px;
  flex: 1;
  overflow-y: auto;
  font-family: 'Courier New', Courier, monospace;
  font-size: 12px;
  background: #0d1117;
  color: #c9d1d9;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.log-line {
  display: flex;
  gap: 8px;
  line-height: 1.4;
  word-break: break-all;
}

.log-time {
  color: #484f58;
  flex-shrink: 0;
}

.log-level {
  font-weight: bold;
  padding: 0 4px;
  border-radius: 2px;
  font-size: 10px;
  flex-shrink: 0;
}

.log-level.info { background: rgba(56, 139, 253, 0.15); color: #58a6ff; }
.log-level.success { background: rgba(46, 160, 67, 0.15); color: #3fb950; }
.log-level.warning { background: rgba(210, 153, 34, 0.15); color: #d29922; }
.log-level.error { background: rgba(248, 81, 73, 0.15); color: #f85149; }

.log-message {
  color: #c9d1d9;
}

/* Status Panel Styling */
.status-panel {
  flex: 1;
  min-width: 280px;
  background: #161b22;
  border: 1px solid #30363d;
  border-radius: 12px;
  padding: 20px;
  display: flex;
  flex-direction: column;
  gap: 16px;
  box-shadow: 0 4px 12px rgba(0,0,0,0.15);
}

.status-panel h4 {
  margin: 0;
  font-size: 13px;
  color: #f0f6fc;
  font-weight: 700;
  border-bottom: 1px solid #30363d;
  padding-bottom: 8px;
}

.status-list {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.status-row {
  display: flex;
  justify-content: space-between;
  align-items: center;
  font-size: 12px;
}

.status-name {
  color: #8b949e;
}

.status-value {
  font-weight: bold;
  font-family: monospace;
}

.status-value.online { color: #3fb950; }
.status-value.offline { color: #f85149; }
.status-value.warning { color: #d29922; }

.performance-chart-mini {
  margin-top: auto;
  background: #0d1117;
  border: 1px solid #30363d;
  border-radius: 8px;
  padding: 10px;
  overflow: hidden;
}

.chart-header {
  font-size: 11px;
  color: #8b949e;
  margin-bottom: 6px;
  font-weight: bold;
}

.mini-chart-svg {
  width: 100%;
  height: 60px;
}

.pulse-point {
  animation: pulse-dot 1.5s infinite;
}

@keyframes pulse-border {
  0% { box-shadow: 0 0 0 0 rgba(46, 160, 67, 0.4); }
  70% { box-shadow: 0 0 0 6px rgba(46, 160, 67, 0); }
  100% { box-shadow: 0 0 0 0 rgba(46, 160, 67, 0); }
}

@keyframes pulse-dot {
  0% { transform: scale(0.9); opacity: 1; }
  50% { transform: scale(1.4); opacity: 0.5; }
  100% { transform: scale(0.9); opacity: 1; }
}
</style>

<template>
  <div class="kline-module">
    <div class="kline-toolbar">
      <div class="intervals">
        <button v-for="tf in timeframes" :key="tf" :class="{ active: tf === currentTf }" @click="changeInterval(tf)">
          {{ tf }}
        </button>
      </div>
      
      <div class="drawing-tools">
        <div class="chart-type-selector">
          <button :class="{ active: localChartType === 'standard' }" @click="changeChartType('standard')">标准</button>
          <button :class="{ active: localChartType === 'heikinAshi' }" @click="changeChartType('heikinAshi')">平均(HA)</button>
        </div>
        
        <span class="divider">|</span>
        
        <select class="draw-select" v-model="currentDrawMode" @change="startDrawing">
          <option value="">✏️ 选择画线工具...</option>
          <option value="segment">📏 趋势线 (线段)</option>
          <option value="horizontalStraightLine">➖ 水平支撑/阻力线</option>
          <option value="rayLine">↗️ 射线</option>
          <option value="priceChannelLine">⏸️ 平行价格通道</option>
          <option value="fibonacciLine">📶 斐波那契回调线</option>
        </select>
        
        <button @click="clearDrawings" title="清除所有画线">🗑️</button>
      </div>
      
      <div class="symbol-info">{{ symbol }}</div>
    </div>
    
    <div class="chart-container" ref="chartContainer" @mousedown.stop @touchstart.stop @pointerdown.stop></div>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted, onUnmounted, watch, computed } from 'vue';
import { init, dispose } from 'klinecharts';
import { useMarketStore } from '@/store/market';
import { MarketAPI } from '@/api/market'; 

const props = defineProps<{ symbol: string }>();
const marketStore = useMarketStore();
const chartContainer = ref<HTMLElement | null>(null);

const timeframes = ['1m', '5m', '15m', '1h', '4h', '1d'];
const currentTf = ref('1m'); 

const localChartType = ref('standard'); 
const currentDrawMode = ref(''); 

const currentChartData = ref<any[]>([]);
let chart: any = null;
let resizeObserver: ResizeObserver | null = null;

// ==========================================
// 核心算法：Heikin Ashi 计算 
// ==========================================
const calculateHeikinAshi = (rawData: any[]) => {
  const haData = [];
  let prevHA = null;
  for (const raw of rawData) {
    const ha = { timestamp: raw.timestamp, open: 0, high: 0, low: 0, close: 0, volume: raw.volume };
    ha.close = (raw.open + raw.high + raw.low + raw.close) / 4;
    
    if (!prevHA) {
      ha.open = (raw.open + raw.close) / 2;
    } else {
      ha.open = (prevHA.open + prevHA.close) / 2;
    }
    
    ha.high = Math.max(raw.high, ha.open, ha.close);
    ha.low = Math.min(raw.low, ha.open, ha.close);
    
    haData.push(ha);
    prevHA = ha;
  }
  return haData;
};

// ==========================================
// KLineChart 数据渲染控制
// ==========================================
const applyCurrentChartType = () => {
  if (!chart || currentChartData.value.length === 0) return;
  
  if (localChartType.value === 'heikinAshi') {
    const haData = calculateHeikinAshi(currentChartData.value);
    // 🌟 稳定版 V9 API: applyNewData
    chart.applyNewData(haData);
  } else {
    chart.applyNewData(currentChartData.value);
  }
};

const changeChartType = (type: string) => {
  localChartType.value = type;
  applyCurrentChartType();
};

const startDrawing = () => {
  if (currentDrawMode.value && chart) {
    // 🌟 稳定版 V9 API: createShape
    chart.createShape({
      name: currentDrawMode.value,
      lock: false
    });
    currentDrawMode.value = '';
  }
};

const clearDrawings = () => {
  // 🌟 稳定版 V9 API: removeShape
  if (chart) chart.removeShape(); 
};

// ==========================================
// 数据格式化与加载
// ==========================================
const formatApiData = (history: any[]) => {
  return history.map(item => ({
    timestamp: item.time * 1000, // 必须是毫秒
    open: item.open,
    high: item.high,
    low: item.low,
    close: item.close,
    volume: item.volume || 0
  }));
};

const loadHistory = async (symbol: string, interval: string) => {
  const history = await MarketAPI.getHistoricalKlines(symbol, interval, 1000);
  if (history && history.length > 0) {
    const formatted = formatApiData(history);
    
    const safeHistory = formatted
      .sort((a: any, b: any) => a.timestamp - b.timestamp)
      .filter((item: any, index: number, array: any[]) => index === 0 || item.timestamp !== array[index - 1].timestamp);

    currentChartData.value = safeHistory;
    applyCurrentChartType();
  }
};

const changeInterval = async (tf: string) => {
  if (tf === currentTf.value) return;
  marketStore.unsubscribeKline(props.symbol, currentTf.value);
  currentTf.value = tf;
  await loadHistory(props.symbol, tf);
  marketStore.subscribeKline(props.symbol, tf);
};

// ==========================================
// 初始化图表实例
// ==========================================
onMounted(async () => {
  if (!chartContainer.value) return;

  chart = init(chartContainer.value, {
    styles: {
      grid: {
        horizontal: { color: '#21262d', size: 1, style: 'dashed' },
        vertical: { color: '#21262d', size: 1, style: 'dashed' }
      },
      candle: {
        type: 'candle_solid',
        bar: {
          upColor: '#2ea043', downColor: '#f85149', noChangeColor: '#8b949e',
          upBorderColor: '#2ea043', downBorderColor: '#f85149', noChangeBorderColor: '#8b949e',
          upWickColor: '#2ea043', downWickColor: '#f85149', noChangeWickColor: '#8b949e'
        },
        tooltip: {
          labels: ['时间: ', '开: ', '收: ', '高: ', '低: ', '成交量: '],
          text: { color: '#c9d1d9', size: 12, weight: 'normal' }
        }
      },
      xAxis: { axisLine: { color: '#30363d' }, tickText: { color: '#8b949e' } },
      yAxis: { axisLine: { color: '#30363d' }, tickText: { color: '#8b949e' } }
    }
  });

  // 🌟 稳定版 V9 API: loadMore
  chart.loadMore(async (timestamp: number) => {
    const olderHistory = await MarketAPI.getHistoricalKlines(props.symbol, currentTf.value, 1000, timestamp - 1);
    if (olderHistory && olderHistory.length > 0) {
      const formatted = formatApiData(olderHistory);
      currentChartData.value = [...formatted, ...currentChartData.value];
      
      if (localChartType.value === 'heikinAshi') {
        const haData = calculateHeikinAshi(currentChartData.value);
        // 🌟 稳定版 V9 API: applyMoreData
        chart.applyMoreData(haData.slice(0, olderHistory.length));
      } else {
        chart.applyMoreData(formatted);
      }
    }
  });

  resizeObserver = new ResizeObserver((entries) => {
    if (entries.length === 0 || entries[0].target !== chartContainer.value) return;
    const newRect = entries[0].contentRect;
    if (newRect.width === 0 || newRect.height === 0) return;
    // 🌟 稳定版 V9 API: resize
    chart.resize(); 
  });
  resizeObserver.observe(chartContainer.value);

  marketStore.connectWs();
  await loadHistory(props.symbol, currentTf.value);
  marketStore.subscribeKline(props.symbol, currentTf.value);
});

// ==========================================
// 响应 WebSocket 数据推流
// ==========================================
const currentKlineData = computed(() => marketStore.latestKlines[`${props.symbol}_${currentTf.value}`]);

watch(currentKlineData, (newVal) => {
  if (newVal && chart) {
    const rawFormat = {
      timestamp: newVal.time, 
      open: newVal.open, high: newVal.high, low: newVal.low, close: newVal.close, volume: newVal.volume || 0
    };

    if (currentChartData.value.length > 0) {
      const lastIndex = currentChartData.value.length - 1;
      if (currentChartData.value[lastIndex].timestamp === rawFormat.timestamp) {
        currentChartData.value[lastIndex] = rawFormat; 
      } else if (rawFormat.timestamp > currentChartData.value[lastIndex].timestamp) {
        currentChartData.value.push(rawFormat); 
      }
    }

    if (localChartType.value === 'heikinAshi') {
      const haData = calculateHeikinAshi(currentChartData.value);
      // 🌟 稳定版 V9 API: updateData
      chart.updateData(haData[haData.length - 1]);
    } else {
      chart.updateData(rawFormat);
    }
  }
}, { deep: true });

onUnmounted(() => {
  marketStore.unsubscribeKline(props.symbol, currentTf.value);
  if (resizeObserver && chartContainer.value) resizeObserver.unobserve(chartContainer.value);
  if (chart) {
    dispose(chartContainer.value!);
  }
});
</script>

<style scoped>
.kline-module { width: 100%; height: 100%; display: flex; flex-direction: column; background: #0d1117; }
.kline-toolbar { display: flex; justify-content: space-between; align-items: center; padding: 6px 12px; background: #161b22; border-bottom: 1px solid #21262d; flex-shrink: 0; }
.intervals button { background: transparent; border: 1px solid transparent; color: #8b949e; padding: 3px 6px; border-radius: 4px; cursor: pointer; font-size: 12px; }
.intervals button:hover { background: #21262d; color: #c9d1d9; }
.intervals button.active { background: #2ea043; color: #ffffff; font-weight: bold; }
.drawing-tools { display: flex; align-items: center; gap: 6px; }
.chart-type-selector { display: flex; background: #0d1117; border-radius: 4px; padding: 2px; }
.chart-type-selector button { background: transparent; border: none; color: #8b949e; padding: 2px 8px; font-size: 12px; cursor: pointer; border-radius: 2px; }
.chart-type-selector button.active { background: #30363d; color: #c9d1d9; font-weight: bold; }
.draw-select { background: #0d1117; color: #c9d1d9; border: 1px solid #30363d; border-radius: 4px; padding: 3px 6px; font-size: 12px; outline: none; cursor: pointer; }
.drawing-tools > button { background: transparent; border: 1px solid #30363d; color: #8b949e; padding: 3px 8px; border-radius: 4px; cursor: pointer; font-size: 12px; transition: all 0.2s; }
.drawing-tools > button:hover { border-color: #8b949e; color: #c9d1d9; }
.divider { color: #30363d; margin: 0 2px; }
.symbol-info { font-size: 14px; font-weight: bold; color: #e6edf3; }
.chart-container { flex: 1; width: 100%; position: relative; }
</style>
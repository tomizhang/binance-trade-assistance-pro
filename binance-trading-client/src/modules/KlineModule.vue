<template>
  <div class="kline-module">
    <div class="kline-toolbar">
      <div class="intervals">
        <button v-for="tf in timeframes" :key="tf" :class="{ active: tf === currentTf }" @click="changeInterval(tf)">
          {{ tf }}
        </button>
      </div>
      
      <div class="drawing-tools">
        <button :class="{ active: marketStore.isSyncEnabled }" @click="marketStore.toggleSync" title="开启后自动同步">
          🔗 同步
        </button>
        <span class="divider">|</span>
        <button :class="{ active: isDrawingMode }" @click="toggleDrawingMode">✏️ 画水平线</button>
        <button v-if="localLines.length > 0 || (marketStore.globalLines[symbol] && marketStore.globalLines[symbol].length > 0)" @click="clearLines">
          🗑️ 清除
        </button>
      </div>
      <div class="symbol-info">{{ symbol }}</div>
    </div>

    <div class="kline-legend">
      <template v-if="displayData">
        <span class="time">{{ formatTime(displayData.time) }}</span>
        <span class="label">开</span><span :class="getColor(displayData.open, displayData.close)">{{ displayData.open.toFixed(2) }}</span>
        <span class="label">高</span><span :class="getColor(displayData.open, displayData.close)">{{ displayData.high.toFixed(2) }}</span>
        <span class="label">低</span><span :class="getColor(displayData.open, displayData.close)">{{ displayData.low.toFixed(2) }}</span>
        <span class="label">收</span><span :class="getColor(displayData.open, displayData.close)">{{ displayData.close.toFixed(2) }}</span>
        <span class="label">涨跌</span>
        <span :class="getChangeColor(displayData.changePercent)">
          {{ displayData.changePercent > 0 ? '+' : '' }}{{ displayData.changePercent.toFixed(2) }}%
        </span>
        <span v-if="isLoadingHistory" class="loading-text">加载历史中...</span>
      </template>
      <template v-else>
        <span class="waiting">等待数据推流...</span>
      </template>
    </div>
    
    <div class="chart-container" :class="{ 'drawing-cursor': isDrawingMode }" ref="chartContainer" @mousedown.stop @touchstart.stop @pointerdown.stop></div>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted, onUnmounted, watch, computed } from 'vue';
import { createChart, CandlestickSeries } from 'lightweight-charts';
import { useMarketStore } from '@/store/market';
import { MarketAPI } from '@/api/market'; 

const props = defineProps<{ symbol: string }>();
const instanceId = Math.random().toString(36).substring(2, 10);
const marketStore = useMarketStore();
const chartContainer = ref<HTMLElement | null>(null);

const timeframes = ['1m', '5m', '15m', '1h', '4h', '1d'];
const currentTf = ref('1m'); 

// 状态
const latestCandle = ref<any>(null);
const hoveredCandle = ref<any>(null);
const isDrawingMode = ref(false);
const localLines = ref<any[]>([]);

// 🌟 新增：历史数据分页状态管理
const currentChartData = ref<any[]>([]);
const isLoadingHistory = ref(false);
const noMoreHistory = ref(false);

const displayData = computed(() => {
  const data = hoveredCandle.value || latestCandle.value;
  if (!data) return null;
  const changePercent = ((data.close - data.open) / data.open) * 100;
  return { ...data, changePercent };
});

// 🌟 时间格式统一：yyyy-MM-dd hh:mm:ss
const formatTime = (timestamp: number) => {
  const d = new Date(timestamp * 1000);
  const pad = (n: number) => n.toString().padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
};

const getColor = (open: number, close: number) => close >= open ? 'text-green' : 'text-red';
const getChangeColor = (change: number) => change >= 0 ? 'text-green' : 'text-red';
const toggleDrawingMode = () => isDrawingMode.value = !isDrawingMode.value;

const clearLines = () => {
  localLines.value.forEach(line => candleSeries?.removePriceLine(line));
  localLines.value = [];
  if (marketStore.isSyncEnabled) {
    marketStore.clearGlobalLines(props.symbol);
  }
};

let chart: any = null;
let candleSeries: any = null;
let resizeObserver: ResizeObserver | null = null;

// ==========================================
// 历史数据拉取与无缝滚动
// ==========================================

// 初次加载最新 1000 根
const loadHistory = async (symbol: string, interval: string) => {
  isLoadingHistory.value = true;
  noMoreHistory.value = false;
  
  const history = await MarketAPI.getHistoricalKlines(symbol, interval, 1000);
  
  if (history && history.length > 0 && candleSeries) {
    try {
      const safeHistory = history
        .sort((a: any, b: any) => a.time - b.time)
        .filter((item: any, index: number, array: any[]) => index === 0 || item.time !== array[index - 1].time);

      currentChartData.value = safeHistory;
      candleSeries.setData(safeHistory);
      latestCandle.value = safeHistory[safeHistory.length - 1]; 
      
      // 仅在初次加载时自适应缩放宽度
      chart.timeScale().fitContent();
    } catch (err) {
      console.error('setData 失败:', err);
    }
  }
  isLoadingHistory.value = false;
};

// 🌟 新增：向左滑动触底时，拉取更老的历史数据
const loadMoreHistory = async () => {
  if (isLoadingHistory.value || noMoreHistory.value || currentChartData.value.length === 0) return;

  isLoadingHistory.value = true;
  // 拿到当前图表最左侧（最老）的那根 K 线时间
  const oldestTimeMs = currentChartData.value[0].time * 1000;

  // 请求在此时间之前的 1000 根 K 线
  const olderHistory = await MarketAPI.getHistoricalKlines(props.symbol, currentTf.value, 1000, oldestTimeMs - 1);

  if (olderHistory && olderHistory.length > 0) {
    // 将老数据拼接到现有的前面
    const mergedData = [...olderHistory, ...currentChartData.value];
    const safeData = mergedData
      .sort((a: any, b: any) => a.time - b.time)
      .filter((item: any, index: number, array: any[]) => index === 0 || item.time !== array[index - 1].time);

    currentChartData.value = safeData;
    candleSeries.setData(safeData); // setData 注入全量数据，底层会自动保持当前的视图滚动位置不跳跃

    if (olderHistory.length < 1000) {
      noMoreHistory.value = true; // 已经拉到创世区块了
    }
  } else {
    noMoreHistory.value = true;
  }
  isLoadingHistory.value = false;
};

const changeInterval = async (tf: string) => {
  if (tf === currentTf.value) return;
  marketStore.unsubscribeKline(props.symbol, currentTf.value);
  currentTf.value = tf;
  if (!marketStore.isSyncEnabled) clearLines(); 
  await loadHistory(props.symbol, tf);
  marketStore.subscribeKline(props.symbol, tf);
};

// ==========================================
// 挂载与事件中心
// ==========================================
onMounted(async () => {
  if (!chartContainer.value) return;

  chart = createChart(chartContainer.value, {
    layout: { background: { color: 'transparent' }, textColor: '#8b949e' },
    grid: { vertLines: { color: '#23272e' }, horzLines: { color: '#23272e' } },
    crosshair: { mode: 0 },
    // 🌟 应用时间格式化
    localization: {
      timeFormatter: (time: number) => formatTime(time),
      locale: 'zh-CN',
    },
    timeScale: { timeVisible: true, secondsVisible: true },
    width: chartContainer.value.clientWidth,
    height: chartContainer.value.clientHeight,
  });

  candleSeries = chart.addSeries(CandlestickSeries, {
    upColor: '#26a69a', downColor: '#ef5350', borderVisible: false,
    wickUpColor: '#26a69a', wickDownColor: '#ef5350'
  });

  // 🌟 监听时间轴滚动事件，触发行情分页加载
  chart.timeScale().subscribeVisibleLogicalRangeChange((logicalRange: any) => {
    if (logicalRange) {
      // 当左侧可视边缘剩下不到 50 根 K 线时，提前静默预加载上一页
      if (logicalRange.from < 50) {
        loadMoreHistory();
      }
    }
  });

  resizeObserver = new ResizeObserver((entries) => {
    if (entries.length === 0 || entries[0].target !== chartContainer.value) return;
    const newRect = entries[0].contentRect;
    if (newRect.width === 0 || newRect.height === 0) return;
    chart.applyOptions({ width: newRect.width, height: newRect.height });
  });
  resizeObserver.observe(chartContainer.value);

  chart.subscribeCrosshairMove((param: any) => {
    if (!param.time || param.point.x < 0 || param.point.y < 0) {
      hoveredCandle.value = null;
      if (marketStore.isSyncEnabled) marketStore.clearCrosshair(props.symbol, instanceId);
      return;
    }
    const data = param.seriesData.get(candleSeries);
    if (data) hoveredCandle.value = data;
    if (marketStore.isSyncEnabled) {
      const price = candleSeries.coordinateToPrice(param.point.y);
      marketStore.setCrosshair(props.symbol, price, param.time as number, instanceId);
    }
  });

  chart.subscribeClick((param: any) => {
    if (!isDrawingMode.value || !param.point) return;
    const price = candleSeries.coordinateToPrice(param.point.y);
    if (price !== null) {
      if (marketStore.isSyncEnabled) {
        marketStore.addGlobalLine(props.symbol, price);
      } else {
        const line = candleSeries.createPriceLine({
          price: price, color: '#58a6ff', lineWidth: 2, lineStyle: 2, axisLabelVisible: true, title: '本地',
        });
        localLines.value.push(line);
      }
      isDrawingMode.value = false; 
    }
  });

  marketStore.connectWs();
  await loadHistory(props.symbol, currentTf.value);
  marketStore.subscribeKline(props.symbol, currentTf.value);
});

// ==========================================
// 响应式监听
// ==========================================
watch(() => props.symbol, async (newSymbol) => {
  if (newSymbol) await loadHistory(newSymbol, currentTf.value);
});

watch(() => marketStore.crosshairData[props.symbol], (newVal) => {
  if (!marketStore.isSyncEnabled || !newVal || newVal.sourceId === instanceId) return;
  if (newVal.time === 0) {
    chart.clearCrosshairPosition(); 
  } else {
    try { chart.setCrosshairPosition(newVal.price, newVal.time, candleSeries); } catch (e) {}
  }
}, { deep: true });

watch(() => marketStore.globalLines[props.symbol], (newLines) => {
  if (!marketStore.isSyncEnabled) return;
  localLines.value.forEach(line => candleSeries.removePriceLine(line));
  localLines.value = [];
  if (newLines && newLines.length > 0) {
    newLines.forEach(price => {
      const line = candleSeries.createPriceLine({
        price: price, color: '#ff7b72', lineWidth: 2, lineStyle: 2, axisLabelVisible: true, title: '同步',
      });
      localLines.value.push(line);
    });
  }
}, { deep: true });

const currentKlineData = computed(() => marketStore.latestKlines[`${props.symbol}_${currentTf.value}`]);

watch(currentKlineData, (newVal) => {
  if (newVal && candleSeries) {
    try {
      const formattedData = {
        time: Math.floor(newVal.time / 1000), 
        open: newVal.open, high: newVal.high, low: newVal.low, close: newVal.close,
      };
      
      candleSeries.update(formattedData);
      latestCandle.value = formattedData; 

      // 🌟 新增：将最新收到的跳动数据同步存入本地缓存，防止再次拉取历史时数据断层
      if (currentChartData.value.length > 0) {
        const lastIndex = currentChartData.value.length - 1;
        if (currentChartData.value[lastIndex].time === formattedData.time) {
          currentChartData.value[lastIndex] = formattedData; 
        } else if (formattedData.time > currentChartData.value[lastIndex].time) {
          currentChartData.value.push(formattedData); 
        }
      }
    } catch (err) {
      console.error('更新失败:', err);
    }
  }
}, { deep: true });

onUnmounted(() => {
  marketStore.unsubscribeKline(props.symbol, currentTf.value);
  if (resizeObserver && chartContainer.value) {
    resizeObserver.unobserve(chartContainer.value);
    resizeObserver.disconnect();
  }
  if (chart) chart.remove();
});
</script>

<style scoped>
.kline-module { width: 100%; height: 100%; display: flex; flex-direction: column; position: relative; overflow: hidden; }
.kline-toolbar { display: flex; justify-content: space-between; align-items: center; padding: 6px 12px; background: #161b22; border-bottom: 1px solid #21262d; flex-shrink: 0; }
.divider { color: #30363d; margin: 0 5px; }
.drawing-tools button { background: transparent; border: 1px solid #30363d; color: #8b949e; padding: 3px 8px; border-radius: 4px; cursor: pointer; font-size: 12px; margin-right: 5px; transition: all 0.2s; }
.drawing-tools button:hover { border-color: #8b949e; color: #c9d1d9; }
.drawing-tools button.active { background: #1f6feb; color: white; border-color: #1f6feb; }
.intervals button { background: transparent; border: 1px solid transparent; color: #8b949e; padding: 3px 6px; border-radius: 4px; cursor: pointer; font-size: 12px; }
.intervals button:hover { background: #21262d; color: #c9d1d9; }
.intervals button.active { background: #238636; color: #ffffff; font-weight: bold; }
.symbol-info { font-size: 14px; font-weight: bold; color: #c9d1d9; }
.kline-legend { display: flex; gap: 12px; padding: 4px 12px; background: #0d1117; font-size: 12px; color: #c9d1d9; border-bottom: 1px solid #21262d; flex-shrink: 0; }
.kline-legend .label { color: #8b949e; margin-right: -8px; }
.kline-legend .time { color: #58a6ff; font-weight: bold; margin-right: 10px; }
.kline-legend .waiting { color: #8b949e; font-style: italic; }
.kline-legend .loading-text { color: #e3b341; font-style: italic; margin-left: auto; }
.text-green { color: #26a69a; font-family: monospace; }
.text-red { color: #ef5350; font-family: monospace; }
.chart-container { flex: 1; width: 100%; background-color: #0d1117; }
.drawing-cursor { cursor: crosshair !important; }
:deep(.tv-lightweight-charts) { width: 100% !important; height: 100% !important; }
</style>
<template>
  <div class="kline-module">
    <div class="kline-toolbar">
      <div class="intervals">
        <button 
          v-for="tf in timeframes" 
          :key="tf" 
          :class="{ active: tf === currentTf }"
          @click="changeInterval(tf)"
        >
          {{ tf }}
        </button>
      </div>
      
      <div class="drawing-tools">
        <button :class="{ active: isDrawingMode }" @click="toggleDrawingMode">
          ✏️ 画水平线
        </button>
        <button v-if="priceLines.length > 0" @click="clearLines">🗑️ 清除</button>
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
      </template>
      <template v-else>
        <span class="waiting">等待数据...</span>
      </template>
    </div>
    
    <div 
      class="chart-container" 
      :class="{ 'drawing-cursor': isDrawingMode }"
      ref="chartContainer"
      @mousedown.stop
      @touchstart.stop
      @pointerdown.stop
    ></div>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted, onUnmounted, watch, computed } from 'vue';
import { createChart, CandlestickSeries } from 'lightweight-charts';
import { useMarketStore } from '@/store/market';
import { MarketAPI } from '@/api/market'; 

const props = defineProps<{ symbol: string }>();

const timeframes = ['1m', '5m', '15m', '1h', '4h', '1d'];
const currentTf = ref('1m'); 
const chartContainer = ref<HTMLElement | null>(null);
const marketStore = useMarketStore();

// 状态：悬浮数据与画线
const latestCandle = ref<any>(null);
const hoveredCandle = ref<any>(null);
const isDrawingMode = ref(false);
const priceLines = ref<any[]>([]);

const displayData = computed(() => {
  const data = hoveredCandle.value || latestCandle.value;
  if (!data) return null;
  const changePercent = ((data.close - data.open) / data.open) * 100;
  return { ...data, changePercent };
});

const formatTime = (timestamp: number) => {
  const d = new Date(timestamp * 1000);
  return `${d.getMonth()+1}-${d.getDate()} ${d.getHours().toString().padStart(2, '0')}:${d.getMinutes().toString().padStart(2, '0')}`;
};
const getColor = (open: number, close: number) => close >= open ? 'text-green' : 'text-red';
const getChangeColor = (change: number) => change >= 0 ? 'text-green' : 'text-red';

const toggleDrawingMode = () => isDrawingMode.value = !isDrawingMode.value;
const clearLines = () => {
  if (candleSeries) {
    priceLines.value.forEach(line => candleSeries.removePriceLine(line));
    priceLines.value = [];
  }
};

let chart: any = null;
let candleSeries: any = null;
let resizeObserver: ResizeObserver | null = null;

const loadHistory = async (symbol: string, interval: string) => {
  const history = await MarketAPI.getHistoricalKlines(symbol, interval, 1000);
  if (!history || history.length === 0) return;

  if (candleSeries) {
    try {
      const safeHistory = history
        .sort((a: any, b: any) => a.time - b.time)
        .filter((item: any, index: number, array: any[]) => index === 0 || item.time !== array[index - 1].time);

      candleSeries.setData(safeHistory);
      latestCandle.value = safeHistory[safeHistory.length - 1]; 
    } catch (err) {
      console.error('setData 失败:', err);
    }
  }
};

const changeInterval = async (tf: string) => {
  if (tf === currentTf.value) return;
  marketStore.unsubscribeKline(props.symbol, currentTf.value);
  currentTf.value = tf;
  clearLines(); 
  await loadHistory(props.symbol, tf);
  marketStore.subscribeKline(props.symbol, tf);
};

onMounted(async () => {
  if (!chartContainer.value) return;

  chart = createChart(chartContainer.value, {
    layout: { background: { color: 'transparent' }, textColor: '#8b949e' },
    grid: { vertLines: { color: '#23272e' }, horzLines: { color: '#23272e' } },
    crosshair: { mode: 0 },
    timeScale: { timeVisible: true, secondsVisible: false },
    width: chartContainer.value.clientWidth,
    height: chartContainer.value.clientHeight,
  });

  candleSeries = chart.addSeries(CandlestickSeries, {
    upColor: '#26a69a', downColor: '#ef5350', borderVisible: false,
    wickUpColor: '#26a69a', wickDownColor: '#ef5350'
  });

  resizeObserver = new ResizeObserver((entries) => {
    if (entries.length === 0 || entries[0].target !== chartContainer.value) return;
    const newRect = entries[0].contentRect;
    chart.applyOptions({ width: newRect.width, height: newRect.height });
  });
  resizeObserver.observe(chartContainer.value);

  // 悬浮显示详情
  chart.subscribeCrosshairMove((param: any) => {
    if (!param.time || param.point.x < 0 || param.point.y < 0) {
      hoveredCandle.value = null;
      return;
    }
    const data = param.seriesData.get(candleSeries);
    if (data) hoveredCandle.value = data;
  });

  // 画线交互
  chart.subscribeClick((param: any) => {
    if (!isDrawingMode.value || !param.point) return;
    const price = candleSeries.coordinateToPrice(param.point.y);
    if (price !== null) {
      const line = candleSeries.createPriceLine({
        price: price, color: '#58a6ff', lineWidth: 2, lineStyle: 2, axisLabelVisible: true, title: '标注',
      });
      priceLines.value.push(line);
      isDrawingMode.value = false; // 画完一根自动退出模式
    }
  });

  marketStore.connectWs();
  await loadHistory(props.symbol, currentTf.value);
  marketStore.subscribeKline(props.symbol, currentTf.value);
});

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
.text-green { color: #26a69a; font-family: monospace; }
.text-red { color: #ef5350; font-family: monospace; }
.chart-container { flex: 1; width: 100%; background-color: #0d1117; }
.drawing-cursor { cursor: crosshair !important; }
:deep(.tv-lightweight-charts) { width: 100% !important; height: 100% !important; }
</style>
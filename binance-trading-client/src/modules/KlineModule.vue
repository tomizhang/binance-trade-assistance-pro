<template>
  <div 
    class="kline-module" 
    :class="{ 'is-focused': isFocused }"
    @click="takeFocus"
  >
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
        
        <button 
          class="sync-btn" 
          :class="{ active: showVolume }" 
          @click="toggleVolume" 
          title="显示/隐藏副图成交量"
        >
          📊 成交量
        </button>

        <span class="divider">|</span>
        
        <button 
          class="sync-btn" 
          :class="{ active: marketStore.isSyncEnabled }" 
          @click="marketStore.toggleSync()" 
          title="同币种跨屏同步"
        >
          🔗 同步
        </button>
      </div>
      
      <div class="symbol-info-wrapper">
        <span class="symbol-info">{{ symbol }}</span>
        <span v-if="isFocused" class="focus-badge">🟢 操作中</span>
      </div>
    </div>
    
    <div class="chart-wrapper">
      <div class="chart-legend" v-if="hoverData">
        <span class="legend-time">{{ hoverData.time }}</span>
        <span class="legend-item">开: <span :class="hoverData.colorClass">{{ hoverData.open }}</span></span>
        <span class="legend-item">高: <span :class="hoverData.colorClass">{{ hoverData.high }}</span></span>
        <span class="legend-item">低: <span :class="hoverData.colorClass">{{ hoverData.low }}</span></span>
        <span class="legend-item">收: <span :class="hoverData.colorClass">{{ hoverData.close }}</span></span>
        <span class="legend-item" v-if="showVolume">量: <span class="vol-text">{{ hoverData.vol }}</span></span>
      </div>

      <div 
        class="chart-container" 
        ref="chartContainer"
        @mousedown.stop
        @touchstart.stop
        @pointerdown.stop
        @wheel.stop
        @touchmove.stop
        @contextmenu.prevent
      ></div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted, onUnmounted, watch, computed } from 'vue';
import { createChart, CandlestickSeries, HistogramSeries, CrosshairMode, LineStyle, IChartApi } from 'lightweight-charts';
import { useMarketStore } from '@/store/market';
import { MarketAPI } from '@/api/market'; 

const props = defineProps<{ symbol: string }>();
const marketStore = useMarketStore();
const chartContainer = ref<HTMLElement | null>(null);

const isFocused = computed(() => marketStore.currentSymbol === props.symbol);
const takeFocus = () => { if (!isFocused.value) marketStore.setCurrentSymbol(props.symbol); };

const timeframes = ['1m', '5m', '15m', '1h', '4h', '1d'];
const currentTf = ref('1m'); 
const localChartType = ref('standard'); 
const showVolume = ref(true);

const currentChartData = ref<any[]>([]);

let chart: IChartApi | null = null;
let candleSeries: any = null;
let volumeSeries: any = null;
let resizeObserver: ResizeObserver | null = null;
let positionLineId: any = null;

// ==========================================
// 🌟 悬浮数据摘要 (Tooltip) 状态
// ==========================================
const hoverData = ref<any>(null);

// 辅助函数：格式化时间戳为 yyyy-MM-dd HH:mm:ss
const formatDateTime = (timestamp: number) => {
  const date = new Date(timestamp * 1000); // Lightweight 给的是秒，转回毫秒
  const y = date.getFullYear();
  const m = String(date.getMonth() + 1).padStart(2, '0');
  const d = String(date.getDate()).padStart(2, '0');
  const H = String(date.getHours()).padStart(2, '0');
  const M = String(date.getMinutes()).padStart(2, '0');
  const S = String(date.getSeconds()).padStart(2, '0');
  return `${y}-${m}-${d} ${H}:${M}:${S}`;
};

// ==========================================
// 数据格式化 
// ==========================================
const formatApiData = (history: any[]) => {
  return history; 
};

// ==========================================
// 动态精度设置
// ==========================================
const updateChartPrecision = (data: any[]) => {
  if (!candleSeries || data.length === 0) return;
  let maxDecimals = 2; 
  const sampleData = data.slice(-20);
  for (const item of sampleData) {
    const priceStr = String(item.close);
    if (priceStr.includes('.')) {
      const decimals = priceStr.split('.')[1].length;
      if (decimals > maxDecimals) maxDecimals = decimals;
    }
  }
  maxDecimals = Math.min(maxDecimals, 8);
  
  candleSeries.applyOptions({
    priceFormat: { type: 'price', precision: maxDecimals, minMove: 1 / Math.pow(10, maxDecimals) }
  });
};

// ==========================================
// 渲染图表类型
// ==========================================
const calculateHeikinAshi = (rawData: any[]) => {
  const haData = [];
  let prevHA: any = null;
  for (const raw of rawData) {
    const ha = { time: raw.time, open: 0, high: 0, low: 0, close: 0, value: raw.value, color: raw.color };
    ha.close = (Number(raw.open) + Number(raw.high) + Number(raw.low) + Number(raw.close)) / 4;
    if (!prevHA) { ha.open = (Number(raw.open) + Number(raw.close)) / 2; } 
    else { ha.open = (Number(prevHA.open) + Number(prevHA.close)) / 2; }
    ha.high = Math.max(Number(raw.high), ha.open, ha.close);
    ha.low = Math.min(Number(raw.low), ha.open, ha.close);
    ha.color = ha.close >= ha.open ? 'rgba(38, 166, 154, 0.5)' : 'rgba(239, 83, 80, 0.5)';
    haData.push(ha);
    prevHA = ha;
  }
  return haData;
};

const applyDataToSeries = (data: any[]) => {
  if (!candleSeries || !volumeSeries) return;
  
  const uniqueData = data.filter((item, index, self) => index === 0 || item.time !== self[index - 1].time);
  const finalData = localChartType.value === 'heikinAshi' ? calculateHeikinAshi(uniqueData) : uniqueData;
  
  candleSeries.setData(finalData.map((d: any) => ({
    time: Number(d.time) > 9999999999 ? Math.floor(Number(d.time) / 1000) : Number(d.time),
    open: Number(d.open), high: Number(d.high), low: Number(d.low), close: Number(d.close)
  })));

  console.log('vol data',finalData);
  volumeSeries.setData(finalData.map((d: any) => {
    const isUp = Number(d.close) >= Number(d.open);
    return {
      time: Number(d.time) > 9999999999 ? Math.floor(Number(d.time) / 1000) : Number(d.time),
      value: Number(d.value !== undefined ? d.value : (d.volume !== undefined ? d.volume : (d.vol || 0))),
      color: d.color || (isUp ? 'rgba(38, 166, 154, 0.5)' : 'rgba(239, 83, 80, 0.5)')
    };
  }));
  
  updateChartPrecision(finalData);
};

// ==========================================
// 初始化与历史拉取
// ==========================================
const getIntervalSec = (tf: string) => {
  const v = parseInt(tf);
  const u = tf.slice(-1);
  return (u === 'm' ? v * 60 : u === 'h' ? v * 3600 : u === 'd' ? v * 86400 : 60);
};

const loadHistory = async (symbol: string, interval: string) => {
  try {
    const history = await MarketAPI.getHistoricalKlines(symbol, interval, 1000);
    if (!history || history.length === 0) return;
    currentChartData.value = formatApiData(history);
    applyDataToSeries(currentChartData.value);
  } catch (error) {
    console.error(`❌ [网络异常] K 线历史接口请求失败:`, error);
  }
};

let isLoadingMoreHistory = false;
const loadMoreHistory = async () => {
  if (isLoadingMoreHistory || currentChartData.value.length === 0) return;
  isLoadingMoreHistory = true;

  const rawOldest = Number(currentChartData.value[0].time);
  const oldestTimeSec = rawOldest > 9999999999 ? Math.floor(rawOldest / 1000) : rawOldest;
  const targetEndTimeSec = oldestTimeSec - getIntervalSec(currentTf.value);
  const targetEndTimeMs = targetEndTimeSec * 1000;

  try {
    const olderHistory = await MarketAPI.getHistoricalKlines(props.symbol, currentTf.value, 1000, targetEndTimeMs);
    if (olderHistory && olderHistory.length > 0) {
      const formatted = formatApiData(olderHistory);
      const safeNewData = formatted.filter(item => {
        const itemTime = Number(item.time) > 9999999999 ? Math.floor(Number(item.time) / 1000) : Number(item.time);
        return itemTime < oldestTimeSec;
      });
      
      if (safeNewData.length > 0) {
        currentChartData.value = [...safeNewData, ...currentChartData.value];
        applyDataToSeries(currentChartData.value);
      }
    }
  } catch (e) {
    console.error("加载历史失败:", e);
  } finally {
    isLoadingMoreHistory = false;
  }
};

onMounted(async () => {
  if (!chartContainer.value) return;

  chart = createChart(chartContainer.value, {
    layout: { textColor: '#8b949e', background: { type: 'solid', color: '#0d1117' } },
    grid: { vertLines: { color: '#21262d', style: LineStyle.Dotted }, horzLines: { color: '#21262d', style: LineStyle.Dotted } },
    crosshair: { mode: CrosshairMode.Normal, vertLine: { labelBackgroundColor: '#1f6feb' }, horzLine: { labelBackgroundColor: '#1f6feb' } },
    timeScale: { 
      borderColor: '#30363d', 
      timeVisible: true,
      secondsVisible: true, // 允许显示秒
    },
    // 🌟 核心修复 1：拦截图表内部的日期格式化，替换为你指定的 YYYY-MM-DD HH:mm:ss
    localization: {
      timeFormatter: (businessDayOrTimestamp: any) => {
        // 判断是否为有效的 Unix 时间戳 (秒)
        if (typeof businessDayOrTimestamp === 'number') {
          return formatDateTime(businessDayOrTimestamp);
        }
        return String(businessDayOrTimestamp);
      }
    },
    rightPriceScale: { 
      borderColor: '#30363d',
      scaleMargins: { top: 0.05, bottom: 0.25 }
    }
  });

  candleSeries = chart.addSeries(CandlestickSeries, {
    upColor: '#2ea043', downColor: '#f85149', 
    borderVisible: false,
    wickUpColor: '#2ea043', wickDownColor: '#f85149'
  });

  volumeSeries = chart.addSeries(HistogramSeries, {
    priceFormat: { type: 'volume' },
    priceScaleId: '', 
    visible: showVolume.value
  });
  
  volumeSeries.priceScale().applyOptions({
    scaleMargins: { top: 0.8, bottom: 0 }
  });

  chart.timeScale().subscribeVisibleLogicalRangeChange((logicalRange) => {
    if (logicalRange && logicalRange.from < 10 && !isLoadingMoreHistory) {
      loadMoreHistory();
    }
  });

  // 🌟 核心修复 2：监听十字光标移动，动态提取 K 线数据更新给摘要面板
  chart.subscribeCrosshairMove((param) => {
    // 鼠标移出图表范围，隐藏面板
    if (
      param.point === undefined ||
      !param.time ||
      param.point.x < 0 ||
      param.point.x > chartContainer.value!.clientWidth ||
      param.point.y < 0 ||
      param.point.y > chartContainer.value!.clientHeight
    ) {
      hoverData.value = null;
      return;
    }

    // 从引擎内部捞取当前光标所在的那根 K 线数据
    const candleData: any = param.seriesData.get(candleSeries);
    const volData: any = param.seriesData.get(volumeSeries);

    if (candleData) {
      const isUp = candleData.close >= candleData.open;
      hoverData.value = {
        time: formatDateTime(Number(param.time)), // 格式化悬浮时间
        open: candleData.open.toFixed(2),
        high: candleData.high.toFixed(2),
        low: candleData.low.toFixed(2),
        close: candleData.close.toFixed(2),
        vol: volData && volData.value !== undefined ? Number(volData.value).toFixed(2) : '0.00',
        colorClass: isUp ? 'text-up' : 'text-down' // 用于控制涨跌颜色
      };
    } else {
      hoverData.value = null;
    }
  });

  chart.subscribeClick(() => takeFocus());

  resizeObserver = new ResizeObserver(entries => {
    if (entries[0].contentRect.width === 0) return;
    chart?.applyOptions({ width: chartContainer.value!.clientWidth, height: chartContainer.value!.clientHeight });
  });
  resizeObserver.observe(chartContainer.value);

  await loadHistory(props.symbol, currentTf.value);
  marketStore.subscribeKline(props.symbol, currentTf.value);
});

// ==========================================
// 实时数据更新 
// ==========================================
const currentKlineData = computed(() => marketStore.latestKlines[`${props.symbol}_${currentTf.value}`]);
watch(currentKlineData, (newVal) => {
  if (newVal && candleSeries && volumeSeries) {
    const rawTime = Number(newVal.time);
    const timeInSeconds = rawTime > 9999999999 ? Math.floor(rawTime / 1000) : rawTime;

    const rawFormat = {
      time: timeInSeconds, 
      open: Number(newVal.open), high: Number(newVal.high), 
      low: Number(newVal.low), close: Number(newVal.close), 
      value: Number(newVal.volume !== undefined ? newVal.volume : (newVal.vol || 0)),
      color: Number(newVal.close) >= Number(newVal.open) ? 'rgba(38, 166, 154, 0.5)' : 'rgba(239, 83, 80, 0.5)'
    };

    if (currentChartData.value.length > 0) {
      const lastIndex = currentChartData.value.length - 1;
      const lastTimeSec = Number(currentChartData.value[lastIndex].time) > 9999999999 
        ? Math.floor(Number(currentChartData.value[lastIndex].time) / 1000) 
        : Number(currentChartData.value[lastIndex].time);

      if (lastTimeSec === rawFormat.time) {
        currentChartData.value[lastIndex] = newVal; 
      } else if (rawFormat.time > lastTimeSec) {
        currentChartData.value.push(newVal);
      }
    }

    if (localChartType.value === 'heikinAshi') {
      const haData = calculateHeikinAshi(currentChartData.value);
      const latestHA = haData[haData.length - 1];
      candleSeries.update(latestHA);
      volumeSeries.update({ time: latestHA.time, value: latestHA.value, color: latestHA.color });
    } else {
      candleSeries.update(rawFormat);
      volumeSeries.update({ time: rawFormat.time, value: rawFormat.value, color: rawFormat.color });
    }
  }
}, { deep: true });

// ==========================================
// 绘制持仓盈亏线
// ==========================================
const getCurrentPrice = () => marketStore.marketTickers[props.symbol]?.lastPrice || 0;
watch([() => marketStore.positions, () => marketStore.marketTickers[props.symbol]?.lastPrice], () => {
  if (!candleSeries) return;
  const pos = marketStore.positions.find(p => p.symbol === props.symbol);
  
  if (positionLineId) {
    candleSeries.removePriceLine(positionLineId);
    positionLineId = null;
  }

  if (pos) {
    const currentPrice = getCurrentPrice() || pos.entryPrice;
    const pnl = pos.side === 'LONG' 
      ? (currentPrice - pos.entryPrice) * Math.abs(pos.amount)
      : (pos.entryPrice - currentPrice) * Math.abs(pos.amount);
      
    positionLineId = candleSeries.createPriceLine({
      price: pos.entryPrice,
      color: pnl >= 0 ? '#2ea043' : '#f85149',
      lineWidth: 2,
      lineStyle: LineStyle.Dashed,
      axisLabelVisible: true,
      title: `${pos.side === 'LONG' ? '做多' : '做空'} ${Math.abs(pos.amount)} | ${pnl >= 0 ? '+' : ''}${pnl.toFixed(2)}`,
    });
  }
}, { deep: true });

const changeInterval = async (tf: string) => {
  if (tf === currentTf.value) return;
  marketStore.unsubscribeKline(props.symbol, currentTf.value);
  currentTf.value = tf;
  await loadHistory(props.symbol, tf);
  marketStore.subscribeKline(props.symbol, tf);
};

const changeChartType = (type: string) => {
  localChartType.value = type;
  applyDataToSeries(currentChartData.value);
};

const toggleVolume = () => {
  showVolume.value = !showVolume.value;
  if (volumeSeries) {
    volumeSeries.applyOptions({ visible: showVolume.value });
  }
};

onUnmounted(() => {
  marketStore.unsubscribeKline(props.symbol, currentTf.value);
  if (resizeObserver && chartContainer.value) resizeObserver.unobserve(chartContainer.value);
  if (chart) {
    chart.remove();
    chart = null;
  }
});
</script>

<style scoped>
.kline-module { width: 100%; height: 100%; display: flex; flex-direction: column; background: #0d1117; border: 1px solid transparent; transition: all 0.2s ease; box-sizing: border-box; }
.kline-module.is-focused { border-color: #58a6ff; box-shadow: inset 0 0 10px rgba(88, 166, 255, 0.1); }
.kline-toolbar { display: flex; justify-content: space-between; align-items: center; padding: 6px 12px; background: #161b22; border-bottom: 1px solid #21262d; flex-shrink: 0; z-index: 2; }
.intervals button { background: transparent; border: 1px solid transparent; color: #8b949e; padding: 3px 6px; border-radius: 4px; cursor: pointer; font-size: 12px; }
.intervals button:hover { background: #21262d; color: #c9d1d9; }
.intervals button.active { background: #2ea043; color: #ffffff; font-weight: bold; }
.drawing-tools { display: flex; align-items: center; gap: 6px; }
.chart-type-selector { display: flex; background: #0d1117; border-radius: 4px; padding: 2px; }
.chart-type-selector button { background: transparent; border: none; color: #8b949e; padding: 2px 8px; font-size: 12px; cursor: pointer; border-radius: 2px; }
.chart-type-selector button.active { background: #30363d; color: #c9d1d9; font-weight: bold; }
.sync-btn { background: transparent; border: 1px solid #30363d; color: #8b949e; padding: 3px 8px; border-radius: 4px; cursor: pointer; font-size: 12px; transition: all 0.2s; }
.sync-btn.active { background: #1f6feb; color: white; border-color: #1f6feb; }
.divider { color: #30363d; margin: 0 2px; }
.symbol-info-wrapper { display: flex; align-items: center; gap: 8px; }
.symbol-info { font-size: 14px; font-weight: bold; color: #e6edf3; }
.focus-badge { background: #1f6feb; color: #ffffff; font-size: 10px; padding: 2px 6px; border-radius: 10px; font-weight: normal; }

/* 🌟 新增：图表容器层与面板样式 */
.chart-wrapper { flex: 1; position: relative; width: 100%; display: flex; flex-direction: column; overflow: hidden; }
.chart-container { flex: 1; width: 100%; position: relative; }

/* 🌟 新增：Legend 面板，悬浮在图表左上角，彻底杜绝挡住鼠标 */
.chart-legend {
  position: absolute;
  top: 8px;
  left: 12px;
  z-index: 10;
  display: flex;
  gap: 12px;
  font-size: 12px;
  pointer-events: none; /* 让鼠标事件穿透面板，继续响应底下的图表拖拽 */
  background: rgba(13, 17, 23, 0.75);
  padding: 4px 8px;
  border-radius: 4px;
}
.legend-time { color: #8b949e; font-weight: bold; margin-right: 4px; }
.legend-item { color: #8b949e; }
.vol-text { color: #c9d1d9; font-weight: bold; }
.text-up { color: #2ea043; font-weight: bold; }
.text-down { color: #f85149; font-weight: bold; }
</style>
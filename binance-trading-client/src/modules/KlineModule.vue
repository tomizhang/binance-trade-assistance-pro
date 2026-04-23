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
          :class="{ active: isDrawingLine }" 
          @click="toggleDrawMode" 
          title="点击后，在图表任意位置点击即可绘制水平线"
        >
          ➖ 水平线
        </button>
        
        <button 
          v-if="drawnLines.length > 0"
          class="sync-btn clear-btn" 
          @click="clearAllLines(true)" 
          title="清除所有画线"
        >
          🗑️
        </button>

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
          title="开启后，所有图表的十字光标与缩放平移将完全同步"
        >
          🔗 同步
        </button>
      </div>
      
      <div class="symbol-info-wrapper">
        <span class="symbol-info">{{ symbol }}</span>
        <span v-if="isFocused" class="focus-badge">🟢 操作中</span>
      </div>
    </div>
    
    <div class="chart-wrapper" :class="{ 'is-drawing-mode': isDrawingLine }">
      <div class="chart-legend" v-if="hoverData">
        <span class="legend-time">{{ hoverData.time }}</span>
        <span class="legend-item">开: <span :class="hoverData.colorClass">{{ hoverData.open }}</span></span>
        <span class="legend-item">高: <span :class="hoverData.colorClass">{{ hoverData.high }}</span></span>
        <span class="legend-item">低: <span :class="hoverData.colorClass">{{ hoverData.low }}</span></span>
        <span class="legend-item">收: <span :class="hoverData.colorClass">{{ hoverData.close }}</span></span>
        <span class="legend-item" v-if="showVolume">量: <span class="vol-text">{{ hoverData.vol }}</span></span>
      </div>

      <div class="line-settings-panel" v-if="selectedLineId && !isDrawingLine">
        <span class="setting-title">✏️ 编辑水平线</span>
        <input type="color" :value="selectedLineColor" @input="updateLineColor" title="调整线条颜色" />
        <button class="action-btn delete-btn" @click="deleteSelectedLine">🗑️ 删除</button>
        <button class="action-btn" @click="deselectLine">❌ 取消</button>
      </div>

      <div 
        class="chart-container no-drag" 
        :class="{ 'is-resizing': isHoveringLine || draggingLineId }"
        ref="chartContainer"
        @pointerdown.stop="onPointerDown"
        @pointermove.stop="onPointerMove"
        @pointerup.stop="onPointerUp"
        @pointerleave.stop="onPointerUp"
        @wheel.stop
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

const instanceId = Math.random().toString(36).substring(2, 10);

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

const hoverData = ref<any>(null);

// ==========================================
// 画线引擎状态
// ==========================================
const isDrawingLine = ref(false);
const drawnLines = ref<{ id: string, price: number, color: string, obj: any }[]>([]);
const selectedLineId = ref<string | null>(null);
const draggingLineId = ref<string | null>(null);
const isHoveringLine = ref(false);

const selectedLineColor = computed(() => {
  const line = drawnLines.value.find(l => l.id === selectedLineId.value);
  return line ? line.color : '#58a6ff';
});

const toggleDrawMode = () => {
  isDrawingLine.value = !isDrawingLine.value;
  if (isDrawingLine.value) deselectLine();
};

const deselectLine = () => {
  selectedLineId.value = null;
  drawnLines.value.forEach(l => l.obj.applyOptions({ lineWidth: 2 }));
};

const addHorizontalLine = (id: string, price: number, color: string, isSource: boolean = false) => {
  if (!candleSeries) return;
  
  const lineObj = candleSeries.createPriceLine({
    price: price,
    color: color,
    lineWidth: 2,
    lineStyle: LineStyle.Solid,
    axisLabelVisible: true,
    title: '',
  });
  
  drawnLines.value.push({ id, price, color, obj: lineObj });

  if (isSource && marketStore.isSyncEnabled) {
    window.dispatchEvent(new CustomEvent('sync-drawing', {
      detail: { action: 'add', id, price, color, symbol: props.symbol, sourceId: instanceId }
    }));
  }
};

const updateLineColor = (e: Event) => {
  const newColor = (e.target as HTMLInputElement).value;
  const line = drawnLines.value.find(l => l.id === selectedLineId.value);
  if (line && candleSeries) {
    line.color = newColor;
    line.obj.applyOptions({ color: newColor });
    if (marketStore.isSyncEnabled) {
      window.dispatchEvent(new CustomEvent('sync-drawing', {
        detail: { action: 'color', id: line.id, color: newColor, symbol: props.symbol, sourceId: instanceId }
      }));
    }
  }
};

const deleteSelectedLine = () => {
  if (selectedLineId.value && candleSeries) {
    const id = selectedLineId.value;
    const line = drawnLines.value.find(l => l.id === id);
    if (line) {
      candleSeries.removePriceLine(line.obj);
      drawnLines.value = drawnLines.value.filter(l => l.id !== id);
      if (marketStore.isSyncEnabled) {
        window.dispatchEvent(new CustomEvent('sync-drawing', {
          detail: { action: 'delete', id, symbol: props.symbol, sourceId: instanceId }
        }));
      }
    }
    selectedLineId.value = null;
  }
};

const clearAllLines = (isSource: boolean = false) => {
  if (!candleSeries) return;
  drawnLines.value.forEach(line => candleSeries.removePriceLine(line.obj));
  drawnLines.value = [];
  selectedLineId.value = null;

  if (isSource && marketStore.isSyncEnabled) {
    window.dispatchEvent(new CustomEvent('sync-drawing', {
      detail: { action: 'clear', symbol: props.symbol, sourceId: instanceId }
    }));
  }
};

// ==========================================
// 🌟 核心修复：纯血 Pointer Events 接管交互
// ==========================================
const handleDown = (clientY: number) => {
  if (!candleSeries || !chartContainer.value) return;
  const rect = chartContainer.value.getBoundingClientRect();
  const y = clientY - rect.top;

  const hitLine = drawnLines.value.find(line => {
    const lineY = candleSeries.priceToCoordinate(line.price);
    return lineY !== null && Math.abs(lineY - y) < 10;
  });

  if (hitLine) {
    selectedLineId.value = hitLine.id;
    draggingLineId.value = hitLine.id;
    
    drawnLines.value.forEach(l => l.obj.applyOptions({ lineWidth: l.id === hitLine.id ? 4 : 2 }));
    chart?.applyOptions({ handleScroll: false, handleScale: false });
  } else if (!isDrawingLine.value) {
    deselectLine();
  }
};

const handleMove = (clientY: number) => {
  if (!candleSeries || !chartContainer.value) return;
  const rect = chartContainer.value.getBoundingClientRect();
  const y = clientY - rect.top;

  if (draggingLineId.value) {
    const newPrice = candleSeries.coordinateToPrice(y);
    if (newPrice !== null) {
      const line = drawnLines.value.find(l => l.id === draggingLineId.value);
      if (line) {
        line.price = newPrice;
        line.obj.applyOptions({ price: newPrice });
      }
    }
  } else {
    const hitLine = drawnLines.value.find(line => {
      const lineY = candleSeries.priceToCoordinate(line.price);
      return lineY !== null && Math.abs(lineY - y) < 10;
    });
    isHoveringLine.value = !!hitLine;
  }
};

// 🌟 统一使用 Pointer 事件映射
const onPointerDown = (e: PointerEvent) => handleDown(e.clientY);
const onPointerMove = (e: PointerEvent) => handleMove(e.clientY);

const onPointerUp = () => {
  if (draggingLineId.value) {
    // 恢复图表原生操作权限
    chart?.applyOptions({
      handleScroll: true,
      handleScale: true
    });

    const line = drawnLines.value.find(l => l.id === draggingLineId.value);
    if (line && marketStore.isSyncEnabled) {
      window.dispatchEvent(new CustomEvent('sync-drawing', {
        detail: { action: 'move', id: line.id, price: line.price, symbol: props.symbol, sourceId: instanceId }
      }));
    }
    draggingLineId.value = null;
  }
};

const onSyncDrawing = (e: any) => {
  if (!marketStore.isSyncEnabled) return;
  const { action, id, price, color, symbol, sourceId } = e.detail;
  
  if (sourceId !== instanceId && symbol === props.symbol) {
    if (action === 'add') addHorizontalLine(id, price, color, false);
    else if (action === 'clear') clearAllLines(false);
    else if (action === 'move') {
      const line = drawnLines.value.find(l => l.id === id);
      if (line && candleSeries) {
        line.price = price;
        line.obj.applyOptions({ price });
      }
    }
    else if (action === 'color') {
      const line = drawnLines.value.find(l => l.id === id);
      if (line) {
        line.color = color;
        line.obj.applyOptions({ color });
      }
    }
    else if (action === 'delete') {
      const line = drawnLines.value.find(l => l.id === id);
      if (line && candleSeries) {
        candleSeries.removePriceLine(line.obj);
        drawnLines.value = drawnLines.value.filter(l => l.id !== id);
        if (selectedLineId.value === id) selectedLineId.value = null;
      }
    }
  }
};

const formatDateTime = (timestamp: number) => {
  const date = new Date(timestamp * 1000); 
  const y = date.getFullYear();
  const m = String(date.getMonth() + 1).padStart(2, '0');
  const d = String(date.getDate()).padStart(2, '0');
  const H = String(date.getHours()).padStart(2, '0');
  const M = String(date.getMinutes()).padStart(2, '0');
  const S = String(date.getSeconds()).padStart(2, '0');
  return `${y}-${m}-${d} ${H}:${M}:${S}`;
};

const formatApiData = (history: any[]) => { return history; };

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

let isSyncingRange = false;
const onSyncRange = (e: any) => {
  if (!chart || !marketStore.isSyncEnabled) return;
  const { range, sourceId } = e.detail;
  if (sourceId !== instanceId) {
    isSyncingRange = true; 
    chart.timeScale().setVisibleLogicalRange(range);
    setTimeout(() => { isSyncingRange = false; }, 50); 
  }
};

onMounted(async () => {
  if (!chartContainer.value) return;

  window.addEventListener('sync-logical-range', onSyncRange);
  window.addEventListener('sync-drawing', onSyncDrawing);

  chart = createChart(chartContainer.value, {
    layout: { textColor: '#8b949e', background: { type: 'solid', color: '#0d1117' } },
    grid: { vertLines: { color: '#21262d', style: LineStyle.Dotted }, horzLines: { color: '#21262d', style: LineStyle.Dotted } },
    crosshair: { mode: CrosshairMode.Normal, vertLine: { labelBackgroundColor: '#1f6feb' }, horzLine: { labelBackgroundColor: '#1f6feb' } },
    timeScale: { borderColor: '#30363d', timeVisible: true, secondsVisible: true },
    localization: {
      timeFormatter: (businessDayOrTimestamp: any) => {
        if (typeof businessDayOrTimestamp === 'number') {
          return formatDateTime(businessDayOrTimestamp);
        }
        return String(businessDayOrTimestamp);
      }
    },
    rightPriceScale: { borderColor: '#30363d', scaleMargins: { top: 0.05, bottom: 0.25 } }
  });

  candleSeries = chart.addSeries(CandlestickSeries, { upColor: '#2ea043', downColor: '#f85149', borderVisible: false, wickUpColor: '#2ea043', wickDownColor: '#f85149' });
  volumeSeries = chart.addSeries(HistogramSeries, { priceFormat: { type: 'volume' }, priceScaleId: '', visible: showVolume.value });
  volumeSeries.priceScale().applyOptions({ scaleMargins: { top: 0.8, bottom: 0 } });

  chart.timeScale().subscribeVisibleLogicalRangeChange((logicalRange) => {
    if (logicalRange && logicalRange.from < 10 && !isLoadingMoreHistory) loadMoreHistory();
    if (marketStore.isSyncEnabled && !isSyncingRange && logicalRange) {
      window.dispatchEvent(new CustomEvent('sync-logical-range', { detail: { range: logicalRange, sourceId: instanceId } }));
    }
  });

  chart.subscribeCrosshairMove((param) => {
    if (
      param.point === undefined || !param.time ||
      param.point.x < 0 || param.point.x > chartContainer.value!.clientWidth ||
      param.point.y < 0 || param.point.y > chartContainer.value!.clientHeight
    ) {
      hoverData.value = null;
      if (marketStore.isSyncEnabled && marketStore.updateGlobalCrosshair) {
        marketStore.updateGlobalCrosshair(0, 0, props.symbol, instanceId);
      }
      return;
    }

    const candleData: any = param.seriesData.get(candleSeries);
    const volData: any = param.seriesData.get(volumeSeries);

    if (candleData) {
      const isUp = candleData.close >= candleData.open;
      hoverData.value = {
        time: formatDateTime(Number(param.time)),
        open: candleData.open.toFixed(2), high: candleData.high.toFixed(2),
        low: candleData.low.toFixed(2), close: candleData.close.toFixed(2),
        vol: volData && volData.value !== undefined ? Number(volData.value).toFixed(2) : '0.00',
        colorClass: isUp ? 'text-up' : 'text-down' 
      };

      if (marketStore.isSyncEnabled && marketStore.updateGlobalCrosshair) {
        marketStore.updateGlobalCrosshair(Number(param.time), candleData.close, props.symbol, instanceId);
      }
    } else {
      hoverData.value = null;
    }
  });

  chart.subscribeClick((param) => {
    if (isDrawingLine.value && param.point) {
      const price = candleSeries.coordinateToPrice(param.point.y);
      if (price !== null) {
        const newId = Math.random().toString(36).substring(2, 10);
        addHorizontalLine(newId, price, '#58a6ff', true);
      }
      isDrawingLine.value = false;
      return;
    }
    takeFocus();
  });

  resizeObserver = new ResizeObserver(entries => {
    if (entries[0].contentRect.width === 0) return;
    chart?.applyOptions({ width: chartContainer.value!.clientWidth, height: chartContainer.value!.clientHeight });
  });
  resizeObserver.observe(chartContainer.value);

  await loadHistory(props.symbol, currentTf.value);
  marketStore.subscribeKline(props.symbol, currentTf.value);
});

watch(() => marketStore.globalCrosshairTime, () => {
  if (!chart || !candleSeries || !marketStore.isSyncEnabled) return;
  const allCrosshairs = marketStore.crosshairData;
  if (!allCrosshairs) return;

  const remoteCrosshair = Object.values(allCrosshairs as Record<string, any>).find(
    (c: any) => c.sourceId !== instanceId && c.time > 0
  );

  if (!remoteCrosshair) {
    chart.clearCrosshairPosition();
    return;
  }
  chart.setCrosshairPosition(remoteCrosshair.price, remoteCrosshair.time, candleSeries);
});

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

      if (lastTimeSec === rawFormat.time) currentChartData.value[lastIndex] = newVal; 
      else if (rawFormat.time > lastTimeSec) currentChartData.value.push(newVal);
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
    const pnl = pos.side === 'LONG' ? (currentPrice - pos.entryPrice) * Math.abs(pos.amount) : (pos.entryPrice - currentPrice) * Math.abs(pos.amount);
      
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
  if (volumeSeries) volumeSeries.applyOptions({ visible: showVolume.value });
};

onUnmounted(() => {
  window.removeEventListener('sync-logical-range', onSyncRange);
  window.removeEventListener('sync-drawing', onSyncDrawing);
  marketStore.unsubscribeKline(props.symbol, currentTf.value);
  if (resizeObserver && chartContainer.value) resizeObserver.unobserve(chartContainer.value);
  if (chart) { chart.remove(); chart = null; }
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
.sync-btn.active { background: #58a6ff; color: white; border-color: #58a6ff; }
.clear-btn:hover { border-color: #f85149 !important; color: #f85149 !important; }
.divider { color: #30363d; margin: 0 2px; }
.symbol-info-wrapper { display: flex; align-items: center; gap: 8px; }
.symbol-info { font-size: 14px; font-weight: bold; color: #e6edf3; }
.focus-badge { background: #1f6feb; color: #ffffff; font-size: 10px; padding: 2px 6px; border-radius: 10px; font-weight: normal; }

.chart-wrapper { flex: 1; position: relative; width: 100%; display: flex; flex-direction: column; overflow: hidden; transition: box-shadow 0.2s; }
.is-drawing-mode { box-shadow: inset 0 0 15px rgba(88, 166, 255, 0.2); cursor: crosshair; }
.chart-container { flex: 1; width: 100%; position: relative; }
.is-resizing { cursor: ns-resize !important; }

/* 悬浮数据面板 */
.chart-legend {
  position: absolute;
  top: 8px;
  left: 12px;
  z-index: 10;
  display: flex;
  gap: 12px;
  font-size: 12px;
  pointer-events: none;
  background: rgba(13, 17, 23, 0.75);
  padding: 4px 8px;
  border-radius: 4px;
}
.legend-time { color: #8b949e; font-weight: bold; margin-right: 4px; }
.legend-item { color: #8b949e; }
.vol-text { color: #c9d1d9; font-weight: bold; }
.text-up { color: #2ea043; font-weight: bold; }
.text-down { color: #f85149; font-weight: bold; }

/* 🌟 画线设置面板 */
.line-settings-panel {
  position: absolute;
  top: 8px;
  right: 12px;
  z-index: 10;
  display: flex;
  align-items: center;
  gap: 8px;
  background: rgba(22, 27, 34, 0.85);
  border: 1px solid #30363d;
  padding: 6px 12px;
  border-radius: 6px;
  backdrop-filter: blur(4px);
}
.setting-title { font-size: 12px; color: #8b949e; margin-right: 4px; }
.line-settings-panel input[type="color"] { background: transparent; border: none; width: 24px; height: 24px; cursor: pointer; padding: 0; }
.action-btn { background: transparent; border: 1px solid #30363d; color: #c9d1d9; padding: 2px 8px; border-radius: 4px; cursor: pointer; font-size: 12px; transition: 0.2s; }
.action-btn:hover { background: #30363d; }
.delete-btn:hover { border-color: #f85149; color: #f85149; }
</style>
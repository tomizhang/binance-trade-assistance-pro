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
        
        <select class="draw-select" v-model="currentDrawMode" @change="startDrawing">
          <option value="">✏️ 画线...</option>
          <option value="segment">📏 趋势线</option>
          <option value="horizontalStraightLine">➖ 水平线</option>
          <option value="rayLine">↗️ 射线</option>
          <option value="priceChannelLine">⏸️ 价格通道</option>
          <option value="fibonacciLine">📶 斐波那契</option>
        </select>
        
        <button 
          class="sync-btn" 
          :class="{ active: marketStore.isSyncEnabled }" 
          @click="marketStore.toggleSync()" 
          title="同币种跨屏同步"
        >
          🔗 同步
        </button>

        <button @click="clearDrawings" title="清除所有画线">🗑️</button>
      </div>
      
      <div class="symbol-info-wrapper">
        <span class="symbol-info">{{ symbol }}</span>
        <span v-if="isFocused" class="focus-badge">🟢 操作中</span>
      </div>
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

// 唯一 ID 防循环同步
const instanceId = Math.random().toString(36).substring(2, 10);

// ==========================================
// 全局焦点逻辑
// ==========================================
const isFocused = computed(() => marketStore.currentSymbol === props.symbol);
const takeFocus = () => {
  if (!isFocused.value) {
    marketStore.setCurrentSymbol(props.symbol);
  }
};

const timeframes = ['1m', '5m', '15m', '1h', '4h', '1d'];
const currentTf = ref('1m'); 
const localChartType = ref('standard'); 
const currentDrawMode = ref(''); 

const currentChartData = ref<any[]>([]);
let chart: any = null;
let resizeObserver: ResizeObserver | null = null;

// ==========================================
// 🌟 1. 十字光标同步接收 (已优化：严格限制同币种)
// ==========================================
watch(() => marketStore.globalCrosshairTime, () => {
  if (!chart || !marketStore.isSyncEnabled) return;

  const allCrosshairs = marketStore.crosshairData;
  const remoteCrosshair = Object.values(allCrosshairs as Record<string, any>).find(
    (c: any) => c.sourceId !== instanceId && c.time > 0
  );

  // 每次触发先清理旧的光标线
  chart.removeOverlay({ id: 'sync-v' });
  chart.removeOverlay({ id: 'sync-h' });

  if (!remoteCrosshair) return;

  // 获取发送者的币种
  const sourceSymbol = Object.keys(allCrosshairs).find(s => (allCrosshairs as any)[s] === remoteCrosshair);

  // 🚨 核心优化：只有币种相同时才显示同步十字架
  if (sourceSymbol !== props.symbol) return;

  // 1. 垂直时间线
  chart.createOverlay({
    name: 'verticalStraightLine',
    id: 'sync-v',
    lock: true,
    points: [{ timestamp: remoteCrosshair.time }],
    styles: { line: { color: '#58a6ff', style: 'dashed', size: 1 } }
  });

  // 2. 水平价格线 (自由模式)
  if (remoteCrosshair.price > 0) {
    chart.createOverlay({
      name: 'horizontalStraightLine',
      id: 'sync-h',
      lock: true,
      points: [{ value: remoteCrosshair.price }],
      styles: { line: { color: '#58a6ff', style: 'dashed', size: 1 } }
    });
  }
});

// ==========================================
// 🌟 2. 画线(Overlay)同步接收 (已优化：严格限制同币种)
// ==========================================
watch(() => marketStore.lastOverlayEvent, (event) => {
  if (!event || !chart || !marketStore.isSyncEnabled) return;
  
  // 🚨 增加同币种校验
  if (event.symbol === props.symbol && event.sourceId !== instanceId) {
    if (event.action === 'clear') {
      chart.removeOverlay();
    } else if (event.action === 'add' && event.data) {
      chart.createOverlay({
        name: event.data.name,
        points: event.data.points,
        lock: false
      });
    }
  }
});

const startDrawing = () => {
  if (currentDrawMode.value && chart) {
    chart.createOverlay({
      name: currentDrawMode.value,
      lock: false,
      onDrawEnd: (event: any) => {
        if (marketStore.isSyncEnabled && event.overlay) {
          marketStore.broadcastOverlay({
            action: 'add',
            symbol: props.symbol,
            sourceId: instanceId,
            data: {
              name: event.overlay.name,
              points: event.overlay.points
            }
          });
        }
        return true; 
      }
    });
    currentDrawMode.value = ''; 
  }
};

const clearDrawings = () => {
  if (chart) {
    chart.removeOverlay(); 
    if (marketStore.isSyncEnabled) {
      marketStore.broadcastOverlay({
        action: 'clear',
        symbol: props.symbol,
        sourceId: instanceId
      });
    }
  }
};

const calculateHeikinAshi = (rawData: any[]) => {
  const haData = [];
  let prevHA = null;
  for (const raw of rawData) {
    const ha = { timestamp: raw.timestamp, open: 0, high: 0, low: 0, close: 0, volume: raw.volume };
    ha.close = (raw.open + raw.high + raw.low + raw.close) / 4;
    if (!prevHA) { ha.open = (raw.open + raw.close) / 2; } 
    else { ha.open = (prevHA.open + prevHA.close) / 2; }
    ha.high = Math.max(raw.high, ha.open, ha.close);
    ha.low = Math.min(raw.low, ha.open, ha.close);
    haData.push(ha);
    prevHA = ha;
  }
  return haData;
};

const applyCurrentChartType = () => {
  if (!chart || currentChartData.value.length === 0) return;
  if (localChartType.value === 'heikinAshi') {
    const haData = calculateHeikinAshi(currentChartData.value);
    chart.applyNewData(haData);
  } else {
    chart.applyNewData(currentChartData.value);
  }
};

const changeChartType = (type: string) => {
  localChartType.value = type;
  applyCurrentChartType();
};

const formatApiData = (history: any[]) => {
  return history.map(item => ({
    timestamp: item.time * 1000, 
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

  chart.subscribeAction('onPaneClick', (params: any) => {
    takeFocus(); 
    if (params && params.value !== undefined && params.value !== null) {
      if (marketStore.setClickedPrice) {
        marketStore.setClickedPrice(params.value);
      }
    }
  });

  // 🌟 3. 本地光标移动广播 (自由浮动 + 严谨兼容)
  chart.subscribeAction('onCrosshairChange', (params: any) => {
    if (!marketStore.isSyncEnabled) return;

    if (!params || params.dataIndex === undefined || params.dataIndex < 0) {
      marketStore.updateGlobalCrosshair(0, 0, props.symbol, instanceId);
      return;
    }

    const kLineList = chart.getDataList();
    const currentKline = kLineList[params.dataIndex];
    if (!currentKline) return;

    let targetPrice = currentKline.close;
    const pixelY = params.y !== undefined ? params.y : params.realY;
    
    if (params.paneId && pixelY !== undefined) {
      try {
        const convertedPrice = chart.convertFromPixel({ x: 0, y: pixelY }, { paneId: params.paneId });
        if (!isNaN(convertedPrice as any)) {
          if (typeof convertedPrice === 'number') {
            targetPrice = convertedPrice;
          }
        } else if (typeof convertedPrice === 'object' && convertedPrice !== null) {
          targetPrice = (convertedPrice as any).value;
        }
      } catch (e) {
        console.warn("坐标转换失败", e);
      }
    }

    marketStore.updateGlobalCrosshair(
      currentKline.timestamp, 
      targetPrice, 
      props.symbol, 
      instanceId
    );
  });

  chart.setLoadDataCallback(async (params: any) => {
    const timestamp = params.timestamp;
    if (!timestamp) return;

    const olderHistory = await MarketAPI.getHistoricalKlines(props.symbol, currentTf.value, 1000, timestamp - 1);
    if (olderHistory && olderHistory.length > 0) {
      const formatted = formatApiData(olderHistory);
      currentChartData.value = [...formatted, ...currentChartData.value];
      
      if (localChartType.value === 'heikinAshi') {
        const haData = calculateHeikinAshi(currentChartData.value);
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
    chart.resize(); 
  });
  resizeObserver.observe(chartContainer.value);

  marketStore.connectWs();
  await loadHistory(props.symbol, currentTf.value);
  marketStore.subscribeKline(props.symbol, currentTf.value);
});

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
.kline-module { 
  width: 100%; 
  height: 100%; 
  display: flex; 
  flex-direction: column; 
  background: #0d1117; 
  border: 1px solid transparent; 
  transition: all 0.2s ease;
  box-sizing: border-box;
}

.kline-module.is-focused {
  border-color: #58a6ff;
  box-shadow: inset 0 0 10px rgba(88, 166, 255, 0.1);
}

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

.sync-btn {
  background: transparent; 
  border: 1px solid #30363d; 
  color: #8b949e; 
  padding: 3px 8px; 
  border-radius: 4px; 
  cursor: pointer; 
  font-size: 12px; 
  transition: all 0.2s;
}
.sync-btn.active {
  background: #1f6feb; 
  color: white;
  border-color: #1f6feb;
}

.divider { color: #30363d; margin: 0 2px; }

.symbol-info-wrapper { display: flex; align-items: center; gap: 8px; }
.symbol-info { font-size: 14px; font-weight: bold; color: #e6edf3; }
.focus-badge {
  background: #1f6feb; 
  color: #ffffff;
  font-size: 10px;
  padding: 2px 6px;
  border-radius: 10px;
  font-weight: normal;
}

.chart-container { flex: 1; width: 100%; position: relative; }
</style>
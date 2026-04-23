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
// 数据格式化
// ==========================================
const formatApiData = (history: any[]) => {
  return history;
  // return history
  //   .map(item => ({
  //     time: Math.floor(Number(item.time) / 1000), 
  //     open: Number(item.open), 
  //     high: Number(item.high), 
  //     low: Number(item.low), 
  //     close: Number(item.close), 
  //     value: Number(item.volume || 0), 
  //     color: Number(item.close) >= Number(item.open) ? 'rgba(38, 166, 154, 0.5)' : 'rgba(239, 83, 80, 0.5)'
  //   }))
  //   .filter(item => !isNaN(item.time) && !isNaN(item.close))
  //   .sort((a, b) => a.time - b.time); 
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
    ha.close = (raw.open + raw.high + raw.low + raw.close) / 4;
    if (!prevHA) { ha.open = (raw.open + raw.close) / 2; } 
    else { ha.open = (prevHA.open + prevHA.close) / 2; }
    ha.high = Math.max(raw.high, ha.open, ha.close);
    ha.low = Math.min(raw.low, ha.open, ha.close);
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
  
  // 主副图统一喂数据，显隐由 visible 控制
  candleSeries.setData(finalData);
  volumeSeries.setData(finalData.map((d: any) => ({ time: d.time, value: d.value, color: d.color })));
  
  updateChartPrecision(finalData);
};

// ==========================================
// 初始化与历史拉取
// ==========================================
const getIntervalMs = (tf: string) => {
  const v = parseInt(tf);
  const u = tf.slice(-1);
  return (u === 'm' ? v * 60 : u === 'h' ? v * 3600 : u === 'd' ? v * 86400 : 60) * 1000;
};

// ==========================================
// 初始化与历史拉取 (带防空洞报警)
// ==========================================
const loadHistory = async (symbol: string, interval: string) => {
  try {
    const history = await MarketAPI.getHistoricalKlines(symbol, interval, 1000);
    
    // 🚨 强力报警器：如果接口返回空，立刻在控制台和页面上暴露真凶！
    if (!history || history.length === 0) {
      console.error(`❌ [数据异常] 接口请求成功，但 ${symbol} 返回了空数组 []！请检查该币种近期是否有交易，或检查后端代理配置。`);
      // 可选：你甚至可以通过弹窗提示自己
      // alert(`${symbol} 获取历史数据为空！`);
      return;
    }

    currentChartData.value = formatApiData(history);
    applyDataToSeries(currentChartData.value);
    
    console.log(`✅ [数据加载成功] 成功为 ${symbol} 铺设了 ${currentChartData.value.length} 根底座 K 线！`);
  } catch (error) {
    console.error(`❌ [网络异常] K 线历史接口请求彻底失败:`, error);
  }
};

let isLoadingMoreHistory = false;
const loadMoreHistory = async () => {
  if (isLoadingMoreHistory || currentChartData.value.length === 0) return;
  isLoadingMoreHistory = true;

  const oldestTimeMs = currentChartData.value[0].time * 1000; 
  const targetEndTime = oldestTimeMs - getIntervalMs(currentTf.value);

  try {
    const olderHistory = await MarketAPI.getHistoricalKlines(props.symbol, currentTf.value, 1000, targetEndTime);
    if (olderHistory && olderHistory.length > 0) {
      const formatted = formatApiData(olderHistory);
      const safeNewData = formatted.filter(item => item.time < currentChartData.value[0].time);
      
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
    timeScale: { borderColor: '#30363d', timeVisible: true },
    rightPriceScale: { 
      borderColor: '#30363d',
      // 🚨 副图隔离核心1：为主图（蜡烛图）底部硬性留出 25% 的空白护城河
      scaleMargins: {
        top: 0.05,
        bottom: 0.25, 
      }
    }
  });

  candleSeries = chart.addSeries(CandlestickSeries, {
    upColor: '#2ea043', downColor: '#f85149', 
    borderVisible: false,
    wickUpColor: '#2ea043', wickDownColor: '#f85149'
  });

  volumeSeries = chart.addSeries(HistogramSeries, {
    priceFormat: { type: 'volume' },
    priceScaleId: '', // 空 ID 表示不挂载到右侧 Y 轴，拥有自己独立的隐形刻度
    visible: showVolume.value
  });
  
  // 🚨 副图隔离核心2：强制成交量只在底部 20% 的区域内渲染，与主图绝不重叠
  volumeSeries.priceScale().applyOptions({
    scaleMargins: {
      top: 0.8, // 从图表的 80% 高度开始画
      bottom: 0,
    }
  });

  chart.timeScale().subscribeVisibleLogicalRangeChange((logicalRange) => {
    if (logicalRange && logicalRange.from < 10 && !isLoadingMoreHistory) {
      loadMoreHistory();
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
    const rawFormat = {
      time: Math.floor(Number(newVal.time) / 1000), 
      open: Number(newVal.open), high: Number(newVal.high), 
      low: Number(newVal.low), close: Number(newVal.close), 
      value: Number(newVal.volume || 0),
      color: Number(newVal.close) >= Number(newVal.open) ? 'rgba(38, 166, 154, 0.5)' : 'rgba(239, 83, 80, 0.5)'
    };

    if (currentChartData.value.length > 0) {
      const lastIndex = currentChartData.value.length - 1;
      if (currentChartData.value[lastIndex].time === rawFormat.time) {
        currentChartData.value[lastIndex] = rawFormat;
      } else if (rawFormat.time > currentChartData.value[lastIndex].time) {
        currentChartData.value.push(rawFormat);
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

// 🚨 优化成交量显隐逻辑：不再重新塞数据，直接开关 visible 属性，性能极高
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
.kline-toolbar { display: flex; justify-content: space-between; align-items: center; padding: 6px 12px; background: #161b22; border-bottom: 1px solid #21262d; flex-shrink: 0; }
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
.chart-container { flex: 1; width: 100%; position: relative; }
</style>
import { ref, onMounted, onUnmounted, watch, computed } from 'vue';
import { createChart, LineSeries, HistogramSeries, CrosshairMode, LineStyle } from 'lightweight-charts';
import { MarketAPI } from '@/api/market';
import { useMarketStore } from '@/store/market';
const props = defineProps();
const marketStore = useMarketStore();
const isFocused = ref(false);
const takeFocus = () => isFocused.value = true;
const timeframes = ['1m', '5m', '15m', '1h', '4h', '1d'];
const popularSymbols = [
    'BTCUSDT', 'ETHUSDT', 'SOLUSDT', 'BNBUSDT', 'XRPUSDT',
    'DOGEUSDT', 'ADAUSDT', 'AVAXUSDT', 'LINKUSDT', 'MATICUSDT',
    'NEARUSDT', 'LTCUSDT', 'BCHUSDT', 'OPUSDT', 'ARBUSDT',
    'INJUSDT', 'RNDRUSDT', 'SUIUSDT', 'APTUSDT', 'PEPEUSDT'
];
const currentSymbol = ref(props.symbol || 'BTCUSDT');
const currentTf = ref('1h');
const currentDimension = ref('price');
const topK = ref(5);
const predictBars = ref(30); // 🌟 新增：默认向未来推演 30 根 K 线
const isCalculating = ref(false);
const isRealTimeActive = ref(false);
const mainChartRef = ref(null);
const freqChartRef = ref(null);
let mainChart = null;
let freqChart = null;
let originalSeries = null;
let reconstructedSeries = null;
let predictSeries = null; // 🌟 新增：专门用于画未来虚线的 Series
let spectrumSeries = null;
let resizeObserver = null;
let rawKlines = [];
let signalTimes = [];
let originalSignal = [];
let fourierResult = null;
const formatDateTime = (timestamp) => {
    const date = new Date(timestamp * 1000);
    const y = date.getFullYear();
    const m = String(date.getMonth() + 1).padStart(2, '0');
    const d = String(date.getDate()).padStart(2, '0');
    const H = String(date.getHours()).padStart(2, '0');
    const M = String(date.getMinutes()).padStart(2, '0');
    return `${y}-${m}-${d} ${H}:${M}`;
};
const getIntervalSec = (tf) => {
    const v = parseInt(tf);
    const u = tf.slice(-1);
    return (u === 'm' ? v * 60 : u === 'h' ? v * 3600 : u === 'd' ? v * 86400 : 60);
};
// 🧠 DFT 引擎
const computeDFT = (signal) => {
    const N = signal.length;
    const re = new Float64Array(N);
    const im = new Float64Array(N);
    const amplitude = new Array(N);
    const mean = signal.reduce((a, b) => a + b, 0) / N;
    const normalizedSignal = signal.map(val => val - mean);
    for (let k = 0; k < N; k++) {
        for (let n = 0; n < N; n++) {
            const angle = (Math.PI * 2 * k * n) / N;
            re[k] += normalizedSignal[n] * Math.cos(angle);
            im[k] -= normalizedSignal[n] * Math.sin(angle);
        }
        amplitude[k] = Math.sqrt(re[k] ** 2 + im[k] ** 2) / N;
    }
    re._mean = mean;
    return { re, im, amplitude };
};
// 🌟 核心升级：外推预测引擎 (IDFT Extended)
const computeIDFT = (re, im, amplitudes, kTop, futureBars) => {
    const N = re.length;
    // 🌟 总长度 = 历史长度 N + 预测未来长度 futureBars
    const totalLen = N + futureBars;
    const reconstructed = new Float64Array(totalLen);
    const mean = re._mean || 0;
    const freqs = amplitudes.map((amp, index) => ({ amp, index }))
        .filter(f => f.index > 0 && f.index < N / 2)
        .sort((a, b) => b.amp - a.amp)
        .slice(0, kTop)
        .map(f => f.index);
    // 🌟 让时间变量 n 突破历史 N 的束缚，继续向未来计算
    for (let n = 0; n < totalLen; n++) {
        let sum = mean;
        for (const k of freqs) {
            // 注意：公式里的周期基数依然是历史样本数 N
            const angle = (Math.PI * 2 * k * n) / N;
            sum += (re[k] * Math.cos(angle) + im[k] * Math.sin(angle)) * 2 / N;
        }
        reconstructed[n] = sum;
    }
    return reconstructed;
};
const fetchData = async () => {
    isCalculating.value = true;
    isRealTimeActive.value = false;
    try {
        const history = await MarketAPI.getHistoricalKlines(currentSymbol.value, currentTf.value, 500);
        if (!history || history.length === 0)
            return;
        rawKlines = history.filter((d) => !isNaN(Number(d.time))).sort((a, b) => Number(a.time) - Number(b.time));
        signalTimes = rawKlines.map(d => Number(d.time) > 9999999999 ? Math.floor(Number(d.time) / 1000) : Number(d.time));
        processFourier();
        isRealTimeActive.value = true;
    }
    catch (error) {
        console.error("傅里叶数据拉取失败", error);
    }
    finally {
        isCalculating.value = false;
    }
};
const onSymbolChange = () => {
    marketStore.subscribeKline(currentSymbol.value, currentTf.value);
    fetchData();
};
let isLoadingMoreHistory = false;
const loadMoreHistory = async () => {
    if (isLoadingMoreHistory || rawKlines.length === 0)
        return;
    isLoadingMoreHistory = true;
    isCalculating.value = true;
    const rawOldest = Number(rawKlines[0].time);
    const oldestTimeSec = rawOldest > 9999999999 ? Math.floor(rawOldest / 1000) : rawOldest;
    const targetEndTimeSec = oldestTimeSec - getIntervalSec(currentTf.value);
    const targetEndTimeMs = targetEndTimeSec * 1000;
    try {
        const olderHistory = await MarketAPI.getHistoricalKlines(currentSymbol.value, currentTf.value, 500, targetEndTimeMs);
        if (olderHistory && olderHistory.length > 0) {
            const safeNewData = olderHistory.filter((item) => {
                const itemTime = Number(item.time) > 9999999999 ? Math.floor(Number(item.time) / 1000) : Number(item.time);
                return itemTime < oldestTimeSec;
            });
            if (safeNewData.length > 0) {
                rawKlines = [...safeNewData, ...rawKlines];
                signalTimes = rawKlines.map(d => Number(d.time) > 9999999999 ? Math.floor(Number(d.time) / 1000) : Number(d.time));
                processFourier();
            }
        }
    }
    catch (e) {
    }
    finally {
        isLoadingMoreHistory = false;
        isCalculating.value = false;
    }
};
const currentKlineData = computed(() => marketStore.latestKlines[`${currentSymbol.value}_${currentTf.value}`]);
let isProcessingLive = false;
watch(currentKlineData, (newVal) => {
    if (!newVal || rawKlines.length === 0 || isProcessingLive || isCalculating.value)
        return;
    const rawTime = Number(newVal.time);
    const timeInSeconds = rawTime > 9999999999 ? Math.floor(rawTime / 1000) : rawTime;
    const lastIndex = rawKlines.length - 1;
    const lastTimeSec = Number(rawKlines[lastIndex].time) > 9999999999
        ? Math.floor(Number(rawKlines[lastIndex].time) / 1000)
        : Number(rawKlines[lastIndex].time);
    let isDataChanged = false;
    if (lastTimeSec === timeInSeconds) {
        rawKlines[lastIndex] = newVal;
        isDataChanged = true;
    }
    else if (timeInSeconds > lastTimeSec) {
        rawKlines.push(newVal);
        if (rawKlines.length > 1000)
            rawKlines.shift();
        isDataChanged = true;
    }
    if (isDataChanged) {
        isProcessingLive = true;
        signalTimes = rawKlines.map(d => Number(d.time) > 9999999999 ? Math.floor(Number(d.time) / 1000) : Number(d.time));
        requestAnimationFrame(() => {
            processFourier();
            isProcessingLive = false;
        });
    }
}, { deep: true });
const processFourier = () => {
    if (rawKlines.length === 0)
        return;
    originalSignal = rawKlines.map(d => {
        const c = Number(d.close), o = Number(d.open), v = Number(d.volume || d.vol || 0);
        switch (currentDimension.value) {
            case 'price': return c;
            case 'volume': return v;
            case 'amount': return d.quoteAssetVolume ? Number(d.quoteAssetVolume) : c * v;
            case 'delta': return c > o ? v : -v;
            default: return c;
        }
    });
    originalSeries.setData(signalTimes.map((t, i) => ({ time: t, value: originalSignal[i] })));
    fourierResult = computeDFT(originalSignal);
    reconstructSignal();
};
// 🌟 核心升级：切分历史拟合线与未来推演线
const reconstructSignal = () => {
    if (!fourierResult)
        return;
    // 算出历史 + 未来的总体数据
    const N = fourierResult.amplitude.length;
    const reconstructed = computeIDFT(fourierResult.re, fourierResult.im, fourierResult.amplitude, topK.value, predictBars.value);
    const histData = [];
    const futData = [];
    const intervalSec = getIntervalSec(currentTf.value);
    const lastTime = signalTimes[N - 1];
    // 切分数据分配给两根不同的 Series
    for (let i = 0; i < reconstructed.length; i++) {
        if (i < N) {
            histData.push({ time: signalTimes[i], value: reconstructed[i] });
        }
        else {
            const futTime = lastTime + (i - N + 1) * intervalSec;
            futData.push({ time: futTime, value: reconstructed[i] });
        }
    }
    // 为了让虚线和实线无缝连接，把历史最后一个点作为未来的起点
    if (histData.length > 0 && futData.length > 0) {
        futData.unshift(histData[histData.length - 1]);
    }
    reconstructedSeries.setData(histData);
    predictSeries.setData(futData);
    // 更新下方频域柱子颜色
    const freqs = fourierResult.amplitude.map((amp, index) => ({ amp, index }))
        .filter(f => f.index > 0 && f.index < N / 2)
        .sort((a, b) => b.amp - a.amp)
        .slice(0, topK.value)
        .map(f => f.index);
    const spectrumData = [];
    for (let i = 1; i < Math.floor(N / 2); i++) {
        spectrumData.push({
            time: signalTimes[i],
            value: fourierResult.amplitude[i],
            color: freqs.includes(i) ? '#e2b514' : '#21262d'
        });
    }
    spectrumSeries.setData(spectrumData);
    // 确保预测区域可见
    mainChart?.timeScale().fitContent();
};
onMounted(() => {
    if (!mainChartRef.value || !freqChartRef.value)
        return;
    const commonOptions = {
        layout: { textColor: '#8b949e', background: { type: 'solid', color: '#0d1117' } },
        grid: { vertLines: { color: '#21262d', style: LineStyle.Dotted }, horzLines: { color: '#21262d', style: LineStyle.Dotted } },
        crosshair: { mode: CrosshairMode.Magnet },
        timeScale: { borderColor: '#30363d', timeVisible: true },
        localization: {
            timeFormatter: (t) => typeof t === 'number' ? formatDateTime(t) : String(t)
        },
    };
    mainChart = createChart(mainChartRef.value, { ...commonOptions });
    originalSeries = mainChart.addSeries(LineSeries, { color: 'rgba(88, 166, 255, 0.3)', lineWidth: 1, title: '原始信号' });
    // 🌟 拟合结果分为两段：历史拟合实线，未来推演虚线
    reconstructedSeries = mainChart.addSeries(LineSeries, { color: '#ff9800', lineWidth: 2, title: '频率叠加拟合' });
    predictSeries = mainChart.addSeries(LineSeries, { color: '#e2b514', lineWidth: 2, lineStyle: LineStyle.Dashed, title: '未来推演' });
    freqChart = createChart(freqChartRef.value, { ...commonOptions });
    spectrumSeries = freqChart.addSeries(HistogramSeries, { color: '#21262d', priceFormat: { type: 'volume' }, title: '频谱能量' });
    freqChart.applyOptions({ timeScale: { visible: false } });
    mainChart.timeScale().subscribeVisibleLogicalRangeChange((logicalRange) => {
        if (logicalRange && logicalRange.from < 10 && !isLoadingMoreHistory) {
            loadMoreHistory();
        }
    });
    resizeObserver = new ResizeObserver(() => {
        mainChart?.applyOptions({ width: mainChartRef.value.clientWidth, height: mainChartRef.value.clientHeight });
        freqChart?.applyOptions({ width: freqChartRef.value.clientWidth, height: freqChartRef.value.clientHeight });
    });
    resizeObserver.observe(mainChartRef.value);
    resizeObserver.observe(freqChartRef.value);
    marketStore.subscribeKline(currentSymbol.value, currentTf.value);
    fetchData();
});
onUnmounted(() => {
    if (resizeObserver && mainChartRef.value)
        resizeObserver.disconnect();
    marketStore.unsubscribeKline(currentSymbol.value, currentTf.value);
    mainChart?.remove();
    freqChart?.remove();
});
const __VLS_ctx = {
    ...{},
    ...{},
    ...{},
    ...{},
};
let __VLS_components;
let __VLS_intrinsics;
let __VLS_directives;
/** @type {__VLS_StyleScopedClasses['fourier-module']} */ ;
/** @type {__VLS_StyleScopedClasses['f-select']} */ ;
/** @type {__VLS_StyleScopedClasses['slider-group']} */ ;
/** @type {__VLS_StyleScopedClasses['action-btn']} */ ;
/** @type {__VLS_StyleScopedClasses['action-btn']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ onClick: (__VLS_ctx.takeFocus) },
    ...{ class: "fourier-module" },
    ...{ class: ({ 'is-focused': __VLS_ctx.isFocused }) },
});
/** @type {__VLS_StyleScopedClasses['fourier-module']} */ ;
/** @type {__VLS_StyleScopedClasses['is-focused']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ class: "fourier-toolbar" },
});
/** @type {__VLS_StyleScopedClasses['fourier-toolbar']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ class: "controls-group" },
});
/** @type {__VLS_StyleScopedClasses['controls-group']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.select, __VLS_intrinsics.select)({
    ...{ onChange: (__VLS_ctx.onSymbolChange) },
    ...{ class: "f-select" },
    value: (__VLS_ctx.currentSymbol),
});
/** @type {__VLS_StyleScopedClasses['f-select']} */ ;
for (const [sym] of __VLS_vFor((__VLS_ctx.popularSymbols))) {
    __VLS_asFunctionalElement1(__VLS_intrinsics.option, __VLS_intrinsics.option)({
        key: (sym),
        value: (sym),
    });
    (sym);
    // @ts-ignore
    [takeFocus, isFocused, onSymbolChange, currentSymbol, popularSymbols,];
}
__VLS_asFunctionalElement1(__VLS_intrinsics.select, __VLS_intrinsics.select)({
    ...{ onChange: (__VLS_ctx.fetchData) },
    ...{ class: "f-select" },
    value: (__VLS_ctx.currentTf),
});
/** @type {__VLS_StyleScopedClasses['f-select']} */ ;
for (const [tf] of __VLS_vFor((__VLS_ctx.timeframes))) {
    __VLS_asFunctionalElement1(__VLS_intrinsics.option, __VLS_intrinsics.option)({
        key: (tf),
        value: (tf),
    });
    (tf);
    // @ts-ignore
    [fetchData, currentTf, timeframes,];
}
__VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({
    ...{ class: "divider" },
});
/** @type {__VLS_StyleScopedClasses['divider']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.select, __VLS_intrinsics.select)({
    ...{ onChange: (__VLS_ctx.processFourier) },
    ...{ class: "f-select highlight" },
    value: (__VLS_ctx.currentDimension),
});
/** @type {__VLS_StyleScopedClasses['f-select']} */ ;
/** @type {__VLS_StyleScopedClasses['highlight']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.option, __VLS_intrinsics.option)({
    value: "price",
});
__VLS_asFunctionalElement1(__VLS_intrinsics.option, __VLS_intrinsics.option)({
    value: "volume",
});
__VLS_asFunctionalElement1(__VLS_intrinsics.option, __VLS_intrinsics.option)({
    value: "amount",
});
__VLS_asFunctionalElement1(__VLS_intrinsics.option, __VLS_intrinsics.option)({
    value: "delta",
});
__VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({
    ...{ class: "divider" },
});
/** @type {__VLS_StyleScopedClasses['divider']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ class: "slider-group" },
    title: "保留能量最强的前 K 个频率，过滤噪音",
});
/** @type {__VLS_StyleScopedClasses['slider-group']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.label, __VLS_intrinsics.label)({});
(__VLS_ctx.topK);
__VLS_asFunctionalElement1(__VLS_intrinsics.input)({
    ...{ onInput: (__VLS_ctx.reconstructSignal) },
    type: "range",
    min: "1",
    max: "50",
});
(__VLS_ctx.topK);
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ class: "slider-group" },
    title: "根据提取的频率叠加公式，向未来推演走势",
});
/** @type {__VLS_StyleScopedClasses['slider-group']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.label, __VLS_intrinsics.label)({});
(__VLS_ctx.predictBars);
__VLS_asFunctionalElement1(__VLS_intrinsics.input)({
    ...{ onInput: (__VLS_ctx.reconstructSignal) },
    type: "range",
    min: "0",
    max: "100",
});
(__VLS_ctx.predictBars);
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ class: "actions-group" },
});
/** @type {__VLS_StyleScopedClasses['actions-group']} */ ;
if (__VLS_ctx.isRealTimeActive) {
    __VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({
        ...{ class: "live-badge" },
        title: "WebSocket 数据接入中",
    });
    /** @type {__VLS_StyleScopedClasses['live-badge']} */ ;
}
__VLS_asFunctionalElement1(__VLS_intrinsics.button, __VLS_intrinsics.button)({
    ...{ onClick: (__VLS_ctx.fetchData) },
    ...{ class: "action-btn" },
    disabled: (__VLS_ctx.isCalculating),
});
/** @type {__VLS_StyleScopedClasses['action-btn']} */ ;
(__VLS_ctx.isCalculating ? '🧠 算力运转中...' : '🔄 刷新数据');
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ class: "charts-wrapper" },
});
/** @type {__VLS_StyleScopedClasses['charts-wrapper']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ onPointerdown: () => { } },
    ...{ onWheel: () => { } },
    ...{ class: "chart-box main-chart no-drag" },
    ref: "mainChartRef",
});
/** @type {__VLS_StyleScopedClasses['chart-box']} */ ;
/** @type {__VLS_StyleScopedClasses['main-chart']} */ ;
/** @type {__VLS_StyleScopedClasses['no-drag']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ class: "chart-badge" },
});
/** @type {__VLS_StyleScopedClasses['chart-badge']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ onPointerdown: () => { } },
    ...{ onWheel: () => { } },
    ...{ class: "chart-box sub-chart no-drag" },
    ref: "freqChartRef",
});
/** @type {__VLS_StyleScopedClasses['chart-box']} */ ;
/** @type {__VLS_StyleScopedClasses['sub-chart']} */ ;
/** @type {__VLS_StyleScopedClasses['no-drag']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ class: "chart-badge" },
});
/** @type {__VLS_StyleScopedClasses['chart-badge']} */ ;
// @ts-ignore
[fetchData, processFourier, currentDimension, topK, topK, reconstructSignal, reconstructSignal, predictBars, predictBars, isRealTimeActive, isCalculating, isCalculating,];
const __VLS_export = (await import('vue')).defineComponent({
    __typeProps: {},
});
export default {};

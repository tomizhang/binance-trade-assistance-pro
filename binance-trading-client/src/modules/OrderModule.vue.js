import { ref, computed, watch, onMounted } from 'vue';
import { debounce, createAsyncLock } from '@/utils/optimize';
import { useMarketStore } from '@/store/market';
import { useToast } from '@/utils/useToast';
import { calculateLiquidationPrice } from '@/utils/tradeUtils';
const marketStore = useMarketStore();
const toast = useToast();
const currentSymbol = computed(() => marketStore.currentSymbol || 'BTCUSDT');
const baseAsset = computed(() => currentSymbol.value.replace('USDT', ''));
const latestPrice = computed(() => marketStore.marketTickers[currentSymbol.value]?.lastPrice || 0);
const priceTrend = ref('');
onMounted(() => {
    marketStore.fetchExchangeInfo();
    // 同步当前真实杠杆
    if (marketStore.symbolConfigs[currentSymbol.value]) {
        leverage.value = marketStore.symbolConfigs[currentSymbol.value].leverage;
    }
});
const orderType = ref('LIMIT');
const price = ref('');
const amount = ref('');
const leverage = ref(20);
const positionPercent = ref(0);
const estimatedValue = ref(0);
const requiredMargin = ref(0);
// 监听币种切换
watch(currentSymbol, (newSymbol) => {
    price.value = '';
    amount.value = '';
    positionPercent.value = 0;
    estimatedValue.value = 0;
    requiredMargin.value = 0;
    // 切换币种时，拉取该币种在币安的真实杠杆设置
    leverage.value = marketStore.symbolConfigs[newSymbol]?.leverage || 20;
    if (orderType.value === 'LIMIT' && latestPrice.value > 0) {
        const rule = marketStore.symbolRules[newSymbol] || { tickSize: '0.1', stepSize: '0.001' };
        price.value = formatByStep(latestPrice.value, rule.tickSize);
    }
});
const formatByStep = (value, stepStr) => {
    const step = parseFloat(stepStr);
    if (step <= 0)
        return value.toString();
    const factor = (value / step) + Number.EPSILON;
    return (Math.floor(factor) * step).toFixed(stepStr.includes('.') ? stepStr.split('.')[1].length : 0);
};
const getPriceForCalc = () => orderType.value === 'LIMIT' ? (parseFloat(price.value) || 0) : latestPrice.value;
// 🌟 计算逻辑更新：使用 dynamicUsdtBalance
const calculateAmountFromPercent = () => {
    const p = getPriceForCalc();
    if (p <= 0 || positionPercent.value === 0) {
        amount.value = '';
        estimatedValue.value = 0;
        requiredMargin.value = 0;
        return;
    }
    const rule = marketStore.symbolRules[currentSymbol.value] || { tickSize: '0.1', stepSize: '0.001' };
    // 预留缓冲：市价 2%，限价 1%
    const feeBuffer = orderType.value === 'MARKET' ? 0.98 : 0.99;
    const safeBalance = marketStore.dynamicUsdtBalance * feeBuffer;
    amount.value = formatByStep((safeBalance * (positionPercent.value / 100) * leverage.value) / p, rule.stepSize);
    const notional = parseFloat(amount.value) * p;
    estimatedValue.value = notional;
    requiredMargin.value = notional / leverage.value;
};
const setPercentage = (pct) => {
    positionPercent.value = pct;
    calculateAmountFromPercent();
};
const handleAmountInput = () => {
    const p = getPriceForCalc();
    const a = parseFloat(amount.value);
    if (p > 0 && a > 0) {
        estimatedValue.value = p * a;
        requiredMargin.value = (p * a) / leverage.value;
        // 使用动态余额反推百分比
        const feeBuffer = orderType.value === 'MARKET' ? 0.98 : 0.99;
        const safeBalance = marketStore.dynamicUsdtBalance * feeBuffer;
        if (safeBalance > 0) {
            positionPercent.value = Math.min(100, Math.max(0, Math.round((requiredMargin.value / (safeBalance)) * 100)));
        }
    }
    else {
        estimatedValue.value = 0;
        requiredMargin.value = 0;
        positionPercent.value = 0;
    }
};
const debouncedCalculate = debounce(calculateAmountFromPercent, 300);
watch(orderType, calculateAmountFromPercent);
// 修改杠杆
const handleLeverageChange = async () => {
    try {
        const res = await fetch(`${import.meta.env.VITE_API_BASE_URL}/api/account/leverage`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ symbol: currentSymbol.value, leverage: leverage.value })
        });
        if (!res.ok)
            throw new Error((await res.json()).message || '修改杠杆失败');
        toast.info(`杠杆调整为 ${leverage.value}x`);
        calculateAmountFromPercent();
    }
    catch (e) {
        toast.error(e.message);
    }
};
// 强平价推演
const getOtherCrossPositions = () => marketStore.positions.filter(p => p.symbol !== currentSymbol.value && p.marginType === 'cross').map(p => {
    const pr = marketStore.marketTickers[p.symbol]?.lastPrice || p.entryPrice;
    const amt = Math.abs(p.amount);
    return { ...p, unrealizedPnL: p.side === 'LONG' ? (pr - p.entryPrice) * amt : (p.entryPrice - pr) * amt, markPrice: pr };
});
const previewLiqLong = computed(() => parseFloat(amount.value) > 0 ? calculateLiquidationPrice({ symbol: currentSymbol.value, side: 'LONG', amount: parseFloat(amount.value), entryPrice: getPriceForCalc(), leverage: leverage.value, marginType: 'cross' }, marketStore.dynamicUsdtBalance, getOtherCrossPositions()) : 0);
const previewLiqShort = computed(() => parseFloat(amount.value) > 0 ? calculateLiquidationPrice({ symbol: currentSymbol.value, side: 'SHORT', amount: parseFloat(amount.value), entryPrice: getPriceForCalc(), leverage: leverage.value, marginType: 'cross' }, marketStore.dynamicUsdtBalance, getOtherCrossPositions()) : 0);
const riskDistancePct = computed(() => {
    if (previewLiqLong.value <= 0 || getPriceForCalc() <= 0)
        return 0;
    return Math.min(100, Math.max(0, 100 - ((Math.abs(getPriceForCalc() - previewLiqLong.value) / getPriceForCalc()) * 100 * leverage.value)));
});
const riskLevel = computed(() => riskDistancePct.value > 80 ? 'danger' : riskDistancePct.value > 50 ? 'warning' : 'safe');
const isValid = computed(() => (orderType.value === 'LIMIT' && parseFloat(price.value) > 0 && parseFloat(amount.value) > 0) || (orderType.value === 'MARKET' && parseFloat(amount.value) > 0));
const isSubmitting = ref(false);
const orderLock = createAsyncLock();
const placeOrder = async (side) => {
    const rule = marketStore.symbolRules[currentSymbol.value] || { tickSize: '0.1', stepSize: '0.001' };
    try {
        const res = await fetch(`${import.meta.env.VITE_API_BASE_URL}/api/order/place-ws`, {
            method: 'POST', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                symbol: currentSymbol.value,
                side,
                type: orderType.value,
                quantity: parseFloat(formatByStep(parseFloat(amount.value), rule.stepSize)),
                price: orderType.value === 'LIMIT' ? parseFloat(formatByStep(parseFloat(price.value), rule.tickSize)) : null
            })
        });
        const data = await res.json();
        if (!res.ok || data.error)
            throw new Error(data.error?.msg || '下单被拒');
        toast.success(`下单成功!`);
        return true;
    }
    catch (e) {
        toast.error(e.message);
        return false;
    }
};
const handleBuy = async () => { await orderLock(async () => { isSubmitting.value = true; if (await placeOrder('BUY')) {
    amount.value = '';
    positionPercent.value = 0;
    handleAmountInput();
} isSubmitting.value = false; }); };
const handleSell = async () => { await orderLock(async () => { isSubmitting.value = true; if (await placeOrder('SELL')) {
    amount.value = '';
    positionPercent.value = 0;
    handleAmountInput();
} isSubmitting.value = false; }); };
const __VLS_ctx = {
    ...{},
    ...{},
};
let __VLS_components;
let __VLS_intrinsics;
let __VLS_directives;
/** @type {__VLS_StyleScopedClasses['order-tabs']} */ ;
/** @type {__VLS_StyleScopedClasses['order-tabs']} */ ;
/** @type {__VLS_StyleScopedClasses['available-balance']} */ ;
/** @type {__VLS_StyleScopedClasses['input-group']} */ ;
/** @type {__VLS_StyleScopedClasses['input-group']} */ ;
/** @type {__VLS_StyleScopedClasses['input-group']} */ ;
/** @type {__VLS_StyleScopedClasses['slider-header']} */ ;
/** @type {__VLS_StyleScopedClasses['custom-slider']} */ ;
/** @type {__VLS_StyleScopedClasses['percentage-marks']} */ ;
/** @type {__VLS_StyleScopedClasses['action-buttons-fixed']} */ ;
/** @type {__VLS_StyleScopedClasses['action-buttons-fixed']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ class: "order-module" },
});
/** @type {__VLS_StyleScopedClasses['order-module']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ class: "module-header" },
});
/** @type {__VLS_StyleScopedClasses['module-header']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({
    ...{ class: "symbol-badge" },
});
/** @type {__VLS_StyleScopedClasses['symbol-badge']} */ ;
(__VLS_ctx.currentSymbol);
if (__VLS_ctx.latestPrice) {
    __VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({
        ...{ class: "price-ticker" },
        ...{ class: (__VLS_ctx.priceTrend) },
    });
    /** @type {__VLS_StyleScopedClasses['price-ticker']} */ ;
    (__VLS_ctx.latestPrice);
}
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ class: "order-tabs" },
});
/** @type {__VLS_StyleScopedClasses['order-tabs']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.button, __VLS_intrinsics.button)({
    ...{ onClick: (...[$event]) => {
            __VLS_ctx.orderType = 'LIMIT';
            // @ts-ignore
            [currentSymbol, latestPrice, latestPrice, priceTrend, orderType,];
        } },
    ...{ class: ({ active: __VLS_ctx.orderType === 'LIMIT' }) },
});
/** @type {__VLS_StyleScopedClasses['active']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.button, __VLS_intrinsics.button)({
    ...{ onClick: (...[$event]) => {
            __VLS_ctx.orderType = 'MARKET';
            // @ts-ignore
            [orderType, orderType,];
        } },
    ...{ class: ({ active: __VLS_ctx.orderType === 'MARKET' }) },
});
/** @type {__VLS_StyleScopedClasses['active']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ class: "order-form-content" },
});
/** @type {__VLS_StyleScopedClasses['order-form-content']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ class: "available-balance" },
});
/** @type {__VLS_StyleScopedClasses['available-balance']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({});
(__VLS_ctx.marketStore.dynamicUsdtBalance.toFixed(2));
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ class: "leverage-control" },
});
/** @type {__VLS_StyleScopedClasses['leverage-control']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ class: "slider-header" },
});
/** @type {__VLS_StyleScopedClasses['slider-header']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.label, __VLS_intrinsics.label)({});
__VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({});
(__VLS_ctx.leverage);
__VLS_asFunctionalElement1(__VLS_intrinsics.input)({
    ...{ onChange: (__VLS_ctx.handleLeverageChange) },
    ...{ onInput: (__VLS_ctx.calculateAmountFromPercent) },
    ...{ onPointerdown: () => { } },
    ...{ onMousedown: () => { } },
    type: "range",
    min: "1",
    max: "125",
    ...{ class: "custom-slider no-drag" },
    ...{ style: {} },
});
(__VLS_ctx.leverage);
/** @type {__VLS_StyleScopedClasses['custom-slider']} */ ;
/** @type {__VLS_StyleScopedClasses['no-drag']} */ ;
if (__VLS_ctx.orderType === 'LIMIT') {
    __VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
        ...{ class: "input-group" },
    });
    /** @type {__VLS_StyleScopedClasses['input-group']} */ ;
    __VLS_asFunctionalElement1(__VLS_intrinsics.label, __VLS_intrinsics.label)({});
    __VLS_asFunctionalElement1(__VLS_intrinsics.input)({
        ...{ onInput: (__VLS_ctx.debouncedCalculate) },
        type: "number",
        placeholder: "0.00",
    });
    (__VLS_ctx.price);
}
else {
    __VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
        ...{ class: "input-group disabled" },
    });
    /** @type {__VLS_StyleScopedClasses['input-group']} */ ;
    /** @type {__VLS_StyleScopedClasses['disabled']} */ ;
    __VLS_asFunctionalElement1(__VLS_intrinsics.label, __VLS_intrinsics.label)({});
    __VLS_asFunctionalElement1(__VLS_intrinsics.input)({
        type: "text",
        value: "市价 (取最新价)",
        disabled: true,
    });
}
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ class: "input-group" },
});
/** @type {__VLS_StyleScopedClasses['input-group']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.label, __VLS_intrinsics.label)({});
(__VLS_ctx.baseAsset);
__VLS_asFunctionalElement1(__VLS_intrinsics.input)({
    ...{ onInput: (__VLS_ctx.handleAmountInput) },
    type: "number",
    placeholder: "0.00",
});
(__VLS_ctx.amount);
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ class: "percentage-control" },
});
/** @type {__VLS_StyleScopedClasses['percentage-control']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ class: "slider-header" },
});
/** @type {__VLS_StyleScopedClasses['slider-header']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.label, __VLS_intrinsics.label)({});
__VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({});
(__VLS_ctx.positionPercent);
__VLS_asFunctionalElement1(__VLS_intrinsics.input)({
    ...{ onInput: (__VLS_ctx.calculateAmountFromPercent) },
    ...{ onPointerdown: () => { } },
    ...{ onMousedown: () => { } },
    type: "range",
    min: "0",
    max: "100",
    step: "1",
    ...{ class: "custom-slider percent-slider no-drag" },
    ...{ style: {} },
});
(__VLS_ctx.positionPercent);
/** @type {__VLS_StyleScopedClasses['custom-slider']} */ ;
/** @type {__VLS_StyleScopedClasses['percent-slider']} */ ;
/** @type {__VLS_StyleScopedClasses['no-drag']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ class: "percentage-marks" },
});
/** @type {__VLS_StyleScopedClasses['percentage-marks']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({
    ...{ onClick: (...[$event]) => {
            __VLS_ctx.setPercentage(25);
            // @ts-ignore
            [orderType, orderType, marketStore, leverage, leverage, handleLeverageChange, calculateAmountFromPercent, calculateAmountFromPercent, debouncedCalculate, price, baseAsset, handleAmountInput, amount, positionPercent, positionPercent, setPercentage,];
        } },
});
__VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({
    ...{ onClick: (...[$event]) => {
            __VLS_ctx.setPercentage(50);
            // @ts-ignore
            [setPercentage,];
        } },
});
__VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({
    ...{ onClick: (...[$event]) => {
            __VLS_ctx.setPercentage(75);
            // @ts-ignore
            [setPercentage,];
        } },
});
__VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({
    ...{ onClick: (...[$event]) => {
            __VLS_ctx.setPercentage(100);
            // @ts-ignore
            [setPercentage,];
        } },
});
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ class: "order-summary" },
});
/** @type {__VLS_StyleScopedClasses['order-summary']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ class: "summary-row" },
});
/** @type {__VLS_StyleScopedClasses['summary-row']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({});
__VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({});
(__VLS_ctx.requiredMargin.toFixed(2));
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ class: "summary-row" },
});
/** @type {__VLS_StyleScopedClasses['summary-row']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({});
__VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({});
(__VLS_ctx.estimatedValue.toFixed(2));
if (parseFloat(__VLS_ctx.amount) > 0) {
    __VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
        ...{ class: "risk-preview" },
        ...{ class: (__VLS_ctx.riskLevel) },
    });
    /** @type {__VLS_StyleScopedClasses['risk-preview']} */ ;
    __VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
        ...{ class: "risk-row" },
    });
    /** @type {__VLS_StyleScopedClasses['risk-row']} */ ;
    __VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({});
    __VLS_asFunctionalElement1(__VLS_intrinsics.strong, __VLS_intrinsics.strong)({
        ...{ class: "text-green" },
    });
    /** @type {__VLS_StyleScopedClasses['text-green']} */ ;
    (__VLS_ctx.previewLiqLong > 0 ? __VLS_ctx.previewLiqLong.toFixed(4) : '--');
    __VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({});
    __VLS_asFunctionalElement1(__VLS_intrinsics.strong, __VLS_intrinsics.strong)({
        ...{ class: "text-red" },
    });
    /** @type {__VLS_StyleScopedClasses['text-red']} */ ;
    (__VLS_ctx.previewLiqShort > 0 ? __VLS_ctx.previewLiqShort.toFixed(4) : '--');
    __VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
        ...{ class: "risk-bar-bg" },
    });
    /** @type {__VLS_StyleScopedClasses['risk-bar-bg']} */ ;
    __VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
        ...{ class: "risk-bar-fill" },
        ...{ style: ({ width: __VLS_ctx.riskDistancePct + '%' }) },
    });
    /** @type {__VLS_StyleScopedClasses['risk-bar-fill']} */ ;
}
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ class: "action-buttons-fixed" },
});
/** @type {__VLS_StyleScopedClasses['action-buttons-fixed']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.button, __VLS_intrinsics.button)({
    ...{ onClick: (__VLS_ctx.handleBuy) },
    ...{ class: "btn-buy" },
    ...{ class: ({ loading: __VLS_ctx.isSubmitting }) },
    disabled: (__VLS_ctx.isSubmitting || !__VLS_ctx.isValid),
});
/** @type {__VLS_StyleScopedClasses['btn-buy']} */ ;
/** @type {__VLS_StyleScopedClasses['loading']} */ ;
(__VLS_ctx.isSubmitting ? '提交中...' : '买入 / 做多');
__VLS_asFunctionalElement1(__VLS_intrinsics.button, __VLS_intrinsics.button)({
    ...{ onClick: (__VLS_ctx.handleSell) },
    ...{ class: "btn-sell" },
    ...{ class: ({ loading: __VLS_ctx.isSubmitting }) },
    disabled: (__VLS_ctx.isSubmitting || !__VLS_ctx.isValid),
});
/** @type {__VLS_StyleScopedClasses['btn-sell']} */ ;
/** @type {__VLS_StyleScopedClasses['loading']} */ ;
(__VLS_ctx.isSubmitting ? '提交中...' : '卖出 / 做空');
// @ts-ignore
[amount, requiredMargin, estimatedValue, riskLevel, previewLiqLong, previewLiqLong, previewLiqShort, previewLiqShort, riskDistancePct, handleBuy, isSubmitting, isSubmitting, isSubmitting, isSubmitting, isSubmitting, isSubmitting, isValid, isValid, handleSell,];
const __VLS_export = (await import('vue')).defineComponent({});
export default {};

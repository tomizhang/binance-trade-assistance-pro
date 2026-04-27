import { ref, computed, onMounted } from 'vue';
import { useMarketStore } from '@/store/market';
const emit = defineEmits(['add-kline', 'collapse']);
const marketStore = useMarketStore();
const searchQuery = ref('');
const sortKey = ref('volume');
const sortOrder = ref(-1); // -1 是降序 (从大到小), 1 是升序
const collapseSidebar = () => {
    emit('collapse');
};
const handleAddKline = (symbol) => {
    emit('add-kline', symbol);
};
// 点击排序逻辑
const sortBy = (key) => {
    if (sortKey.value === key) {
        sortOrder.value = sortOrder.value === 1 ? -1 : 1;
    }
    else {
        sortKey.value = key;
        sortOrder.value = -1; // 切换新维度时，默认显示最大的（降序）
    }
};
// 🌟 修复 1：安全的排序算法，防止 NaN 破坏排序
const filteredAndSortedList = computed(() => {
    let list = Object.keys(marketStore.marketTickers).map(symbol => ({
        symbol,
        ...marketStore.marketTickers[symbol]
    }));
    if (searchQuery.value) {
        const q = searchQuery.value.toUpperCase();
        list = list.filter(item => item.symbol.includes(q));
    }
    list.sort((a, b) => {
        // 强制转换为数字，如果遇到 undefined 或非数字，当做 0 处理
        const valA = Number(a[sortKey.value]) || 0;
        const valB = Number(b[sortKey.value]) || 0;
        // sortOrder === 1 为升序 (小在前)，-1 为降序 (大在前)
        return sortOrder.value === 1 ? (valA - valB) : (valB - valA);
    });
    return list;
});
const getPrecision = (price) => {
    if (price >= 1000)
        return 1;
    if (price >= 1)
        return 3;
    if (price >= 0.01)
        return 4;
    return 6;
};
const formatVolume = (vol) => {
    if (!vol)
        return '0.00';
    if (vol >= 1e9)
        return (vol / 1e9).toFixed(2) + 'B';
    if (vol >= 1e6)
        return (vol / 1e6).toFixed(2) + 'M';
    if (vol >= 1e3)
        return (vol / 1e3).toFixed(2) + 'K';
    return vol.toFixed(0);
};
const getColorClass = (change) => {
    if (!change)
        return '';
    return change >= 0 ? 'text-green' : 'text-red';
};
onMounted(() => {
    marketStore.connectAllTickers();
});
const __VLS_ctx = {
    ...{},
    ...{},
    ...{},
    ...{},
    ...{},
};
let __VLS_components;
let __VLS_intrinsics;
let __VLS_directives;
/** @type {__VLS_StyleScopedClasses['search-box']} */ ;
/** @type {__VLS_StyleScopedClasses['search-box']} */ ;
/** @type {__VLS_StyleScopedClasses['search-box']} */ ;
/** @type {__VLS_StyleScopedClasses['icon-btn']} */ ;
/** @type {__VLS_StyleScopedClasses['list-header']} */ ;
/** @type {__VLS_StyleScopedClasses['list-header']} */ ;
/** @type {__VLS_StyleScopedClasses['list-header']} */ ;
/** @type {__VLS_StyleScopedClasses['sortable']} */ ;
/** @type {__VLS_StyleScopedClasses['sort-icon']} */ ;
/** @type {__VLS_StyleScopedClasses['list-content']} */ ;
/** @type {__VLS_StyleScopedClasses['list-content']} */ ;
/** @type {__VLS_StyleScopedClasses['list-item']} */ ;
/** @type {__VLS_StyleScopedClasses['col-main']} */ ;
/** @type {__VLS_StyleScopedClasses['col-price']} */ ;
/** @type {__VLS_StyleScopedClasses['col-change']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ class: "market-list-module" },
});
/** @type {__VLS_StyleScopedClasses['market-list-module']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ class: "module-header" },
});
/** @type {__VLS_StyleScopedClasses['module-header']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ class: "search-box" },
});
/** @type {__VLS_StyleScopedClasses['search-box']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.svg, __VLS_intrinsics.svg)({
    viewBox: "0 0 24 24",
    width: "14",
    height: "14",
    stroke: "currentColor",
    'stroke-width': "2",
    fill: "none",
});
__VLS_asFunctionalElement1(__VLS_intrinsics.circle, __VLS_intrinsics.circle)({
    cx: "11",
    cy: "11",
    r: "8",
});
__VLS_asFunctionalElement1(__VLS_intrinsics.line, __VLS_intrinsics.line)({
    x1: "21",
    y1: "21",
    x2: "16.65",
    y2: "16.65",
});
__VLS_asFunctionalElement1(__VLS_intrinsics.input)({
    value: (__VLS_ctx.searchQuery),
    type: "text",
    placeholder: "搜索 (如 BTC)",
});
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ class: "header-actions" },
});
/** @type {__VLS_StyleScopedClasses['header-actions']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.button, __VLS_intrinsics.button)({
    ...{ onClick: (__VLS_ctx.collapseSidebar) },
    ...{ class: "icon-btn collapse-btn" },
    title: "收起侧边栏",
});
/** @type {__VLS_StyleScopedClasses['icon-btn']} */ ;
/** @type {__VLS_StyleScopedClasses['collapse-btn']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.svg, __VLS_intrinsics.svg)({
    viewBox: "0 0 24 24",
    width: "16",
    height: "16",
    stroke: "currentColor",
    'stroke-width': "2",
    fill: "none",
});
__VLS_asFunctionalElement1(__VLS_intrinsics.rect, __VLS_intrinsics.rect)({
    x: "3",
    y: "3",
    width: "18",
    height: "18",
    rx: "2",
    ry: "2",
});
__VLS_asFunctionalElement1(__VLS_intrinsics.line, __VLS_intrinsics.line)({
    x1: "9",
    y1: "3",
    x2: "9",
    y2: "21",
});
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ class: "list-header" },
});
/** @type {__VLS_StyleScopedClasses['list-header']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ onClick: (...[$event]) => {
            __VLS_ctx.sortBy('volume');
            // @ts-ignore
            [searchQuery, collapseSidebar, sortBy,];
        } },
    ...{ class: "col-main sortable" },
});
/** @type {__VLS_StyleScopedClasses['col-main']} */ ;
/** @type {__VLS_StyleScopedClasses['sortable']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({});
__VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({
    ...{ class: "sort-icon" },
    ...{ class: ({ active: __VLS_ctx.sortKey === 'volume' }) },
});
/** @type {__VLS_StyleScopedClasses['sort-icon']} */ ;
/** @type {__VLS_StyleScopedClasses['active']} */ ;
(__VLS_ctx.sortKey === 'volume' && __VLS_ctx.sortOrder === 1 ? '↑' : '↓');
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ onClick: (...[$event]) => {
            __VLS_ctx.sortBy('lastPrice');
            // @ts-ignore
            [sortBy, sortKey, sortKey, sortOrder,];
        } },
    ...{ class: "col-price sortable" },
});
/** @type {__VLS_StyleScopedClasses['col-price']} */ ;
/** @type {__VLS_StyleScopedClasses['sortable']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({});
__VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({
    ...{ class: "sort-icon" },
    ...{ class: ({ active: __VLS_ctx.sortKey === 'lastPrice' }) },
});
/** @type {__VLS_StyleScopedClasses['sort-icon']} */ ;
/** @type {__VLS_StyleScopedClasses['active']} */ ;
(__VLS_ctx.sortKey === 'lastPrice' && __VLS_ctx.sortOrder === 1 ? '↑' : '↓');
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ onClick: (...[$event]) => {
            __VLS_ctx.sortBy('priceChangePercent');
            // @ts-ignore
            [sortBy, sortKey, sortKey, sortOrder,];
        } },
    ...{ class: "col-change sortable" },
});
/** @type {__VLS_StyleScopedClasses['col-change']} */ ;
/** @type {__VLS_StyleScopedClasses['sortable']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({});
__VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({
    ...{ class: "sort-icon" },
    ...{ class: ({ active: __VLS_ctx.sortKey === 'priceChangePercent' }) },
});
/** @type {__VLS_StyleScopedClasses['sort-icon']} */ ;
/** @type {__VLS_StyleScopedClasses['active']} */ ;
(__VLS_ctx.sortKey === 'priceChangePercent' && __VLS_ctx.sortOrder === 1 ? '↑' : '↓');
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ class: "list-content" },
});
/** @type {__VLS_StyleScopedClasses['list-content']} */ ;
for (const [item] of __VLS_vFor((__VLS_ctx.filteredAndSortedList))) {
    __VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
        ...{ onDblclick: (...[$event]) => {
                __VLS_ctx.handleAddKline(item.symbol);
                // @ts-ignore
                [sortKey, sortKey, sortOrder, filteredAndSortedList, handleAddKline,];
            } },
        ...{ onClick: (...[$event]) => {
                __VLS_ctx.marketStore.setCurrentSymbol(item.symbol);
                // @ts-ignore
                [marketStore,];
            } },
        ...{ class: "list-item" },
        key: (item.symbol),
    });
    /** @type {__VLS_StyleScopedClasses['list-item']} */ ;
    __VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
        ...{ class: "col-main" },
    });
    /** @type {__VLS_StyleScopedClasses['col-main']} */ ;
    __VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({
        ...{ class: "symbol-name" },
    });
    /** @type {__VLS_StyleScopedClasses['symbol-name']} */ ;
    (item.symbol.replace('USDT', ''));
    __VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({
        ...{ class: "quote" },
    });
    /** @type {__VLS_StyleScopedClasses['quote']} */ ;
    __VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({
        ...{ class: "sub-text" },
    });
    /** @type {__VLS_StyleScopedClasses['sub-text']} */ ;
    (__VLS_ctx.formatVolume(item.volume));
    __VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
        ...{ class: "col-price" },
    });
    /** @type {__VLS_StyleScopedClasses['col-price']} */ ;
    __VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({
        ...{ class: "price-text" },
        ...{ class: (__VLS_ctx.getColorClass(item.priceChangePercent)) },
    });
    /** @type {__VLS_StyleScopedClasses['price-text']} */ ;
    (item.lastPrice ? item.lastPrice.toFixed(__VLS_ctx.getPrecision(item.lastPrice)) : '--');
    __VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({
        ...{ class: "sub-text funding" },
        title: "资金费率",
    });
    /** @type {__VLS_StyleScopedClasses['sub-text']} */ ;
    /** @type {__VLS_StyleScopedClasses['funding']} */ ;
    (item.fundingRate !== undefined ? item.fundingRate.toFixed(4) + '%' : '--');
    __VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
        ...{ class: "col-change" },
    });
    /** @type {__VLS_StyleScopedClasses['col-change']} */ ;
    __VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({
        ...{ class: "change-text" },
        ...{ class: (__VLS_ctx.getColorClass(item.priceChangePercent)) },
    });
    /** @type {__VLS_StyleScopedClasses['change-text']} */ ;
    (item.priceChangePercent > 0 ? '+' : '');
    (item.priceChangePercent ? item.priceChangePercent.toFixed(2) : '0.00');
    // @ts-ignore
    [formatVolume, getColorClass, getColorClass, getPrecision,];
}
if (__VLS_ctx.filteredAndSortedList.length === 0) {
    __VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
        ...{ class: "empty-state" },
    });
    /** @type {__VLS_StyleScopedClasses['empty-state']} */ ;
    __VLS_asFunctionalElement1(__VLS_intrinsics.p, __VLS_intrinsics.p)({});
}
// @ts-ignore
[filteredAndSortedList,];
const __VLS_export = (await import('vue')).defineComponent({
    emits: {},
});
export default {};

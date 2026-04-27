import { ref, computed } from 'vue';
import { useMarketStore } from '@/store/market';
import { useToast } from '@/utils/useToast';
import { calculateLiquidationPrice } from '@/utils/tradeUtils'; // 🌟 引入强平计算引擎
const marketStore = useMarketStore();
const toast = useToast();
const activeTab = ref('ACTIVE');
const displayMode = ref('TOKEN');
const toggleDisplayMode = () => { displayMode.value = displayMode.value === 'TOKEN' ? 'USDT' : 'TOKEN'; };
const searchQuery = ref('');
const showCurrentOnly = ref(false);
const switchToHistory = () => {
    activeTab.value = 'HISTORY';
    marketStore.fetchPositionHistory(showCurrentOnly.value ? marketStore.currentSymbol : undefined);
};
// ==========================================
// 排序与过滤机制
// ==========================================
const sortKey = ref('time');
const sortDesc = ref(true);
const setSort = (key) => {
    if (sortKey.value === key) {
        sortDesc.value = !sortDesc.value;
    }
    else {
        sortKey.value = key;
        sortDesc.value = true;
    }
};
const getSortIcon = (key) => {
    if (sortKey.value !== key)
        return '⇕';
    return sortDesc.value ? '⬇' : '⬆';
};
const filteredPositions = computed(() => {
    let result = marketStore.positions;
    if (showCurrentOnly.value)
        result = result.filter(pos => pos.symbol === marketStore.currentSymbol);
    if (searchQuery.value.trim()) {
        const q = searchQuery.value.trim().toUpperCase();
        result = result.filter(pos => pos.symbol.includes(q));
    }
    return result;
});
const sortedActivePositions = computed(() => {
    const arr = [...filteredPositions.value];
    return arr.sort((a, b) => {
        let valA, valB;
        switch (sortKey.value) {
            case 'time':
                valA = a.updateTime || 0;
                valB = b.updateTime || 0;
                break;
            case 'symbol':
                valA = a.symbol;
                valB = b.symbol;
                break;
            case 'side':
                valA = a.side === 'LONG' ? 1 : -1;
                valB = b.side === 'LONG' ? 1 : -1;
                break;
            case 'amount':
                valA = Math.abs(a.amount);
                valB = Math.abs(b.amount);
                break;
            case 'entryPrice':
                valA = a.entryPrice;
                valB = b.entryPrice;
                break;
            case 'pnl':
                valA = getRealtimePnl(a);
                valB = getRealtimePnl(b);
                break;
            default:
                valA = 0;
                valB = 0;
        }
        if (typeof valA === 'string' && typeof valB === 'string') {
            return sortDesc.value ? valB.localeCompare(valA) : valA.localeCompare(valB);
        }
        return sortDesc.value ? valB - valA : valA - valB;
    });
});
// ==========================================
// 价格、盈亏、保证金、强平计算
// ==========================================
const getCurrentPrice = (symbol) => marketStore.marketTickers[symbol]?.lastPrice || 0;
const getPriceColor = (symbol) => (marketStore.marketTickers[symbol]?.priceChangePercent || 0) >= 0 ? 'text-green' : 'text-red';
const getTickDecimals = (symbol) => {
    const rule = marketStore.symbolRules[symbol];
    if (!rule || !rule.tickSize)
        return 2;
    const match = rule.tickSize.match(/\.([0]+)1/);
    return match ? match[1].length + 1 : 0;
};
// 🌟 强平价推演核心逻辑 (包含全仓护城河计算)
const getLiqPrice = (pos) => {
    const otherCrossPositions = marketStore.positions
        .filter(p => p.symbol !== pos.symbol && p.marginType === 'cross')
        .map(p => {
        const pr = getCurrentPrice(p.symbol) || p.entryPrice;
        const amt = Math.abs(p.amount);
        return {
            ...p,
            unrealizedPnL: p.side === 'LONG' ? (pr - p.entryPrice) * amt : (p.entryPrice - pr) * amt,
            markPrice: pr
        };
    });
    return calculateLiquidationPrice({
        symbol: pos.symbol,
        side: pos.side,
        amount: pos.amount,
        entryPrice: pos.entryPrice,
        leverage: pos.leverage || 1,
        marginType: pos.marginType || 'isolated'
    }, marketStore.usdtBalance, otherCrossPositions);
};
const getDisplayAmount = (pos) => {
    const amount = Math.abs(pos.amount);
    if (displayMode.value === 'TOKEN')
        return `${amount} ${pos.symbol.replace('USDT', '')}`;
    return `${(amount * (getCurrentPrice(pos.symbol) || pos.entryPrice)).toFixed(2)}`;
};
const getUsedMargin = (pos) => {
    const notionalValue = Math.abs(pos.amount) * pos.entryPrice;
    const lev = pos.leverage || 1;
    return (notionalValue / lev).toFixed(2);
};
const getRealtimePnl = (pos) => {
    const currentPrice = getCurrentPrice(pos.symbol);
    if (!currentPrice)
        return pos.unrealizedPnL || 0;
    const amount = Math.abs(pos.amount);
    return pos.side === 'LONG' ? (currentPrice - pos.entryPrice) * amount : (pos.entryPrice - currentPrice) * amount;
};
const getRoe = (pos) => {
    const currentPrice = getCurrentPrice(pos.symbol);
    if (!currentPrice || !pos.entryPrice)
        return 0;
    const priceDiffPct = ((currentPrice - pos.entryPrice) / pos.entryPrice) * 100;
    const directionMultiplier = pos.side === 'LONG' ? 1 : -1;
    const lev = pos.leverage || 1;
    return priceDiffPct * directionMultiplier * lev;
};
const getPnlClass = (pnl) => {
    if (pnl > 0)
        return 'text-green';
    if (pnl < 0)
        return 'text-red';
    return '';
};
const formatDateTime = (timestamp) => {
    if (!timestamp)
        return '-';
    const d = new Date(timestamp);
    const pad = (n) => n.toString().padStart(2, '0');
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
};
// ==========================================
// 订单操作
// ==========================================
const closePosition = async (pos) => {
    try {
        toast.info(`正在平仓 ${pos.symbol}...`);
        const res = await fetch('http://localhost:5000/api/order/place', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                symbol: pos.symbol,
                side: pos.side === 'LONG' ? 'SELL' : 'BUY',
                type: 'MARKET',
                quantity: Math.abs(pos.amount),
                reduceOnly: true
            })
        });
        if (res.ok)
            toast.success(`${pos.symbol} 平仓指令已发送`);
        else {
            const data = await res.json();
            throw new Error(data.message || '平仓拒绝');
        }
    }
    catch (e) {
        toast.error('平仓失败: ' + e.message);
    }
};
const __VLS_ctx = {
    ...{},
    ...{},
};
let __VLS_components;
let __VLS_intrinsics;
let __VLS_directives;
/** @type {__VLS_StyleScopedClasses['tabs']} */ ;
/** @type {__VLS_StyleScopedClasses['tabs']} */ ;
/** @type {__VLS_StyleScopedClasses['mini-toggle']} */ ;
/** @type {__VLS_StyleScopedClasses['sortable']} */ ;
/** @type {__VLS_StyleScopedClasses['btn-close']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ class: "position-module" },
});
/** @type {__VLS_StyleScopedClasses['position-module']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ class: "module-header" },
});
/** @type {__VLS_StyleScopedClasses['module-header']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ class: "tabs" },
});
/** @type {__VLS_StyleScopedClasses['tabs']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.button, __VLS_intrinsics.button)({
    ...{ onClick: (...[$event]) => {
            __VLS_ctx.activeTab = 'ACTIVE';
            // @ts-ignore
            [activeTab,];
        } },
    ...{ class: ({ active: __VLS_ctx.activeTab === 'ACTIVE' }) },
});
/** @type {__VLS_StyleScopedClasses['active']} */ ;
(__VLS_ctx.filteredPositions.length);
__VLS_asFunctionalElement1(__VLS_intrinsics.button, __VLS_intrinsics.button)({
    ...{ onClick: (__VLS_ctx.switchToHistory) },
    ...{ class: ({ active: __VLS_ctx.activeTab === 'HISTORY' }) },
});
/** @type {__VLS_StyleScopedClasses['active']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({
    ...{ class: "balance-info" },
});
/** @type {__VLS_StyleScopedClasses['balance-info']} */ ;
(__VLS_ctx.marketStore.usdtBalance.toFixed(2));
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ class: "toolbar" },
});
/** @type {__VLS_StyleScopedClasses['toolbar']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ class: "filter-group" },
});
/** @type {__VLS_StyleScopedClasses['filter-group']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.input)({
    type: "text",
    ...{ class: "search-input" },
    value: (__VLS_ctx.searchQuery),
    placeholder: "🔍 搜索币种...",
});
/** @type {__VLS_StyleScopedClasses['search-input']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.label, __VLS_intrinsics.label)({
    ...{ class: "checkbox-label" },
    title: "只显示当前K线图正在看的币种",
});
/** @type {__VLS_StyleScopedClasses['checkbox-label']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.input)({
    type: "checkbox",
});
(__VLS_ctx.showCurrentOnly);
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ class: "position-table" },
});
/** @type {__VLS_StyleScopedClasses['position-table']} */ ;
if (__VLS_ctx.activeTab === 'ACTIVE') {
    __VLS_asFunctionalElement1(__VLS_intrinsics.table, __VLS_intrinsics.table)({});
    __VLS_asFunctionalElement1(__VLS_intrinsics.thead, __VLS_intrinsics.thead)({});
    __VLS_asFunctionalElement1(__VLS_intrinsics.tr, __VLS_intrinsics.tr)({});
    __VLS_asFunctionalElement1(__VLS_intrinsics.th, __VLS_intrinsics.th)({
        ...{ onClick: (...[$event]) => {
                if (!(__VLS_ctx.activeTab === 'ACTIVE'))
                    return;
                __VLS_ctx.setSort('time');
                // @ts-ignore
                [activeTab, activeTab, activeTab, filteredPositions, switchToHistory, marketStore, searchQuery, showCurrentOnly, setSort,];
            } },
        ...{ class: "sortable" },
    });
    /** @type {__VLS_StyleScopedClasses['sortable']} */ ;
    (__VLS_ctx.getSortIcon('time'));
    __VLS_asFunctionalElement1(__VLS_intrinsics.th, __VLS_intrinsics.th)({
        ...{ onClick: (...[$event]) => {
                if (!(__VLS_ctx.activeTab === 'ACTIVE'))
                    return;
                __VLS_ctx.setSort('symbol');
                // @ts-ignore
                [setSort, getSortIcon,];
            } },
        ...{ class: "sortable" },
    });
    /** @type {__VLS_StyleScopedClasses['sortable']} */ ;
    (__VLS_ctx.getSortIcon('symbol'));
    __VLS_asFunctionalElement1(__VLS_intrinsics.th, __VLS_intrinsics.th)({
        ...{ onClick: (...[$event]) => {
                if (!(__VLS_ctx.activeTab === 'ACTIVE'))
                    return;
                __VLS_ctx.setSort('side');
                // @ts-ignore
                [setSort, getSortIcon,];
            } },
        ...{ class: "sortable" },
    });
    /** @type {__VLS_StyleScopedClasses['sortable']} */ ;
    (__VLS_ctx.getSortIcon('side'));
    __VLS_asFunctionalElement1(__VLS_intrinsics.th, __VLS_intrinsics.th)({
        ...{ onClick: (...[$event]) => {
                if (!(__VLS_ctx.activeTab === 'ACTIVE'))
                    return;
                __VLS_ctx.setSort('amount');
                // @ts-ignore
                [setSort, getSortIcon,];
            } },
        ...{ class: "sortable amount-th" },
    });
    /** @type {__VLS_StyleScopedClasses['sortable']} */ ;
    /** @type {__VLS_StyleScopedClasses['amount-th']} */ ;
    __VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
        ...{ class: "th-stacked" },
    });
    /** @type {__VLS_StyleScopedClasses['th-stacked']} */ ;
    __VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({});
    (__VLS_ctx.getSortIcon('amount'));
    __VLS_asFunctionalElement1(__VLS_intrinsics.button, __VLS_intrinsics.button)({
        ...{ onClick: (__VLS_ctx.toggleDisplayMode) },
        ...{ class: "mini-toggle" },
    });
    /** @type {__VLS_StyleScopedClasses['mini-toggle']} */ ;
    (__VLS_ctx.displayMode === 'TOKEN' ? 'USDT' : '代币');
    __VLS_asFunctionalElement1(__VLS_intrinsics.th, __VLS_intrinsics.th)({
        ...{ onClick: (...[$event]) => {
                if (!(__VLS_ctx.activeTab === 'ACTIVE'))
                    return;
                __VLS_ctx.setSort('entryPrice');
                // @ts-ignore
                [setSort, getSortIcon, toggleDisplayMode, displayMode,];
            } },
        ...{ class: "sortable" },
    });
    /** @type {__VLS_StyleScopedClasses['sortable']} */ ;
    __VLS_asFunctionalElement1(__VLS_intrinsics.th, __VLS_intrinsics.th)({
        title: "保证金占用 = 名义价值 / 杠杆",
    });
    __VLS_asFunctionalElement1(__VLS_intrinsics.th, __VLS_intrinsics.th)({
        ...{ onClick: (...[$event]) => {
                if (!(__VLS_ctx.activeTab === 'ACTIVE'))
                    return;
                __VLS_ctx.setSort('pnl');
                // @ts-ignore
                [setSort,];
            } },
        ...{ class: "sortable" },
    });
    /** @type {__VLS_StyleScopedClasses['sortable']} */ ;
    (__VLS_ctx.getSortIcon('pnl'));
    __VLS_asFunctionalElement1(__VLS_intrinsics.th, __VLS_intrinsics.th)({});
    __VLS_asFunctionalElement1(__VLS_intrinsics.tbody, __VLS_intrinsics.tbody)({});
    for (const [pos] of __VLS_vFor((__VLS_ctx.sortedActivePositions))) {
        __VLS_asFunctionalElement1(__VLS_intrinsics.tr, __VLS_intrinsics.tr)({
            ...{ onClick: (...[$event]) => {
                    if (!(__VLS_ctx.activeTab === 'ACTIVE'))
                        return;
                    __VLS_ctx.marketStore.setCurrentSymbol(pos.symbol);
                    // @ts-ignore
                    [marketStore, getSortIcon, sortedActivePositions,];
                } },
            key: (pos.symbol),
            ...{ class: ({ 'active-row': __VLS_ctx.marketStore.currentSymbol === pos.symbol }) },
        });
        /** @type {__VLS_StyleScopedClasses['active-row']} */ ;
        __VLS_asFunctionalElement1(__VLS_intrinsics.td, __VLS_intrinsics.td)({
            ...{ class: "font-mono text-muted" },
        });
        /** @type {__VLS_StyleScopedClasses['font-mono']} */ ;
        /** @type {__VLS_StyleScopedClasses['text-muted']} */ ;
        (__VLS_ctx.formatDateTime(pos.updateTime));
        __VLS_asFunctionalElement1(__VLS_intrinsics.td, __VLS_intrinsics.td)({});
        __VLS_asFunctionalElement1(__VLS_intrinsics.strong, __VLS_intrinsics.strong)({});
        (pos.symbol);
        __VLS_asFunctionalElement1(__VLS_intrinsics.td, __VLS_intrinsics.td)({});
        __VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
            ...{ class: "side-col" },
        });
        /** @type {__VLS_StyleScopedClasses['side-col']} */ ;
        __VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({
            ...{ class: (pos.side === 'LONG' ? 'text-green' : 'text-red') },
            ...{ class: "side-badge" },
        });
        /** @type {__VLS_StyleScopedClasses['side-badge']} */ ;
        (pos.side === 'LONG' ? '做多' : '做空');
        __VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({
            ...{ class: "margin-badge" },
        });
        /** @type {__VLS_StyleScopedClasses['margin-badge']} */ ;
        (pos.marginType === 'cross' ? '全仓' : '逐仓');
        __VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({
            ...{ class: "leverage-text" },
        });
        /** @type {__VLS_StyleScopedClasses['leverage-text']} */ ;
        (pos.leverage ? pos.leverage + 'x' : '-x');
        __VLS_asFunctionalElement1(__VLS_intrinsics.td, __VLS_intrinsics.td)({
            ...{ class: "font-mono" },
        });
        /** @type {__VLS_StyleScopedClasses['font-mono']} */ ;
        (__VLS_ctx.getDisplayAmount(pos));
        __VLS_asFunctionalElement1(__VLS_intrinsics.td, __VLS_intrinsics.td)({
            ...{ class: "font-mono price-col" },
        });
        /** @type {__VLS_StyleScopedClasses['font-mono']} */ ;
        /** @type {__VLS_StyleScopedClasses['price-col']} */ ;
        __VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({
            ...{ class: "entry-price" },
            title: "开仓均价",
        });
        /** @type {__VLS_StyleScopedClasses['entry-price']} */ ;
        (pos.entryPrice.toFixed(__VLS_ctx.getTickDecimals(pos.symbol)));
        __VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({
            ...{ class: (__VLS_ctx.getPriceColor(pos.symbol)) },
            ...{ class: "current-price" },
            title: "最新价",
        });
        /** @type {__VLS_StyleScopedClasses['current-price']} */ ;
        (__VLS_ctx.getCurrentPrice(pos.symbol).toFixed(__VLS_ctx.getTickDecimals(pos.symbol)));
        __VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({
            ...{ class: "liq-price" },
            title: "预估强平价",
        });
        /** @type {__VLS_StyleScopedClasses['liq-price']} */ ;
        (__VLS_ctx.getLiqPrice(pos) > 0 ? __VLS_ctx.getLiqPrice(pos).toFixed(__VLS_ctx.getTickDecimals(pos.symbol)) : '--');
        __VLS_asFunctionalElement1(__VLS_intrinsics.td, __VLS_intrinsics.td)({
            ...{ class: "font-mono" },
        });
        /** @type {__VLS_StyleScopedClasses['font-mono']} */ ;
        (__VLS_ctx.getUsedMargin(pos));
        __VLS_asFunctionalElement1(__VLS_intrinsics.td, __VLS_intrinsics.td)({
            ...{ class: "font-mono pnl-col" },
            ...{ class: (__VLS_ctx.getPnlClass(__VLS_ctx.getRealtimePnl(pos))) },
        });
        /** @type {__VLS_StyleScopedClasses['font-mono']} */ ;
        /** @type {__VLS_StyleScopedClasses['pnl-col']} */ ;
        __VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({
            ...{ class: "pnl-value" },
        });
        /** @type {__VLS_StyleScopedClasses['pnl-value']} */ ;
        (__VLS_ctx.getRealtimePnl(pos) > 0 ? '+' : '');
        (__VLS_ctx.getRealtimePnl(pos).toFixed(2));
        __VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({
            ...{ class: "pnl-roe" },
        });
        /** @type {__VLS_StyleScopedClasses['pnl-roe']} */ ;
        (__VLS_ctx.getRoe(pos) > 0 ? '+' : '');
        (__VLS_ctx.getRoe(pos).toFixed(2));
        __VLS_asFunctionalElement1(__VLS_intrinsics.td, __VLS_intrinsics.td)({});
        __VLS_asFunctionalElement1(__VLS_intrinsics.button, __VLS_intrinsics.button)({
            ...{ onClick: (...[$event]) => {
                    if (!(__VLS_ctx.activeTab === 'ACTIVE'))
                        return;
                    __VLS_ctx.closePosition(pos);
                    // @ts-ignore
                    [marketStore, formatDateTime, getDisplayAmount, getTickDecimals, getTickDecimals, getTickDecimals, getPriceColor, getCurrentPrice, getLiqPrice, getLiqPrice, getUsedMargin, getPnlClass, getRealtimePnl, getRealtimePnl, getRealtimePnl, getRoe, getRoe, closePosition,];
                } },
            ...{ class: "btn-close" },
        });
        /** @type {__VLS_StyleScopedClasses['btn-close']} */ ;
        // @ts-ignore
        [];
    }
    if (__VLS_ctx.sortedActivePositions.length === 0) {
        __VLS_asFunctionalElement1(__VLS_intrinsics.tr, __VLS_intrinsics.tr)({});
        __VLS_asFunctionalElement1(__VLS_intrinsics.td, __VLS_intrinsics.td)({
            colspan: "8",
            ...{ class: "empty-state" },
        });
        /** @type {__VLS_StyleScopedClasses['empty-state']} */ ;
    }
}
if (__VLS_ctx.activeTab === 'HISTORY') {
    __VLS_asFunctionalElement1(__VLS_intrinsics.table, __VLS_intrinsics.table)({});
    __VLS_asFunctionalElement1(__VLS_intrinsics.thead, __VLS_intrinsics.thead)({});
    __VLS_asFunctionalElement1(__VLS_intrinsics.tr, __VLS_intrinsics.tr)({});
    __VLS_asFunctionalElement1(__VLS_intrinsics.th, __VLS_intrinsics.th)({});
    __VLS_asFunctionalElement1(__VLS_intrinsics.th, __VLS_intrinsics.th)({});
    __VLS_asFunctionalElement1(__VLS_intrinsics.th, __VLS_intrinsics.th)({});
    __VLS_asFunctionalElement1(__VLS_intrinsics.th, __VLS_intrinsics.th)({});
    __VLS_asFunctionalElement1(__VLS_intrinsics.th, __VLS_intrinsics.th)({});
    __VLS_asFunctionalElement1(__VLS_intrinsics.th, __VLS_intrinsics.th)({});
    __VLS_asFunctionalElement1(__VLS_intrinsics.tbody, __VLS_intrinsics.tbody)({});
    if (__VLS_ctx.marketStore.isLoadingHistory) {
        __VLS_asFunctionalElement1(__VLS_intrinsics.tr, __VLS_intrinsics.tr)({});
        __VLS_asFunctionalElement1(__VLS_intrinsics.td, __VLS_intrinsics.td)({
            colspan: "6",
            ...{ class: "empty-state" },
        });
        /** @type {__VLS_StyleScopedClasses['empty-state']} */ ;
    }
    else {
        for (const [trade] of __VLS_vFor((__VLS_ctx.marketStore.positionHistory))) {
            __VLS_asFunctionalElement1(__VLS_intrinsics.tr, __VLS_intrinsics.tr)({
                ...{ onClick: (...[$event]) => {
                        if (!(__VLS_ctx.activeTab === 'HISTORY'))
                            return;
                        if (!!(__VLS_ctx.marketStore.isLoadingHistory))
                            return;
                        __VLS_ctx.marketStore.setCurrentSymbol(trade.symbol);
                        // @ts-ignore
                        [activeTab, marketStore, marketStore, marketStore, sortedActivePositions,];
                    } },
                key: (trade.id),
            });
            __VLS_asFunctionalElement1(__VLS_intrinsics.td, __VLS_intrinsics.td)({
                ...{ class: "font-mono text-muted" },
            });
            /** @type {__VLS_StyleScopedClasses['font-mono']} */ ;
            /** @type {__VLS_StyleScopedClasses['text-muted']} */ ;
            (__VLS_ctx.formatDateTime(trade.time));
            __VLS_asFunctionalElement1(__VLS_intrinsics.td, __VLS_intrinsics.td)({});
            __VLS_asFunctionalElement1(__VLS_intrinsics.strong, __VLS_intrinsics.strong)({});
            (trade.symbol);
            __VLS_asFunctionalElement1(__VLS_intrinsics.td, __VLS_intrinsics.td)({});
            __VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({
                ...{ class: (trade.side === 'BUY' ? 'text-green' : 'text-red') },
            });
            (trade.side === 'BUY' ? '买入' : '卖出');
            __VLS_asFunctionalElement1(__VLS_intrinsics.td, __VLS_intrinsics.td)({
                ...{ class: "font-mono" },
            });
            /** @type {__VLS_StyleScopedClasses['font-mono']} */ ;
            (parseFloat(trade.price).toFixed(__VLS_ctx.getTickDecimals(trade.symbol)));
            __VLS_asFunctionalElement1(__VLS_intrinsics.td, __VLS_intrinsics.td)({
                ...{ class: "font-mono" },
            });
            /** @type {__VLS_StyleScopedClasses['font-mono']} */ ;
            (parseFloat(trade.qty));
            __VLS_asFunctionalElement1(__VLS_intrinsics.td, __VLS_intrinsics.td)({
                ...{ class: "font-mono" },
                ...{ class: (__VLS_ctx.getPnlClass(parseFloat(trade.realizedPnl))) },
            });
            /** @type {__VLS_StyleScopedClasses['font-mono']} */ ;
            (parseFloat(trade.realizedPnl) > 0 ? '+' : '');
            (parseFloat(trade.realizedPnl).toFixed(4));
            // @ts-ignore
            [formatDateTime, getTickDecimals, getPnlClass,];
        }
    }
}
// @ts-ignore
[];
const __VLS_export = (await import('vue')).defineComponent({});
export default {};

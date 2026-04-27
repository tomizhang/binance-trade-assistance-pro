import { ref, computed, onMounted, onUnmounted } from 'vue';
import { useMarketStore } from '@/store/market';
const marketStore = useMarketStore();
const statusClass = computed(() => {
    switch (marketStore.wsStatus) {
        case 'CONNECTED': return 'status-connected';
        case 'CONNECTING': return 'status-connecting';
        default: return 'status-disconnected';
    }
});
const statusText = computed(() => {
    switch (marketStore.wsStatus) {
        case 'CONNECTED': return '已连接';
        case 'CONNECTING': return '连接中';
        default: return '已断开';
    }
});
// ==========================================
// 🌟 真实网络延迟测量逻辑
// ==========================================
const frontToBackend = ref(0);
const frontToBinance = ref(0);
let pingTimer = null;
const measureLatency = async () => {
    if (marketStore.wsStatus !== 'CONNECTED')
        return;
    if (marketStore.dataSource === 'backend') {
        // 测算: 前端到本地 C# 中继的真实延迟 (极轻量级 HEAD 请求抓取纯网络 RTT)
        const start = Date.now();
        try {
            await fetch(`${import.meta.env.VITE_API_BASE_URL}/ping`, { method: 'GET' }).catch(() => { });
            frontToBackend.value = Date.now() - start;
        }
        catch (e) { }
    }
    else {
        // 测算: 前端直连币安的真实延迟
        const start = Date.now();
        try {
            await fetch('https://fapi.binance.com/fapi/v1/ping', { method: 'GET' });
            frontToBinance.value = Date.now() - start;
        }
        catch (e) { }
    }
};
const getLatencyColor = (ms) => {
    if (ms === 0)
        return 'level-good'; // 初始化
    if (ms < 50)
        return 'level-excellent';
    if (ms < 150)
        return 'level-good';
    return 'level-poor';
};
onMounted(() => {
    measureLatency();
    pingTimer = setInterval(measureLatency, 2000); // 每 2 秒测一次真实延迟
});
onUnmounted(() => {
    if (pingTimer)
        clearInterval(pingTimer);
});
const __VLS_ctx = {
    ...{},
    ...{},
};
let __VLS_components;
let __VLS_intrinsics;
let __VLS_directives;
/** @type {__VLS_StyleScopedClasses['nav-links']} */ ;
/** @type {__VLS_StyleScopedClasses['nav-links']} */ ;
/** @type {__VLS_StyleScopedClasses['nav-links']} */ ;
/** @type {__VLS_StyleScopedClasses['global-symbol']} */ ;
/** @type {__VLS_StyleScopedClasses['segment-item']} */ ;
/** @type {__VLS_StyleScopedClasses['segment-item']} */ ;
/** @type {__VLS_StyleScopedClasses['active']} */ ;
/** @type {__VLS_StyleScopedClasses['pulse-dot']} */ ;
/** @type {__VLS_StyleScopedClasses['pulse-dot']} */ ;
/** @type {__VLS_StyleScopedClasses['pulse-dot']} */ ;
/** @type {__VLS_StyleScopedClasses['status-connected']} */ ;
/** @type {__VLS_StyleScopedClasses['status-label']} */ ;
/** @type {__VLS_StyleScopedClasses['status-disconnected']} */ ;
/** @type {__VLS_StyleScopedClasses['status-label']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.header, __VLS_intrinsics.header)({
    ...{ class: "top-navbar" },
});
/** @type {__VLS_StyleScopedClasses['top-navbar']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ class: "nav-left" },
});
/** @type {__VLS_StyleScopedClasses['nav-left']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ class: "logo" },
});
/** @type {__VLS_StyleScopedClasses['logo']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({
    ...{ class: "logo-icon" },
});
/** @type {__VLS_StyleScopedClasses['logo-icon']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({
    ...{ class: "logo-text" },
});
/** @type {__VLS_StyleScopedClasses['logo-text']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ class: "nav-links" },
});
/** @type {__VLS_StyleScopedClasses['nav-links']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.a, __VLS_intrinsics.a)({
    href: "#",
    ...{ class: "active" },
});
/** @type {__VLS_StyleScopedClasses['active']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.a, __VLS_intrinsics.a)({
    href: "#",
});
__VLS_asFunctionalElement1(__VLS_intrinsics.a, __VLS_intrinsics.a)({
    href: "#",
});
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ class: "nav-right" },
});
/** @type {__VLS_StyleScopedClasses['nav-right']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ class: "global-symbol" },
    title: "全局联动标的",
});
/** @type {__VLS_StyleScopedClasses['global-symbol']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({});
(__VLS_ctx.marketStore.currentSymbol);
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ class: "route-switcher" },
});
/** @type {__VLS_StyleScopedClasses['route-switcher']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({
    ...{ class: "switcher-label" },
});
/** @type {__VLS_StyleScopedClasses['switcher-label']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ class: "segmented-control" },
});
/** @type {__VLS_StyleScopedClasses['segmented-control']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ onClick: (...[$event]) => {
            __VLS_ctx.marketStore.switchDataSource('backend');
            // @ts-ignore
            [marketStore, marketStore,];
        } },
    ...{ class: "segment-item" },
    ...{ class: ({ active: __VLS_ctx.marketStore.dataSource === 'backend' }) },
});
/** @type {__VLS_StyleScopedClasses['segment-item']} */ ;
/** @type {__VLS_StyleScopedClasses['active']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({
    ...{ class: "icon" },
});
/** @type {__VLS_StyleScopedClasses['icon']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ onClick: (...[$event]) => {
            __VLS_ctx.marketStore.switchDataSource('binance');
            // @ts-ignore
            [marketStore, marketStore,];
        } },
    ...{ class: "segment-item" },
    ...{ class: ({ active: __VLS_ctx.marketStore.dataSource === 'binance' }) },
});
/** @type {__VLS_StyleScopedClasses['segment-item']} */ ;
/** @type {__VLS_StyleScopedClasses['active']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({
    ...{ class: "icon" },
});
/** @type {__VLS_StyleScopedClasses['icon']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ class: "network-group" },
});
/** @type {__VLS_StyleScopedClasses['network-group']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ class: "ws-status" },
    ...{ class: (__VLS_ctx.statusClass) },
    title: (__VLS_ctx.statusText),
});
/** @type {__VLS_StyleScopedClasses['ws-status']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({
    ...{ class: "pulse-dot" },
});
/** @type {__VLS_StyleScopedClasses['pulse-dot']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({
    ...{ class: "status-label" },
});
/** @type {__VLS_StyleScopedClasses['status-label']} */ ;
(__VLS_ctx.statusText);
if (__VLS_ctx.marketStore.wsStatus === 'CONNECTED') {
    __VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
        ...{ class: "latency-indicator" },
    });
    /** @type {__VLS_StyleScopedClasses['latency-indicator']} */ ;
    __VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({
        ...{ class: "signal-icon" },
    });
    /** @type {__VLS_StyleScopedClasses['signal-icon']} */ ;
    if (__VLS_ctx.marketStore.dataSource === 'backend') {
        __VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
            ...{ class: "latency-detail" },
        });
        /** @type {__VLS_StyleScopedClasses['latency-detail']} */ ;
        __VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({
            ...{ class: "part" },
            title: "前端到中继延迟",
            ...{ class: (__VLS_ctx.getLatencyColor(__VLS_ctx.frontToBackend)) },
        });
        /** @type {__VLS_StyleScopedClasses['part']} */ ;
        (__VLS_ctx.frontToBackend);
        __VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({
            ...{ class: "divider" },
        });
        /** @type {__VLS_StyleScopedClasses['divider']} */ ;
        __VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({
            ...{ class: "part" },
            title: "中继到币安延迟",
            ...{ class: (__VLS_ctx.getLatencyColor(__VLS_ctx.marketStore.backendLatency)) },
        });
        /** @type {__VLS_StyleScopedClasses['part']} */ ;
        (__VLS_ctx.marketStore.backendLatency);
    }
    else {
        __VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({
            ...{ class: "latency-text" },
            ...{ class: (__VLS_ctx.getLatencyColor(__VLS_ctx.frontToBinance)) },
        });
        /** @type {__VLS_StyleScopedClasses['latency-text']} */ ;
        (__VLS_ctx.frontToBinance);
    }
}
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ class: "user-profile" },
});
/** @type {__VLS_StyleScopedClasses['user-profile']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ class: "avatar" },
});
/** @type {__VLS_StyleScopedClasses['avatar']} */ ;
// @ts-ignore
[marketStore, marketStore, marketStore, marketStore, marketStore, statusClass, statusText, statusText, getLatencyColor, getLatencyColor, getLatencyColor, frontToBackend, frontToBackend, frontToBinance, frontToBinance,];
const __VLS_export = (await import('vue')).defineComponent({});
export default {};

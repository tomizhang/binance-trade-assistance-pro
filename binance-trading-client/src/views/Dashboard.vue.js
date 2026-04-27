import { ref, nextTick } from 'vue';
import { GridLayout, GridItem } from 'grid-layout-plus';
import KlineModule from '@/modules/KlineModule.vue';
import OrderModule from '@/modules/OrderModule.vue';
import PositionModule from '@/modules/PositionModule.vue';
import MarketListModule from '@/modules/MarketListModule.vue';
import FourierModule from '@/modules/FourierModule.vue';
const isSidebarVisible = ref(true);
const triggerGridResize = () => {
    nextTick(() => {
        setTimeout(() => {
            window.dispatchEvent(new Event('resize'));
        }, 150);
    });
};
const handleCollapse = () => {
    isSidebarVisible.value = false;
    triggerGridResize();
};
const handleExpand = () => {
    isSidebarVisible.value = true;
    triggerGridResize();
};
const getComponentByType = (type) => {
    switch (type) {
        case 'kline': return KlineModule;
        case 'order': return OrderModule;
        case 'position': return PositionModule;
        case 'fourier': return FourierModule;
        default: return 'div';
    }
};
// 🌟 核心修复：这里必须是 LayoutItem[] 数组类型，少个 [] 会导致 TS 疯狂报错！
const layout = ref([
    { x: 16, y: 0, w: 8, h: 14, i: 'order-panel', type: 'order', title: '下单面板' },
    { x: 0, y: 14, w: 24, h: 8, i: 'position-panel', type: 'position', title: '仓位与挂单' }
]);
const addKlinePanel = (symbol) => {
    const newId = `kline-${symbol}-${Date.now()}`;
    layout.value.push({ x: 0, y: 0, w: 12, h: 12, i: newId, type: 'kline', symbol: symbol, title: `${symbol} 永续` });
};
const removePanel = (id) => {
    layout.value = layout.value.filter(item => item.i !== id);
};
const updateGridWidth = (id, newWidth) => {
    const index = layout.value.findIndex(item => item.i === id);
    if (index !== -1) {
        layout.value[index].w = newWidth;
        layout.value = [...layout.value];
        triggerGridResize();
    }
};
// 面板克隆逻辑 (点击面板头部复制时触发)
const duplicatePanel = (id) => {
    const targetItem = layout.value.find(item => item.i === id);
    if (targetItem) {
        const clonedItem = {
            ...targetItem,
            i: `${targetItem.type}-clone-${Date.now()}`,
            y: 999, // 让网格自动将其放置在最底部
        };
        layout.value.push(clonedItem);
        triggerGridResize();
    }
};
const __VLS_ctx = {
    ...{},
    ...{},
};
let __VLS_components;
let __VLS_intrinsics;
let __VLS_directives;
/** @type {__VLS_StyleScopedClasses['expand-sidebar-btn']} */ ;
/** @type {__VLS_StyleScopedClasses['action-icon-btn']} */ ;
/** @type {__VLS_StyleScopedClasses['action-icon-btn']} */ ;
/** @type {__VLS_StyleScopedClasses['close-btn']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ class: "trading-dashboard" },
});
/** @type {__VLS_StyleScopedClasses['trading-dashboard']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.aside, __VLS_intrinsics.aside)({
    ...{ class: "sidebar-left" },
});
__VLS_asFunctionalDirective(__VLS_directives.vShow, {})(null, { ...__VLS_directiveBindingRestFields, value: (__VLS_ctx.isSidebarVisible) }, null, null);
/** @type {__VLS_StyleScopedClasses['sidebar-left']} */ ;
const __VLS_0 = MarketListModule;
// @ts-ignore
const __VLS_1 = __VLS_asFunctionalComponent1(__VLS_0, new __VLS_0({
    ...{ 'onAddKline': {} },
    ...{ 'onCollapse': {} },
}));
const __VLS_2 = __VLS_1({
    ...{ 'onAddKline': {} },
    ...{ 'onCollapse': {} },
}, ...__VLS_functionalComponentArgsRest(__VLS_1));
let __VLS_5;
const __VLS_6 = ({ addKline: {} },
    { onAddKline: (__VLS_ctx.addKlinePanel) });
const __VLS_7 = ({ collapse: {} },
    { onCollapse: (__VLS_ctx.handleCollapse) });
var __VLS_3;
var __VLS_4;
__VLS_asFunctionalElement1(__VLS_intrinsics.main, __VLS_intrinsics.main)({
    ...{ class: "grid-workspace" },
});
/** @type {__VLS_StyleScopedClasses['grid-workspace']} */ ;
if (!__VLS_ctx.isSidebarVisible) {
    __VLS_asFunctionalElement1(__VLS_intrinsics.button, __VLS_intrinsics.button)({
        ...{ onClick: (__VLS_ctx.handleExpand) },
        ...{ class: "expand-sidebar-btn" },
        title: "展开行情列表",
    });
    /** @type {__VLS_StyleScopedClasses['expand-sidebar-btn']} */ ;
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
}
let __VLS_8;
/** @ts-ignore @type {typeof __VLS_components.GridLayout | typeof __VLS_components.GridLayout} */
GridLayout;
// @ts-ignore
const __VLS_9 = __VLS_asFunctionalComponent1(__VLS_8, new __VLS_8({
    layout: (__VLS_ctx.layout),
    colNum: (24),
    rowHeight: (30),
    isDraggable: (true),
    isResizable: (true),
    verticalCompact: (true),
    useCssTransforms: (true),
    dragAllowFrom: ".drag-handle, .panel-header",
    dragIgnoreFrom: ".no-drag, .custom-slider, button, input, select, textarea, .chart-wrapper",
}));
const __VLS_10 = __VLS_9({
    layout: (__VLS_ctx.layout),
    colNum: (24),
    rowHeight: (30),
    isDraggable: (true),
    isResizable: (true),
    verticalCompact: (true),
    useCssTransforms: (true),
    dragAllowFrom: ".drag-handle, .panel-header",
    dragIgnoreFrom: ".no-drag, .custom-slider, button, input, select, textarea, .chart-wrapper",
}, ...__VLS_functionalComponentArgsRest(__VLS_9));
const { default: __VLS_13 } = __VLS_11.slots;
for (const [item] of __VLS_vFor((__VLS_ctx.layout))) {
    let __VLS_14;
    /** @ts-ignore @type {typeof __VLS_components.GridItem | typeof __VLS_components.GridItem} */
    GridItem;
    // @ts-ignore
    const __VLS_15 = __VLS_asFunctionalComponent1(__VLS_14, new __VLS_14({
        key: (item.i),
        x: (item.x),
        y: (item.y),
        w: (item.w),
        h: (item.h),
        i: (item.i),
    }));
    const __VLS_16 = __VLS_15({
        key: (item.i),
        x: (item.x),
        y: (item.y),
        w: (item.w),
        h: (item.h),
        i: (item.i),
    }, ...__VLS_functionalComponentArgsRest(__VLS_15));
    const { default: __VLS_19 } = __VLS_17.slots;
    __VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
        ...{ class: "panel-container" },
    });
    /** @type {__VLS_StyleScopedClasses['panel-container']} */ ;
    __VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
        ...{ class: "panel-header drag-handle" },
    });
    /** @type {__VLS_StyleScopedClasses['panel-header']} */ ;
    /** @type {__VLS_StyleScopedClasses['drag-handle']} */ ;
    __VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({
        ...{ class: "panel-title" },
    });
    /** @type {__VLS_StyleScopedClasses['panel-title']} */ ;
    (item.title);
    __VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
        ...{ class: "panel-actions" },
    });
    /** @type {__VLS_StyleScopedClasses['panel-actions']} */ ;
    __VLS_asFunctionalElement1(__VLS_intrinsics.button, __VLS_intrinsics.button)({
        ...{ onClick: (...[$event]) => {
                __VLS_ctx.duplicatePanel(item.i);
                // @ts-ignore
                [isSidebarVisible, isSidebarVisible, addKlinePanel, handleCollapse, handleExpand, layout, layout, duplicatePanel,];
            } },
        ...{ class: "action-icon-btn" },
        title: "复制面板",
    });
    /** @type {__VLS_StyleScopedClasses['action-icon-btn']} */ ;
    __VLS_asFunctionalElement1(__VLS_intrinsics.button, __VLS_intrinsics.button)({
        ...{ onClick: (...[$event]) => {
                __VLS_ctx.updateGridWidth(item.i, 12);
                // @ts-ignore
                [updateGridWidth,];
            } },
        ...{ class: "action-icon-btn" },
        ...{ class: ({ active: item.w === 12 }) },
        title: "50% 宽度",
    });
    /** @type {__VLS_StyleScopedClasses['action-icon-btn']} */ ;
    /** @type {__VLS_StyleScopedClasses['active']} */ ;
    __VLS_asFunctionalElement1(__VLS_intrinsics.button, __VLS_intrinsics.button)({
        ...{ onClick: (...[$event]) => {
                __VLS_ctx.updateGridWidth(item.i, 24);
                // @ts-ignore
                [updateGridWidth,];
            } },
        ...{ class: "action-icon-btn" },
        ...{ class: ({ active: item.w === 24 }) },
        title: "100% 宽度",
    });
    /** @type {__VLS_StyleScopedClasses['action-icon-btn']} */ ;
    /** @type {__VLS_StyleScopedClasses['active']} */ ;
    __VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({
        ...{ class: "action-divider" },
    });
    /** @type {__VLS_StyleScopedClasses['action-divider']} */ ;
    __VLS_asFunctionalElement1(__VLS_intrinsics.button, __VLS_intrinsics.button)({
        ...{ onClick: (...[$event]) => {
                __VLS_ctx.removePanel(item.i);
                // @ts-ignore
                [removePanel,];
            } },
        ...{ class: "close-btn" },
        title: "关闭面板",
    });
    /** @type {__VLS_StyleScopedClasses['close-btn']} */ ;
    __VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
        ...{ onMousedown: () => { } },
        ...{ onTouchstart: () => { } },
        ...{ onPointerdown: () => { } },
        ...{ class: "panel-content no-drag" },
    });
    /** @type {__VLS_StyleScopedClasses['panel-content']} */ ;
    /** @type {__VLS_StyleScopedClasses['no-drag']} */ ;
    const __VLS_20 = (__VLS_ctx.getComponentByType(item.type));
    // @ts-ignore
    const __VLS_21 = __VLS_asFunctionalComponent1(__VLS_20, new __VLS_20({
        ...{ 'onDuplicate': {} },
        symbol: (item.symbol),
    }));
    const __VLS_22 = __VLS_21({
        ...{ 'onDuplicate': {} },
        symbol: (item.symbol),
    }, ...__VLS_functionalComponentArgsRest(__VLS_21));
    let __VLS_25;
    const __VLS_26 = ({ duplicate: {} },
        { onDuplicate: (__VLS_ctx.addKlinePanel) });
    var __VLS_23;
    var __VLS_24;
    // @ts-ignore
    [addKlinePanel, getComponentByType,];
    var __VLS_17;
    // @ts-ignore
    [];
}
// @ts-ignore
[];
var __VLS_11;
// @ts-ignore
[];
const __VLS_export = (await import('vue')).defineComponent({});
export default {};

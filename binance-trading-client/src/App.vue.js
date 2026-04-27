import Dashboard from '@/views/Dashboard.vue';
import ToastContainer from '@/components/ToastContainer.vue';
import { onMounted } from 'vue';
import { useMarketStore } from '@/store/market';
import TopNavBar from '@/components/TopNavBar.vue';
// 如果别名 @ 报错，请使用相对路径: import Dashboard from './views/Dashboard.vue'
const marketStore = useMarketStore();
onMounted(() => {
    marketStore.fetchExchangeInfo();
    marketStore.connectUserDataStream(); // 🌟 连接私有账户数据流
});
const __VLS_ctx = {};
let __VLS_components;
let __VLS_intrinsics;
let __VLS_directives;
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    id: "app",
    ...{ class: "app-container" },
});
/** @type {__VLS_StyleScopedClasses['app-container']} */ ;
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ class: "main-content" },
});
/** @type {__VLS_StyleScopedClasses['main-content']} */ ;
const __VLS_0 = TopNavBar;
// @ts-ignore
const __VLS_1 = __VLS_asFunctionalComponent1(__VLS_0, new __VLS_0({}));
const __VLS_2 = __VLS_1({}, ...__VLS_functionalComponentArgsRest(__VLS_1));
const __VLS_5 = Dashboard;
// @ts-ignore
const __VLS_6 = __VLS_asFunctionalComponent1(__VLS_5, new __VLS_5({}));
const __VLS_7 = __VLS_6({}, ...__VLS_functionalComponentArgsRest(__VLS_6));
const __VLS_10 = ToastContainer;
// @ts-ignore
const __VLS_11 = __VLS_asFunctionalComponent1(__VLS_10, new __VLS_10({}));
const __VLS_12 = __VLS_11({}, ...__VLS_functionalComponentArgsRest(__VLS_11));
const __VLS_export = (await import('vue')).defineComponent({});
export default {};

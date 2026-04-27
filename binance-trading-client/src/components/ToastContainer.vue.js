import { useToast } from '@/utils/useToast';
const { toasts } = useToast();
const __VLS_ctx = {
    ...{},
    ...{},
};
let __VLS_components;
let __VLS_intrinsics;
let __VLS_directives;
__VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
    ...{ class: "toast-container" },
});
/** @type {__VLS_StyleScopedClasses['toast-container']} */ ;
let __VLS_0;
/** @ts-ignore @type {typeof __VLS_components.TransitionGroup | typeof __VLS_components.TransitionGroup} */
TransitionGroup;
// @ts-ignore
const __VLS_1 = __VLS_asFunctionalComponent1(__VLS_0, new __VLS_0({
    name: "toast-list",
}));
const __VLS_2 = __VLS_1({
    name: "toast-list",
}, ...__VLS_functionalComponentArgsRest(__VLS_1));
const { default: __VLS_5 } = __VLS_3.slots;
for (const [toast] of __VLS_vFor((__VLS_ctx.toasts))) {
    __VLS_asFunctionalElement1(__VLS_intrinsics.div, __VLS_intrinsics.div)({
        key: (toast.id),
        ...{ class: "toast-item" },
        ...{ class: (`toast-${toast.type}`) },
    });
    /** @type {__VLS_StyleScopedClasses['toast-item']} */ ;
    __VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({
        ...{ class: "toast-icon" },
    });
    /** @type {__VLS_StyleScopedClasses['toast-icon']} */ ;
    if (toast.type === 'success') {
    }
    else if (toast.type === 'error') {
    }
    else if (toast.type === 'warning') {
    }
    else {
    }
    __VLS_asFunctionalElement1(__VLS_intrinsics.span, __VLS_intrinsics.span)({
        ...{ class: "toast-message" },
    });
    /** @type {__VLS_StyleScopedClasses['toast-message']} */ ;
    (toast.message);
    // @ts-ignore
    [toasts,];
}
// @ts-ignore
[];
var __VLS_3;
// @ts-ignore
[];
const __VLS_export = (await import('vue')).defineComponent({});
export default {};

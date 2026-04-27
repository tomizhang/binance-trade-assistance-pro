import { ref } from 'vue';
// 全局响应式状态，保存当前屏幕上的所有提示
const toasts = ref([]);
export const useToast = () => {
    const show = (message, type = 'info', duration = 3000) => {
        const id = Math.random().toString(36).substring(2, 9);
        toasts.value.push({ id, message, type });
        // 定时自动移除
        setTimeout(() => {
            remove(id);
        }, duration);
    };
    const remove = (id) => {
        const index = toasts.value.findIndex(t => t.id === id);
        if (index > -1) {
            toasts.value.splice(index, 1);
        }
    };
    // 快捷方法
    const success = (msg, duration) => show(msg, 'success', duration);
    const error = (msg, duration) => show(msg, 'error', duration);
    const warning = (msg, duration) => show(msg, 'warning', duration);
    const info = (msg, duration) => show(msg, 'info', duration);
    return {
        toasts,
        show,
        remove,
        success,
        error,
        warning,
        info
    };
};

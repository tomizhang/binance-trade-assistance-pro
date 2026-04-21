import { ref } from 'vue';

export type ToastType = 'success' | 'error' | 'info' | 'warning';

export interface ToastMessage {
  id: string;
  message: string;
  type: ToastType;
}

// 全局响应式状态，保存当前屏幕上的所有提示
const toasts = ref<ToastMessage[]>([]);

export const useToast = () => {
  const show = (message: string, type: ToastType = 'info', duration: number = 3000) => {
    const id = Math.random().toString(36).substring(2, 9);
    toasts.value.push({ id, message, type });

    // 定时自动移除
    setTimeout(() => {
      remove(id);
    }, duration);
  };

  const remove = (id: string) => {
    const index = toasts.value.findIndex(t => t.id === id);
    if (index > -1) {
      toasts.value.splice(index, 1);
    }
  };

  // 快捷方法
  const success = (msg: string, duration?: number) => show(msg, 'success', duration);
  const error = (msg: string, duration?: number) => show(msg, 'error', duration);
  const warning = (msg: string, duration?: number) => show(msg, 'warning', duration);
  const info = (msg: string, duration?: number) => show(msg, 'info', duration);

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
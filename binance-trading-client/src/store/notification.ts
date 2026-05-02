import { defineStore } from 'pinia';
import { ref, computed } from 'vue';

export interface AlertMessage {
  id: string;
  type: 'HA_REVERSAL' | 'STRATEGY_ALERT' | 'SYSTEM';
  title: string;
  content: string;
  timestamp: number;
  isRead: boolean;
  symbol?: string; // 关联的币种
}

export const useNotificationStore = defineStore('notification', () => {
  const isSidebarOpen = ref(false);
  const alerts = ref<AlertMessage[]>([]);

  // 未读消息数量
  const unreadCount = computed(() => alerts.value.filter(a => !a.isRead).length);

  const toggleSidebar = (force?: boolean) => {
    isSidebarOpen.value = force !== undefined ? force : !isSidebarOpen.value;
    if (isSidebarOpen.value) {
      markAllAsRead();
    }
  };

  const addAlert = (alert: Omit<AlertMessage, 'id' | 'isRead'>) => {
    alerts.value.unshift({
      ...alert,
      id: `${Date.now()}_${Math.random().toString(36).substring(2, 9)}`,
      isRead: isSidebarOpen.value // 如果抽屉开着，进来的消息直接算已读
    });

    // 限制最大存储量，防止内存溢出 (比如最多存 100 条)
    if (alerts.value.length > 100) {
      alerts.value.pop();
    }
  };

  const markAllAsRead = () => {
    alerts.value.forEach(a => a.isRead = true);
  };

  const clearAll = () => {
    alerts.value = [];
  };

  return {
    isSidebarOpen,
    alerts,
    unreadCount,
    toggleSidebar,
    addAlert,
    markAllAsRead,
    clearAll
  };
});
import { defineStore } from 'pinia';
import { ref } from 'vue';

export const useLayoutStore = defineStore('layout', () => {
  const currentTab = ref<'dashboard' | 'backtest' | 'assets'>('dashboard');

  const setTab = (tab: 'dashboard' | 'backtest' | 'assets') => {
    currentTab.value = tab;
  };

  return {
    currentTab,
    setTab
  };
});

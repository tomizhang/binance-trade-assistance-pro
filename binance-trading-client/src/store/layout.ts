import { defineStore } from 'pinia';
import { ref } from 'vue';

export const useLayoutStore = defineStore('layout', () => {
  const currentTab = ref<'workbench' | 'dashboard' | 'backtest' | 'assets'>('workbench');

  const setTab = (tab: 'workbench' | 'dashboard' | 'backtest' | 'assets') => {
    currentTab.value = tab;
  };

  return {
    currentTab,
    setTab
  };
});


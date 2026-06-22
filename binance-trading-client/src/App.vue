<template>
  <div id="app" class="app-container">
    <div class="main-content">
      <TopNavBar /> 
      <Workbench v-if="layoutStore.currentTab === 'workbench'" />
      <Dashboard v-show="layoutStore.currentTab === 'dashboard'" />
      <BacktestView v-if="layoutStore.currentTab === 'backtest'" />
      <div v-else-if="layoutStore.currentTab === 'assets'" class="coming-soon">
        <h2>资产管理 (建设中...)</h2>
      </div>
      <ToastContainer />
    </div>
  </div>
</template>
<script setup lang="ts">
import Workbench from '@/views/Workbench.vue'
import Dashboard from '@/views/Dashboard.vue'
import BacktestView from '@/views/BacktestView.vue'
import ToastContainer from '@/components/ToastContainer.vue';
import { watch } from 'vue';
import { useMarketStore } from '@/store/market';
import { useLayoutStore } from '@/store/layout';
import TopNavBar from '@/components/TopNavBar.vue';

const marketStore = useMarketStore();
const layoutStore = useLayoutStore();

watch(() => layoutStore.currentTab, (newTab) => {
  if (newTab === 'dashboard') {
    marketStore.fetchExchangeInfo();
    marketStore.connectUserDataStream();
  }
}, { immediate: true });
</script>

<style>
/* 全局基础样式重置，确保暗黑主题充满全屏 */
html, body, #app {
  margin: 0;
  padding: 0;
  width: 100vw;
  height: 100vh;
  background-color: #0d1117;
  color: #c9d1d9;
  font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif;
  overflow: hidden; /* 防止出现全局滚动条 */
}

/* 隐藏所有组件中可能出现的原生滚动条，保持UI整洁 (按需开启) */
::-webkit-scrollbar {
  width: 6px;
  height: 6px;
}
::-webkit-scrollbar-thumb {
  background: #30363d;
  border-radius: 3px;
}
/* 确保整个应用是垂直的 Flex 布局，占满屏幕 */
.app-container {
  display: flex;
  flex-direction: column;
  height: 100vh;
  width: 100vw;
  background-color: #010409; /* 极暗背景 */
  overflow: hidden;
}

.main-content {
  flex: 1;
  position: relative;
  /* 如果你的 grid-layout 需要滚动或自适应，可以在这里设置 */
}

.coming-soon {
  display: flex;
  justify-content: center;
  align-items: center;
  height: calc(100vh - 50px);
  color: #8b949e;
  font-size: 20px;
}
</style>
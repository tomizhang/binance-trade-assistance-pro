<template>
  <div id="app">
    
    <Dashboard />
    <ToastContainer />
  </div>
</template>
<script setup lang="ts">
// 直接引入我们之前写好的主工作台
import Dashboard from '@/views/Dashboard.vue'
import ToastContainer from '@/components/ToastContainer.vue';
import { onMounted } from 'vue';
import { useMarketStore } from '@/store/market';
// 如果别名 @ 报错，请使用相对路径: import Dashboard from './views/Dashboard.vue'
const marketStore = useMarketStore();
onMounted(()=>{
  marketStore.fetchExchangeInfo()
});
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
</style>
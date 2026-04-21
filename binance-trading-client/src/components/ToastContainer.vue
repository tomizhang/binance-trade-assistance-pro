<template>
  <div class="toast-container">
    <TransitionGroup name="toast-list">
      <div 
        v-for="toast in toasts" 
        :key="toast.id" 
        class="toast-item"
        :class="`toast-${toast.type}`"
      >
        <span class="toast-icon">
          <template v-if="toast.type === 'success'">✅</template>
          <template v-else-if="toast.type === 'error'">❌</template>
          <template v-else-if="toast.type === 'warning'">⚠️</template>
          <template v-else>ℹ️</template>
        </span>
        <span class="toast-message">{{ toast.message }}</span>
      </div>
    </TransitionGroup>
  </div>
</template>

<script setup lang="ts">
import { useToast } from '@/utils/useToast';

const { toasts } = useToast();
</script>

<style scoped>
.toast-container {
  position: fixed;
  top: 20px;
  right: 20px;
  z-index: 9999; /* 确保它永远在最上层 */
  display: flex;
  flex-direction: column;
  gap: 10px;
  pointer-events: none; /* 让鼠标事件穿透底部的空隙 */
}

.toast-item {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 12px 20px;
  border-radius: 6px;
  background: #161b22;
  color: #e6edf3;
  font-size: 13px;
  font-weight: bold;
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.5);
  border-left: 4px solid transparent;
  pointer-events: auto; /* 恢复自身元素的鼠标事件 */
}

.toast-success { border-left-color: #2ea043; }
.toast-error { border-left-color: #f85149; }
.toast-warning { border-left-color: #d29922; }
.toast-info { border-left-color: #58a6ff; }

/* Vue TransitionGroup 动画 */
.toast-list-enter-active,
.toast-list-leave-active {
  transition: all 0.3s ease;
}
.toast-list-enter-from {
  opacity: 0;
  transform: translateX(100%); /* 从右侧滑入 */
}
.toast-list-leave-to {
  opacity: 0;
  transform: translateY(-20px); /* 向上飘走淡出 */
}
</style>
<template>
  <div class="position-module">
    <div class="tabs">
      <span class="active">当前仓位 (0)</span>
      <span>当前挂单 (0)</span>
      <span>历史成交</span>
    </div>
    <div class="position-table">
      <table>
        <thead>
          <tr>
            <th>合约</th>
            <th>数量</th>
            <th>开仓均价</th>
            <th>未实现盈亏</th>
            <th>操作</th>
          </tr>
        </thead>
        <tbody>
          <tr><td colspan="5" style="text-align:center; padding: 20px; color: #8b949e;">暂无仓位</td></tr>
        </tbody>
      </table>
    </div>
  </div>
</template>

<script setup lang="ts">
// 后续将通过 SignalR 监听后端推送的账户事件来更新此处列表
</script>

<style scoped>
.position-module { display: flex; flex-direction: column; height: 100%; }
.tabs { display: flex; gap: 15px; padding: 10px; border-bottom: 1px solid #30363d; }
.tabs span { cursor: pointer; color: #8b949e; }
.tabs .active { color: #fff; border-bottom: 2px solid #58a6ff; padding-bottom: 5px;}
.position-table table { width: 100%; border-collapse: collapse; text-align: left; font-size: 13px;}
.position-table th { padding: 10px; background: #21262d; color: #8b949e; }
.position-table td { padding: 10px; border-bottom: 1px solid #30363d; }
</style>
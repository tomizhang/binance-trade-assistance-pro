<template>
  <div class="market-list-module">
    <div class="module-header">
      <div class="search-box">
        <svg viewBox="0 0 24 24" width="14" height="14" stroke="currentColor" stroke-width="2" fill="none"><circle cx="11" cy="11" r="8"></circle><line x1="21" y1="21" x2="16.65" y2="16.65"></line></svg>
        <input v-model="searchQuery" type="text" placeholder="搜索 (如 BTC)" />
      </div>
      
      <div class="header-actions">
        <button class="icon-btn collapse-btn" @click="collapseSidebar" title="收起侧边栏">
          <svg viewBox="0 0 24 24" width="16" height="16" stroke="currentColor" stroke-width="2" fill="none"><rect x="3" y="3" width="18" height="18" rx="2" ry="2"></rect><line x1="9" y1="3" x2="9" y2="21"></line></svg>
        </button>
      </div>
    </div>

    <div class="list-header">
      <div class="col-main sortable" @click="sortBy('volume')">
        <span>合约/成交额</span>
        <span class="sort-icon" :class="{ active: sortKey === 'volume' }">
          {{ sortKey === 'volume' && sortOrder === 1 ? '↑' : '↓' }}
        </span>
      </div>
      <div class="col-price sortable" @click="sortBy('lastPrice')">
        <span>价格/资金费</span>
        <span class="sort-icon" :class="{ active: sortKey === 'lastPrice' }">
          {{ sortKey === 'lastPrice' && sortOrder === 1 ? '↑' : '↓' }}
        </span>
      </div>
      <div class="col-change sortable" @click="sortBy('priceChangePercent')">
        <span>涨跌幅</span>
        <span class="sort-icon" :class="{ active: sortKey === 'priceChangePercent' }">
          {{ sortKey === 'priceChangePercent' && sortOrder === 1 ? '↑' : '↓' }}
        </span>
      </div>
    </div>

    <div class="list-content">
      <div 
        class="list-item" 
        v-for="item in filteredAndSortedList" 
        :key="item.symbol"
        @dblclick="handleAddKline(item.symbol)"
      >
        <div class="col-main">
          <span class="symbol-name">{{ item.symbol.replace('USDT', '') }}<span class="quote">USDT</span></span>
          <span class="sub-text">{{ formatVolume(item.volume) }}</span>
        </div>
        
        <div class="col-price">
          <span class="price-text" :class="getColorClass(item.priceChangePercent)">
            {{ item.lastPrice ? item.lastPrice.toFixed(getPrecision(item.lastPrice)) : '--' }}
          </span>
          <span class="sub-text funding" title="资金费率">
            {{ item.fundingRate !== undefined ? item.fundingRate.toFixed(4) + '%' : '--' }}
          </span>
        </div>
        
        <div class="col-change">
          <span class="change-text" :class="getColorClass(item.priceChangePercent)">
            {{ item.priceChangePercent > 0 ? '+' : '' }}{{ item.priceChangePercent ? item.priceChangePercent.toFixed(2) : '0.00' }}%
          </span>
        </div>
      </div>
      
      <div v-if="filteredAndSortedList.length === 0" class="empty-state">
        <p>暂无数据</p>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue';
import { useMarketStore } from '@/store/market';

const emit = defineEmits(['add-kline', 'collapse']);
const marketStore = useMarketStore();

const searchQuery = ref('');
const sortKey = ref('volume'); 
const sortOrder = ref(-1); // -1 是降序 (从大到小), 1 是升序

const collapseSidebar = () => {
  emit('collapse');
};

const handleAddKline = (symbol: string) => {
  emit('add-kline', symbol);
};

// 点击排序逻辑
const sortBy = (key: string) => {
  if (sortKey.value === key) {
    sortOrder.value = sortOrder.value === 1 ? -1 : 1; 
  } else {
    sortKey.value = key;
    sortOrder.value = -1; // 切换新维度时，默认显示最大的（降序）
  }
};

// 🌟 修复 1：安全的排序算法，防止 NaN 破坏排序
const filteredAndSortedList = computed(() => {
  let list = Object.keys(marketStore.marketTickers).map(symbol => ({
    symbol,
    ...marketStore.marketTickers[symbol]
  }));

  if (searchQuery.value) {
    const q = searchQuery.value.toUpperCase();
    list = list.filter(item => item.symbol.includes(q));
  }

  list.sort((a, b) => {
    // 强制转换为数字，如果遇到 undefined 或非数字，当做 0 处理
    const valA = Number(a[sortKey.value]) || 0;
    const valB = Number(b[sortKey.value]) || 0;
    
    // sortOrder === 1 为升序 (小在前)，-1 为降序 (大在前)
    return sortOrder.value === 1 ? (valA - valB) : (valB - valA);
  });

  return list;
});

const getPrecision = (price: number) => {
  if (price >= 1000) return 1;
  if (price >= 1) return 3;
  if (price >= 0.01) return 4;
  return 6;
};

const formatVolume = (vol: number) => {
  if (!vol) return '0.00';
  if (vol >= 1e9) return (vol / 1e9).toFixed(2) + 'B';
  if (vol >= 1e6) return (vol / 1e6).toFixed(2) + 'M';
  if (vol >= 1e3) return (vol / 1e3).toFixed(2) + 'K';
  return vol.toFixed(0);
};

const getColorClass = (change: number) => {
  if (!change) return '';
  return change >= 0 ? 'text-green' : 'text-red';
};

onMounted(() => {
  marketStore.connectAllTickers();
});
</script>

<style scoped>
* { box-sizing: border-box; }
.market-list-module { display: flex; flex-direction: column; height: 100%; width: 100%; background-color: #0d1117; color: #c9d1d9; font-size: 13px; overflow: hidden; }
.module-header { display: flex; align-items: center; padding: 8px 10px; gap: 8px; background-color: #161b22; border-bottom: 1px solid #30363d; }
.search-box { flex: 1; display: flex; align-items: center; background: #0d1117; border: 1px solid #30363d; border-radius: 4px; padding: 0 8px; transition: border-color 0.2s; }
.search-box:focus-within { border-color: #58a6ff; }
.search-box svg { color: #8b949e; margin-right: 4px; }
.search-box input { flex: 1; width: 100%; background: transparent; border: none; color: #c9d1d9; padding: 6px 0; outline: none; font-size: 12px; }
.header-actions { display: flex; gap: 4px; }
.icon-btn { display: flex; align-items: center; justify-content: center; width: 26px; height: 26px; background: transparent; border: 1px solid transparent; color: #8b949e; border-radius: 4px; cursor: pointer; transition: all 0.2s; }
.icon-btn:hover { background: #21262d; color: #c9d1d9; border-color: #30363d; }
.collapse-btn:hover { color: #58a6ff; }

/* 🌟 修复 2：针对表头强制重写 Flex 布局，使其水平对齐 */
.list-header { 
  display: flex; 
  padding: 6px 12px; 
  background: #0d1117; 
  color: #8b949e; 
  font-size: 11px; 
  border-bottom: 1px solid #21262d; 
}
/* 强制表头内部水平排布 */
.list-header .col-main { flex-direction: row; justify-content: flex-start; align-items: center; }
.list-header .col-price { flex-direction: row; justify-content: flex-end; align-items: center; }
.list-header .col-change { flex-direction: row; justify-content: flex-end; align-items: center; }

.sortable { cursor: pointer; user-select: none; transition: color 0.2s; }
.sortable:hover { color: #c9d1d9; }
.sort-icon { margin-left: 3px; font-size: 10px; color: transparent; transition: color 0.2s; } /* 默认透明，占据空间防抖 */
.sort-icon.active { color: #58a6ff; } /* 激活时亮蓝色 */

.list-content { flex: 1; overflow-y: auto; overflow-x: hidden; }
.list-content::-webkit-scrollbar { width: 4px; }
.list-content::-webkit-scrollbar-thumb { background: #30363d; border-radius: 2px; }
.list-item { display: flex; padding: 8px 12px; border-bottom: 1px solid #161b22; cursor: pointer; transition: background 0.1s; align-items: center; }
.list-item:hover { background: #1c2128; }

.col-main { flex: 0 0 40%; display: flex; flex-direction: column; overflow: hidden; }
.col-price { flex: 0 0 35%; display: flex; flex-direction: column; text-align: right; padding-right: 10px; justify-content: center; }
.col-change { flex: 0 0 25%; display: flex; justify-content: flex-end; align-items: center; }

.symbol-name { font-weight: 600; color: #e6edf3; font-size: 13px; }
.quote { font-size: 10px; color: #8b949e; margin-left: 2px; font-weight: normal; }
.price-text { font-family: 'Courier New', Courier, monospace; font-weight: bold; font-size: 13px; }
.sub-text { font-size: 11px; color: #8b949e; margin-top: 3px; font-family: 'Courier New', Courier, monospace; }
.funding { color: #d2a8ff; } 
.change-text { font-weight: bold; font-family: 'Courier New', Courier, monospace; font-size: 13px; text-align: right; width: 100%; }
.text-green { color: #2ea043; }
.text-red { color: #f85149; }
.empty-state { text-align: center; padding: 40px 20px; color: #8b949e; font-size: 13px; }
</style>
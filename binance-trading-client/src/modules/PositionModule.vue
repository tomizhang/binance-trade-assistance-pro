<template>
  <div class="position-module">
    <div class="module-header">
      <div class="tabs">
        <button :class="{ active: activeTab === 'ACTIVE' }" @click="activeTab = 'ACTIVE'">
          当前持仓 ({{ filteredPositions.length }})
        </button>
        <button :class="{ active: activeTab === 'HISTORY' }" @click="switchToHistory">
          历史记录
        </button>
      </div>
      <span class="balance-info">可用余额: {{ marketStore.usdtBalance.toFixed(2) }} USDT</span>
    </div>

    <div class="toolbar">
      <div class="filter-group">
        <input type="text" class="search-input" v-model="searchQuery" placeholder="🔍 搜索币种..." />
        <label class="checkbox-label" title="只显示当前K线图正在看的币种">
          <input type="checkbox" v-model="showCurrentOnly" />
          🎯 仅看当前
        </label>
      </div>
      <div class="control-group" v-if="activeTab === 'ACTIVE'">
        <button class="icon-btn" @click="toggleDisplayMode">
          ⇌ {{ displayMode === 'TOKEN' ? '代币数量' : 'USDT名义价值' }}
        </button>
      </div>
    </div>

    <div class="position-table">
      <table v-if="activeTab === 'ACTIVE'">
        <thead>
          <tr>
            <th @click="setSort('symbol')" class="sortable">合约 {{ getSortIcon('symbol') }}</th>
            <th @click="setSort('side')" class="sortable">方向/杠杆 {{ getSortIcon('side') }}</th>
            <th @click="setSort('amount')" class="sortable">
              持仓量({{ displayMode }}) {{ getSortIcon('amount') }}
            </th>
            <th @click="setSort('entryPrice')" class="sortable">开仓均价 {{ getSortIcon('entryPrice') }}</th>
            <th>最新价 <br/><span style="font-size:10px;color:#8b949e">预估资金费</span></th>
            <th @click="setSort('pnl')" class="sortable">未实现盈亏(ROE%) {{ getSortIcon('pnl') }}</th>
            <th>操作</th>
          </tr>
        </thead>
        <tbody>
          <tr 
            v-for="pos in sortedActivePositions" 
            :key="pos.symbol"
            @click="marketStore.setCurrentSymbol(pos.symbol)"
            :class="{ 'active-row': marketStore.currentSymbol === pos.symbol }"
          >
            <td>
              <div class="symbol-col">
                <strong>{{ pos.symbol }}</strong>
              </div>
            </td>
            <td>
              <div class="side-col">
                <span :class="pos.side === 'LONG' ? 'text-green' : 'text-red'" class="side-badge">
                  {{ pos.side === 'LONG' ? '做多' : '做空' }}
                </span>
                <span class="margin-badge">
                  {{ pos.marginType === 'cross' ? '全仓' : '逐仓' }} 
                  <span class="leverage-text">{{ pos.leverage ? pos.leverage + 'x' : '-x' }}</span>
                </span>
              </div>
            </td>
            <td class="font-mono">{{ getDisplayAmount(pos) }}</td>
            <td class="font-mono">{{ pos.entryPrice.toFixed(getTickDecimals(pos.symbol)) }}</td>
            <td class="font-mono pnl-col">
              <span :class="getPriceColor(pos.symbol)">
                {{ getCurrentPrice(pos.symbol).toFixed(getTickDecimals(pos.symbol)) }}
              </span>
              <span class="funding-fee" :class="{ 'text-red': getEstFundingFee(pos) < 0 }">
                费: {{ getEstFundingFee(pos) }} U
              </span>
            </td>
            <td class="font-mono pnl-col" :class="getPnlClass(getRealtimePnl(pos))">
              <span class="pnl-value">{{ getRealtimePnl(pos) > 0 ? '+' : '' }}{{ getRealtimePnl(pos).toFixed(2) }}</span>
              <span class="pnl-roe">({{ getRoe(pos) > 0 ? '+' : '' }}{{ getRoe(pos).toFixed(2) }}%)</span>
            </td>
            <td>
              <button class="btn-close" @click.stop="closePosition(pos)">市价平仓</button>
            </td>
          </tr>
          <tr v-if="sortedActivePositions.length === 0">
            <td colspan="7" class="empty-state">没有符合条件的持仓</td>
          </tr>
        </tbody>
      </table>

      <table v-if="activeTab === 'HISTORY'">
        <thead><tr><th>时间</th><th>合约</th><th>方向</th><th>成交价</th><th>成交量</th><th>已实现盈亏</th></tr></thead>
        <tbody>
          <tr v-if="marketStore.isLoadingHistory"><td colspan="6" class="empty-state">⏳ 正在加载...</td></tr>
          <tr v-else v-for="trade in marketStore.positionHistory" :key="trade.id" @click="marketStore.setCurrentSymbol(trade.symbol)">
            <td class="font-mono" style="color: #8b949e">{{ formatTime(trade.time) }}</td>
            <td><strong>{{ trade.symbol }}</strong></td>
            <td><span :class="trade.side === 'BUY' ? 'text-green' : 'text-red'">{{ trade.side === 'BUY' ? '买入' : '卖出' }}</span></td>
            <td class="font-mono">{{ parseFloat(trade.price).toFixed(getTickDecimals(trade.symbol)) }}</td>
            <td class="font-mono">{{ parseFloat(trade.qty) }}</td>
            <td class="font-mono" :class="getPnlClass(parseFloat(trade.realizedPnl))">
              {{ parseFloat(trade.realizedPnl) > 0 ? '+' : '' }}{{ parseFloat(trade.realizedPnl).toFixed(4) }}
            </td>
          </tr>
        </tbody>
      </table>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed } from 'vue';
import { useMarketStore } from '@/store/market';
import { useToast } from '@/utils/useToast';

const marketStore = useMarketStore();
const toast = useToast();

const activeTab = ref<'ACTIVE' | 'HISTORY'>('ACTIVE');
const displayMode = ref<'TOKEN' | 'USDT'>('TOKEN');
const toggleDisplayMode = () => { displayMode.value = displayMode.value === 'TOKEN' ? 'USDT' : 'TOKEN'; };

const searchQuery = ref('');
const showCurrentOnly = ref(false);

const switchToHistory = () => {
  activeTab.value = 'HISTORY';
  marketStore.fetchPositionHistory(showCurrentOnly.value ? marketStore.currentSymbol : undefined);
};

// ==========================================
// 🌟 核心引擎 1：排序与过滤机制
// ==========================================
const sortKey = ref('pnl'); // 默认按盈亏排序
const sortDesc = ref(true); // 默认降序

const setSort = (key: string) => {
  if (sortKey.value === key) {
    sortDesc.value = !sortDesc.value;
  } else {
    sortKey.value = key;
    sortDesc.value = true;
  }
};

const getSortIcon = (key: string) => {
  if (sortKey.value !== key) return '⇕';
  return sortDesc.value ? '⬇' : '⬆';
};

const filteredPositions = computed(() => {
  let result = marketStore.positions;
  if (showCurrentOnly.value) result = result.filter(pos => pos.symbol === marketStore.currentSymbol);
  if (searchQuery.value.trim()) {
    const q = searchQuery.value.trim().toUpperCase();
    result = result.filter(pos => pos.symbol.includes(q));
  }
  return result;
});

// 结合了过滤和排序的最终数据源
const sortedActivePositions = computed(() => {
  const arr = [...filteredPositions.value];
  return arr.sort((a, b) => {
    let valA, valB;
    switch (sortKey.value) {
      case 'symbol': valA = a.symbol; valB = b.symbol; break;
      case 'side': valA = a.side === 'LONG' ? 1 : -1; valB = b.side === 'LONG' ? 1 : -1; break;
      case 'amount': valA = Math.abs(a.amount); valB = Math.abs(b.amount); break;
      case 'entryPrice': valA = a.entryPrice; valB = b.entryPrice; break;
      case 'pnl': valA = getRealtimePnl(a); valB = getRealtimePnl(b); break;
      default: valA = 0; valB = 0;
    }
    
    if (typeof valA === 'string' && typeof valB === 'string') {
      return sortDesc.value ? valB.localeCompare(valA) : valA.localeCompare(valB);
    }
    return sortDesc.value ? valB - valA : valA - valB;
  });
});

// ==========================================
// 🌟 核心引擎 2：价格、盈亏、资金费计算
// ==========================================
const getCurrentPrice = (symbol: string) => marketStore.marketTickers[symbol]?.lastPrice || 0;
const getPriceColor = (symbol: string) => (marketStore.marketTickers[symbol]?.priceChangePercent || 0) >= 0 ? 'text-green' : 'text-red';
const getTickDecimals = (symbol: string) => {
  const rule = marketStore.symbolRules[symbol];
  if (!rule || !rule.tickSize) return 2;
  const match = rule.tickSize.match(/\.([0]+)1/);
  return match ? match[1].length + 1 : 0;
};

const getDisplayAmount = (pos: any) => {
  const amount = Math.abs(pos.amount);
  if (displayMode.value === 'TOKEN') return `${amount} ${pos.symbol.replace('USDT', '')}`;
  return `${(amount * (getCurrentPrice(pos.symbol) || pos.entryPrice)).toFixed(2)} U`;
};

const getRealtimePnl = (pos: any) => {
  const currentPrice = getCurrentPrice(pos.symbol);
  if (!currentPrice) return pos.unrealizedPnL || 0;
  const amount = Math.abs(pos.amount);
  return pos.side === 'LONG' ? (currentPrice - pos.entryPrice) * amount : (pos.entryPrice - currentPrice) * amount;
};

// 真实的 ROE 需要用到真实杠杆
const getRoe = (pos: any) => {
  const currentPrice = getCurrentPrice(pos.symbol);
  if (!currentPrice || !pos.entryPrice) return 0;
  
  // 价格变动百分比
  const priceDiffPct = ((currentPrice - pos.entryPrice) / pos.entryPrice) * 100;
  const directionMultiplier = pos.side === 'LONG' ? 1 : -1;
  
  // 🌟 如果后台拉取到了真实杠杆，则计算真实 ROE；否则按 1 倍计算名义收益率
  const lev = pos.leverage || 1; 
  return priceDiffPct * directionMultiplier * lev;
};

// 🌟 新增：预估资金费计算
const getEstFundingFee = (pos: any) => {
  const currentPrice = getCurrentPrice(pos.symbol) || pos.entryPrice;
  const notionalValue = Math.abs(pos.amount) * currentPrice;
  
  // 之前 Worker 里如果存的是 0.01 (表示 1%)，需注意单位
  // 假设 marketTickers 里存的 fundingRate 是以 % 为单位 (即 0.01 表示 0.01%)
  const fundingRatePct = marketStore.marketTickers[pos.symbol]?.fundingRate || 0; 
  
  // 资金费 = 名义价值 * 资金费率
  const fee = notionalValue * (fundingRatePct / 100);
  
  // 做多要付钱给做空，做空收钱 (或者反过来，根据资金费率正负)
  // 资金费率为正：多头支付空头。资金费率为负：空头支付多头。
  const directionMultiplier = pos.side === 'LONG' ? -1 : 1;
  return (fee * directionMultiplier).toFixed(4);
};

const getPnlClass = (pnl: number) => {
  if (pnl > 0) return 'text-green';
  if (pnl < 0) return 'text-red';
  return '';
};

const formatTime = (timestamp: number) => {
  if (!timestamp) return '-';
  const d = new Date(timestamp);
  return `${d.getMonth()+1}/${d.getDate()} ${d.getHours().toString().padStart(2,'0')}:${d.getMinutes().toString().padStart(2,'0')}`;
};

const closePosition = async (pos: any) => {
  try {
    toast.info(`正在平仓 ${pos.symbol}...`);
    const res = await fetch('http://localhost:5000/api/order/place', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        symbol: pos.symbol,
        side: pos.side === 'LONG' ? 'SELL' : 'BUY',
        type: 'MARKET',
        quantity: Math.abs(pos.amount),
        reduceOnly: true
      })
    });
    if (res.ok) toast.success(`${pos.symbol} 市价平仓指令已发送`);
    else { const data = await res.json(); throw new Error(data.message || '平仓接口拒绝'); }
  } catch (e: any) {
    toast.error('平仓失败: ' + e.message);
  }
};
</script>

<style scoped>
.position-module { height: 100%; background: #0d1117; display: flex; flex-direction: column; border: 1px solid #30363d; border-radius: 6px; }

/* Tabs 头部设计 */
.module-header { display: flex; justify-content: space-between; align-items: center; background: #161b22; border-bottom: 1px solid #30363d; padding-right: 15px; }
.tabs { display: flex; }
.tabs button { background: transparent; border: none; color: #8b949e; padding: 12px 20px; font-size: 14px; font-weight: bold; cursor: pointer; transition: all 0.2s; border-bottom: 2px solid transparent; }
.tabs button:hover { color: #c9d1d9; }
.tabs button.active { color: #58a6ff; border-bottom-color: #58a6ff; background: rgba(88, 166, 255, 0.05); }
.balance-info { font-size: 12px; color: #8b949e; }

/* 工具栏 */
.toolbar { display: flex; justify-content: space-between; align-items: center; padding: 8px 15px; background: #0d1117; border-bottom: 1px solid #21262d; }
.filter-group, .control-group { display: flex; align-items: center; gap: 12px; }
.search-input { background: #010409; border: 1px solid #30363d; color: #c9d1d9; padding: 4px 8px; border-radius: 4px; font-size: 12px; width: 140px; outline: none; }
.checkbox-label { font-size: 12px; color: #c9d1d9; display: flex; align-items: center; gap: 4px; cursor: pointer; }
.icon-btn { background: #21262d; border: 1px solid #30363d; color: #8b949e; padding: 4px 8px; border-radius: 4px; font-size: 12px; cursor: pointer; outline: none; }

/* 表格主体 */
.position-table { flex: 1; overflow-y: auto; }
table { width: 100%; border-collapse: collapse; font-size: 12px; }
th { text-align: left; padding: 10px 15px; color: #8b949e; background: #0d1117; position: sticky; top: 0; border-bottom: 1px solid #21262d; font-weight: normal; }
td { padding: 10px 15px; border-bottom: 1px solid #21262d; color: #c9d1d9; cursor: pointer; }

/* 🌟 可排序表头样式 */
th.sortable { cursor: pointer; user-select: none; }
th.sortable:hover { color: #c9d1d9; background: #161b22; }

tr:hover td { background: rgba(139, 148, 158, 0.05); }
.active-row td { background: rgba(88, 166, 255, 0.08); border-left: 2px solid #58a6ff; }

.symbol-col { display: flex; align-items: center; gap: 8px; }
.side-col { display: flex; flex-direction: column; gap: 4px; align-items: flex-start; }
.margin-badge { font-size: 10px; background: #21262d; color: #8b949e; padding: 2px 5px; border-radius: 4px; display: inline-flex; gap: 4px; }
.leverage-text { color: #e6edf3; font-weight: bold; }
.side-badge { font-weight: bold; }

.text-green { color: #2ea043 !important; }
.text-red { color: #f85149 !important; }
.font-mono { font-family: 'Courier New', Courier, monospace; font-weight: bold; }

.pnl-col { display: flex; flex-direction: column; gap: 2px; }
.pnl-value { font-size: 13px; }
.pnl-roe { font-size: 11px; opacity: 0.8; }
.funding-fee { font-size: 10px; color: #8b949e; }

.btn-close { background: transparent; border: 1px solid #30363d; color: #c9d1d9; padding: 4px 10px; border-radius: 4px; cursor: pointer; font-size: 12px; }
.btn-close:hover { background: #f85149; border-color: #f85149; color: white; }
.empty-state { text-align: center; color: #8b949e; padding: 60px 0; font-style: italic; }
</style>
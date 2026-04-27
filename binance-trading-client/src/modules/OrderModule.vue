<template>
  <div class="order-module">
    <div class="module-header">
      <span class="symbol-badge">{{ currentSymbol }}</span>
      <span class="price-ticker" v-if="latestPrice" :class="priceTrend">
        {{ latestPrice }}
      </span>
    </div>

    <div class="order-tabs">
      <button :class="{ active: orderType === 'LIMIT' }" @click="orderType = 'LIMIT'">限价单</button>
      <button :class="{ active: orderType === 'MARKET' }" @click="orderType = 'MARKET'">市价单</button>
    </div>

    <div class="order-form-content">
      <div class="available-balance">
        可用余额: <span>{{ marketStore.dynamicUsdtBalance.toFixed(2) }} USDT</span>
      </div>

      <div class="leverage-control">
        <div class="slider-header">
          <label>调整杠杆</label>
          <span>{{ leverage }}x</span>
        </div>
        <input 
          type="range" 
          min="1" 
          max="125" 
          v-model.number="leverage" 
          class="custom-slider no-drag"
          style="touch-action: none;"
          @change="handleLeverageChange"
          @input="calculateAmountFromPercent"
          @pointerdown.stop
          @mousedown.stop
        />
      </div>

      <div class="input-group" v-if="orderType === 'LIMIT'">
        <label>价格 (USDT)</label>
        <input type="number" v-model="price" @input="debouncedCalculate" placeholder="0.00" />
      </div>
      <div class="input-group disabled" v-else>
        <label>价格</label>
        <input type="text" value="市价 (取最新价)" disabled />
      </div>

      <div class="input-group">
        <label>数量 ({{ baseAsset }})</label>
        <input type="number" v-model="amount" @input="handleAmountInput" placeholder="0.00" />
      </div>

      <div class="percentage-control">
        <div class="slider-header">
          <label>仓位占比</label>
          <span>{{ positionPercent }}%</span>
        </div>
        <input 
          type="range" 
          min="0" 
          max="100" 
          step="1"
          v-model.number="positionPercent" 
          class="custom-slider percent-slider no-drag"
          style="touch-action: none;"
          @input="calculateAmountFromPercent"
          @pointerdown.stop
          @mousedown.stop
        />
        <div class="percentage-marks">
          <span @click="setPercentage(25)">25%</span>
          <span @click="setPercentage(50)">50%</span>
          <span @click="setPercentage(75)">75%</span>
          <span @click="setPercentage(100)">100%</span>
        </div>
      </div>

      <div class="order-summary">
        <div class="summary-row">
          <span>占用保证金</span>
          <span>{{ requiredMargin.toFixed(2) }} USDT</span>
        </div>
        <div class="summary-row">
          <span>名义价值</span>
          <span>{{ estimatedValue.toFixed(2) }} USDT</span>
        </div>
      </div>

      <div class="risk-preview" :class="riskLevel" v-if="parseFloat(amount) > 0">
        <div class="risk-row">
          <span>做多强平: <strong class="text-green">{{ previewLiqLong > 0 ? previewLiqLong.toFixed(4) : '--' }}</strong></span>
          <span>做空强平: <strong class="text-red">{{ previewLiqShort > 0 ? previewLiqShort.toFixed(4) : '--' }}</strong></span>
        </div>
        <div class="risk-bar-bg"><div class="risk-bar-fill" :style="{ width: riskDistancePct + '%' }"></div></div>
      </div>
    </div>

    <div class="action-buttons-fixed">
      <button class="btn-buy" :class="{ loading: isSubmitting }" @click="handleBuy" :disabled="isSubmitting || !isValid">
        {{ isSubmitting ? '提交中...' : '买入 / 做多' }}
      </button>
      <button class="btn-sell" :class="{ loading: isSubmitting }" @click="handleSell" :disabled="isSubmitting || !isValid">
        {{ isSubmitting ? '提交中...' : '卖出 / 做空' }}
      </button>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, watch, onMounted } from 'vue';
import { debounce, createAsyncLock } from '@/utils/optimize';
import { useMarketStore } from '@/store/market';
import { useToast } from '@/utils/useToast';
import { calculateLiquidationPrice } from '@/utils/tradeUtils';

const marketStore = useMarketStore();
const toast = useToast();

const currentSymbol = computed(() => marketStore.currentSymbol || 'BTCUSDT');
const baseAsset = computed(() => currentSymbol.value.replace('USDT', ''));
const latestPrice = computed(() => marketStore.marketTickers[currentSymbol.value]?.lastPrice || 0);
const priceTrend = ref('');

onMounted(() => { 
  marketStore.fetchExchangeInfo(); 
  // 同步当前真实杠杆
  if (marketStore.symbolConfigs[currentSymbol.value]) {
    leverage.value = marketStore.symbolConfigs[currentSymbol.value].leverage;
  }
});

const orderType = ref('LIMIT');
const price = ref('');
const amount = ref('');
const leverage = ref(20);
const positionPercent = ref(0);
const estimatedValue = ref(0);
const requiredMargin = ref(0);

// 监听币种切换
watch(currentSymbol, (newSymbol) => {
  price.value = ''; 
  amount.value = ''; 
  positionPercent.value = 0; 
  estimatedValue.value = 0; 
  requiredMargin.value = 0; 
  // 切换币种时，拉取该币种在币安的真实杠杆设置
  leverage.value = marketStore.symbolConfigs[newSymbol]?.leverage || 20; 
  
  if (orderType.value === 'LIMIT' && latestPrice.value > 0) {
    const rule = marketStore.symbolRules[newSymbol] || { tickSize: '0.1', stepSize: '0.001' };
    price.value = formatByStep(latestPrice.value, rule.tickSize);
  }
});

const formatByStep = (value: number, stepStr: string) => {
  const step = parseFloat(stepStr);
  if (step <= 0) return value.toString();
  const factor = (value / step) + Number.EPSILON;
  return (Math.floor(factor) * step).toFixed(stepStr.includes('.') ? stepStr.split('.')[1].length : 0);
};

const getPriceForCalc = () => orderType.value === 'LIMIT' ? (parseFloat(price.value) || 0) : latestPrice.value;

// 🌟 计算逻辑更新：使用 dynamicUsdtBalance
const calculateAmountFromPercent = () => {
  const p = getPriceForCalc();
  if (p <= 0 || positionPercent.value === 0) {
    amount.value = '';
    estimatedValue.value = 0;
    requiredMargin.value = 0;
    return;
  }
  
  const rule = marketStore.symbolRules[currentSymbol.value] || { tickSize: '0.1', stepSize: '0.001' };
  
  // 预留缓冲：市价 2%，限价 1%
  const feeBuffer = orderType.value === 'MARKET' ? 0.98 : 0.99;
  const safeBalance = marketStore.dynamicUsdtBalance * feeBuffer;

  amount.value = formatByStep((safeBalance * (positionPercent.value / 100) * leverage.value) / p, rule.stepSize);
  
  const notional = parseFloat(amount.value) * p;
  estimatedValue.value = notional;
  requiredMargin.value = notional / leverage.value;
};

const setPercentage = (pct: number) => { 
  positionPercent.value = pct; 
  calculateAmountFromPercent(); 
};

const handleAmountInput = () => {
  const p = getPriceForCalc(); 
  const a = parseFloat(amount.value);
  if (p > 0 && a > 0) {
    estimatedValue.value = p * a; 
    requiredMargin.value = (p * a) / leverage.value;
    // 使用动态余额反推百分比
    const feeBuffer = orderType.value === 'MARKET' ? 0.98 : 0.99;
    const safeBalance = marketStore.dynamicUsdtBalance * feeBuffer;
    if (safeBalance > 0) {
      positionPercent.value = Math.min(100, Math.max(0, Math.round((requiredMargin.value / (safeBalance)) * 100)));
    }
  } else { 
    estimatedValue.value = 0; requiredMargin.value = 0; positionPercent.value = 0; 
  }
};

const debouncedCalculate = debounce(calculateAmountFromPercent, 300);
watch(orderType, calculateAmountFromPercent);

// 修改杠杆
const handleLeverageChange = async () => {
  try {
    const res = await fetch(`http://localhost:5000/api/account/leverage`, { 
      method: 'POST', 
      headers: { 'Content-Type': 'application/json' }, 
      body: JSON.stringify({ symbol: currentSymbol.value, leverage: leverage.value }) 
    });
    if (!res.ok) throw new Error((await res.json()).message || '修改杠杆失败');
    toast.info(`杠杆调整为 ${leverage.value}x`); 
    calculateAmountFromPercent();
  } catch (e: any) { toast.error(e.message); }
};

// 强平价推演
const getOtherCrossPositions = () => marketStore.positions.filter(p => p.symbol !== currentSymbol.value && p.marginType === 'cross').map(p => {
  const pr = marketStore.marketTickers[p.symbol]?.lastPrice || p.entryPrice;
  const amt = Math.abs(p.amount);
  return { ...p, unrealizedPnL: p.side === 'LONG' ? (pr - p.entryPrice) * amt : (p.entryPrice - pr) * amt, markPrice: pr };
});

const previewLiqLong = computed(() => parseFloat(amount.value) > 0 ? calculateLiquidationPrice({ symbol: currentSymbol.value, side: 'LONG', amount: parseFloat(amount.value), entryPrice: getPriceForCalc(), leverage: leverage.value, marginType: 'cross' }, marketStore.dynamicUsdtBalance, getOtherCrossPositions()) : 0);
const previewLiqShort = computed(() => parseFloat(amount.value) > 0 ? calculateLiquidationPrice({ symbol: currentSymbol.value, side: 'SHORT', amount: parseFloat(amount.value), entryPrice: getPriceForCalc(), leverage: leverage.value, marginType: 'cross' }, marketStore.dynamicUsdtBalance, getOtherCrossPositions()) : 0);

const riskDistancePct = computed(() => {
  if (previewLiqLong.value <= 0 || getPriceForCalc() <= 0) return 0;
  return Math.min(100, Math.max(0, 100 - ((Math.abs(getPriceForCalc() - previewLiqLong.value) / getPriceForCalc()) * 100 * leverage.value)));
});
const riskLevel = computed(() => riskDistancePct.value > 80 ? 'danger' : riskDistancePct.value > 50 ? 'warning' : 'safe');

const isValid = computed(() => (orderType.value === 'LIMIT' && parseFloat(price.value) > 0 && parseFloat(amount.value) > 0) || (orderType.value === 'MARKET' && parseFloat(amount.value) > 0));
const isSubmitting = ref(false);
const orderLock = createAsyncLock();

const placeOrder = async (side: string) => {
  const rule = marketStore.symbolRules[currentSymbol.value] || { tickSize: '0.1', stepSize: '0.001' };
  try {
    const res = await fetch('http://localhost:5000/api/order/place-ws', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ 
        symbol: currentSymbol.value, 
        side, 
        type: orderType.value, 
        quantity: parseFloat(formatByStep(parseFloat(amount.value), rule.stepSize)), 
        price: orderType.value === 'LIMIT' ? parseFloat(formatByStep(parseFloat(price.value), rule.tickSize)) : null 
      })
    });
    const data = await res.json();
    if (!res.ok || data.error) throw new Error(data.error?.msg || '下单被拒');
    toast.success(`下单成功!`); return true;
  } catch (e: any) { toast.error(e.message); return false; }
};

const handleBuy = async () => { await orderLock(async () => { isSubmitting.value = true; if (await placeOrder('BUY')) { amount.value = ''; positionPercent.value = 0; handleAmountInput(); } isSubmitting.value = false; }); };
const handleSell = async () => { await orderLock(async () => { isSubmitting.value = true; if (await placeOrder('SELL')) { amount.value = ''; positionPercent.value = 0; handleAmountInput(); } isSubmitting.value = false; }); };
</script>

<style scoped>
/* 🌟 极致紧凑化布局 */
.order-module { width: 100%; height: 100%; display: flex; flex-direction: column; background: #0d1117; color: #c9d1d9; font-size: 12px; }

/* 头部固定 */
.module-header { display: flex; justify-content: space-between; align-items: center; padding: 8px 12px; background: #161b22; border-bottom: 1px solid #30363d; flex-shrink: 0; }
.symbol-badge { font-size: 14px; font-weight: 800; color: #e6edf3; letter-spacing: 0.5px; }
.price-ticker { font-size: 13px; font-family: monospace; font-weight: bold; }

/* 标签切换固定 */
.order-tabs { display: flex; border-bottom: 1px solid #21262d; flex-shrink: 0; }
.order-tabs button { flex: 1; padding: 6px 0; background: transparent; border: none; color: #8b949e; cursor: pointer; font-weight: bold; border-bottom: 2px solid transparent; transition: all 0.2s; font-size: 12px; }
.order-tabs button.active { color: #e6edf3; border-bottom-color: #58a6ff; }

/* 🌟 表单区域自适应滚动 */
.order-form-content { padding: 10px 12px; display: flex; flex-direction: column; gap: 10px; flex: 1; overflow-y: auto; }

/* 内部控件 */
.available-balance { display: flex; justify-content: space-between; color: #8b949e; font-size: 11px; }
.available-balance span { color: #e6edf3; font-weight: bold; }

.input-group { display: flex; flex-direction: column; gap: 4px; }
.input-group label { color: #8b949e; font-size: 11px; }
.input-group input { background: #161b22; border: 1px solid #30363d; border-radius: 4px; padding: 6px 8px; color: #e6edf3; outline: none; font-family: monospace; font-size: 12px; }
.input-group input:focus { border-color: #58a6ff; }

.slider-header { display: flex; justify-content: space-between; color: #8b949e; font-size: 11px; margin-bottom: 4px; }
.slider-header span { color: #e6edf3; font-weight: bold; }
.custom-slider { -webkit-appearance: none; width: 100%; height: 4px; background: #30363d; border-radius: 2px; outline: none; margin-bottom: 4px; }
.custom-slider::-webkit-slider-thumb { -webkit-appearance: none; appearance: none; width: 14px; height: 14px; border-radius: 50%; background: #58a6ff; cursor: pointer; }
.percent-slider::-webkit-slider-thumb { background: #f85149; }

.percentage-marks { display: flex; justify-content: space-between; padding-top: 2px; }
.percentage-marks span { color: #8b949e; font-size: 10px; cursor: pointer; }

.order-summary { padding: 8px 0; border-top: 1px dashed #30363d; border-bottom: 1px dashed #30363d; display: flex; flex-direction: column; gap: 4px; font-size: 11px; }
.summary-row { display: flex; justify-content: space-between; color: #8b949e; }

.risk-preview { background: rgba(22, 27, 34, 0.5); padding: 6px; border-radius: 6px; border: 1px solid #30363d; font-size: 11px; }
.risk-row { display: flex; justify-content: space-between; margin-bottom: 4px; }
.risk-bar-bg { height: 3px; background: #21262d; border-radius: 2px; }
.risk-bar-fill { height: 100%; background: #2ea043; transition: 0.3s; }

/* 🌟 操作按钮固定在底部 */
.action-buttons-fixed { display: flex; gap: 8px; padding: 10px 12px; background: #0d1117; border-top: 1px solid #21262d; flex-shrink: 0; }
.action-buttons-fixed button { flex: 1; padding: 10px 0; border: none; border-radius: 4px; font-weight: bold; cursor: pointer; color: white; font-size: 13px; }
.btn-buy { background: #2ea043; } .btn-sell { background: #f85149; }
.action-buttons-fixed button:disabled { opacity: 0.5; cursor: not-allowed; }

/* 文字颜色 */
.text-green { color: #2ea043; } .text-red { color: #f85149; }
</style>
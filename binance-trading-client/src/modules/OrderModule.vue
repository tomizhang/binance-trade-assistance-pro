<template>
  <div class="order-module">
    <div class="module-header">
      <div class="symbol-info">
        <span class="symbol-badge">{{ currentSymbol }}</span>
        <span class="price-ticker" v-if="latestPrice" :class="priceTrend">{{ latestPrice }}</span>
      </div>
      <div class="margin-mode-toggle">
        <button :class="{ active: marginType === 'cross' }" @click="marginType = 'cross'">全仓</button>
        <button :class="{ active: marginType === 'isolated' }" @click="marginType = 'isolated'">逐仓</button>
      </div>
    </div>

    <div class="order-tabs">
      <button :class="{ active: orderType === 'LIMIT' }" @click="orderType = 'LIMIT'">限价单</button>
      <button :class="{ active: orderType === 'MARKET' }" @click="orderType = 'MARKET'">市价单</button>
    </div>

    <div class="order-form">
      
      <div class="inputs-section">
        <div class="input-group" v-if="orderType === 'LIMIT'">
          <label>价格 (USDT)</label>
          <div class="input-wrapper">
            <input type="number" v-model="price" @input="debouncedCalculate" placeholder="0.00" />
            <button class="shortcut-btn" @click="useLastPrice">最新价</button>
          </div>
        </div>
        <div class="input-group disabled" v-else>
          <label>价格</label>
          <div class="input-wrapper">
            <input type="text" value="市价最优成交" disabled />
          </div>
        </div>

        <div class="input-group">
          <label>数量 ({{ baseAsset }})</label>
          <div class="input-wrapper">
            <input type="number" v-model="amount" @input="handleAmountInput" placeholder="0.00" />
          </div>
        </div>
      </div>

      <div class="sliders-section">
        <div class="slider-row">
          <div class="slider-header">
            <span>杠杆倍数</span> <span class="val-text">{{ leverage }}x</span>
          </div>
          <input type="range" min="1" max="125" v-model.number="leverage" class="custom-slider" @change="handleLeverageChange" @input="calculateAmountFromPercent" 
            @mousedown.stop
            @touchstart.stop
            @pointerdown.stop />
        </div>
        
        <div class="slider-row">
          <div class="slider-header">
            <span>仓位占比</span> <span class="val-text">{{ positionPercent }}%</span>
          </div>
          <input type="range" min="0" max="100" step="1" v-model.number="positionPercent" class="custom-slider percent-slider" @input="calculateAmountFromPercent" 
            @mousedown.stop
            @touchstart.stop
            @pointerdown.stop/>
          <div class="percentage-marks">
            <span @click="setPercentage(25)">25%</span>
            <span @click="setPercentage(50)">50%</span>
            <span @click="setPercentage(75)">75%</span>
            <span @click="setPercentage(100)">100%</span>
          </div>
        </div>
      </div>

      <div class="info-grid-section">
        <div class="info-item">
          <span class="label">可用余额</span>
          <span class="value">{{ marketStore.usdtBalance.toFixed(2) }} U</span>
        </div>
        <div class="info-item">
          <span class="label">占用保证金</span>
          <span class="value highlight">{{ requiredMargin.toFixed(2) }} U</span>
        </div>
        
        <div class="info-item liq-box long-liq" :class="{ 'danger': riskLevel === 'danger' && parseFloat(amount) > 0 }">
          <span class="label">做多强平价</span>
          <span class="value">{{ previewLiqLong > 0 ? previewLiqLong.toFixed(4) : '--' }}</span>
        </div>
        <div class="info-item liq-box short-liq" :class="{ 'danger': riskLevel === 'danger' && parseFloat(amount) > 0 }">
          <span class="label">做空强平价</span>
          <span class="value">{{ previewLiqShort > 0 ? previewLiqShort.toFixed(4) : '--' }}</span>
        </div>

        <div class="risk-bar-container" v-if="parseFloat(amount) > 0">
          <div class="risk-bar-fill" :style="{ width: riskDistancePct + '%' }" :class="riskLevel"></div>
        </div>
      </div>

    </div>

    <div class="order-footer">
      <div class="action-buttons">
        <button class="btn-buy" :class="{ loading: isSubmitting }" @click="handleBuy" :disabled="isSubmitting || !isValid">
          {{ isSubmitting ? '提交中...' : '做多 (LONG)' }}
        </button>
        <button class="btn-sell" :class="{ loading: isSubmitting }" @click="handleSell" :disabled="isSubmitting || !isValid">
          {{ isSubmitting ? '提交中...' : '做空 (SHORT)' }}
        </button>
      </div>
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
});

const marginType = ref<'cross' | 'isolated'>('cross'); 
const orderType = ref('LIMIT');
const price = ref('');
const amount = ref('');
const leverage = ref(20);
const positionPercent = ref(0);
const estimatedValue = ref(0);
const requiredMargin = ref(0);

const useLastPrice = () => { if (latestPrice.value > 0) price.value = latestPrice.value.toString(); };

watch(currentSymbol, (newSymbol) => {
  price.value = '';
  amount.value = '';
  positionPercent.value = 0;
  estimatedValue.value = 0;
  requiredMargin.value = 0;
  leverage.value = 20; 

  if (orderType.value === 'LIMIT' && latestPrice.value > 0) {
    const rule = marketStore.symbolRules[newSymbol] || { tickSize: '0.1', stepSize: '0.001' };
    price.value = formatByStep(latestPrice.value, rule.tickSize);
  }
});

const formatByStep = (value: number, stepStr: string) => {
  const step = parseFloat(stepStr);
  if (step <= 0) return value.toString();
  const factor = (value / step) + Number.EPSILON;
  const rounded = Math.floor(factor) * step;
  const precision = stepStr.includes('.') ? stepStr.split('.')[1].length : 0;
  return rounded.toFixed(precision);
};

const getPriceForCalc = () => {
  return orderType.value === 'LIMIT' ? (parseFloat(price.value) || 0) : latestPrice.value;
};

const calculateAmountFromPercent = () => {
  const p = getPriceForCalc();
  if (p <= 0 || positionPercent.value === 0) return;

  const rule = marketStore.symbolRules[currentSymbol.value] || { tickSize: '0.1', stepSize: '0.001' };

  const plannedMargin = marketStore.usdtBalance * (positionPercent.value / 100);
  const notional = plannedMargin * leverage.value;
  const rawAmount = notional / p;

  amount.value = formatByStep(rawAmount, rule.stepSize);
  
  if (orderType.value === 'LIMIT' && price.value) {
     price.value = formatByStep(parseFloat(price.value), rule.tickSize);
  }

  const actualNotional = parseFloat(amount.value) * p;
  estimatedValue.value = actualNotional;
  requiredMargin.value = actualNotional / leverage.value;
};

const setPercentage = (pct: number) => {
  positionPercent.value = pct;
  calculateAmountFromPercent();
};

const handleAmountInput = () => {
  const p = getPriceForCalc();
  const a = parseFloat(amount.value);
  
  if (p > 0 && a > 0) {
    const notional = p * a;
    const margin = notional / leverage.value;
    
    estimatedValue.value = notional;
    requiredMargin.value = margin;

    if (marketStore.usdtBalance > 0) {
      let pct = (margin / marketStore.usdtBalance) * 100;
      positionPercent.value = Math.min(100, Math.max(0, Math.round(pct)));
    }
  } else {
    estimatedValue.value = 0;
    requiredMargin.value = 0;
    positionPercent.value = 0;
  }
};

const debouncedCalculate = debounce(calculateAmountFromPercent, 300);
watch(orderType, calculateAmountFromPercent);

const handleLeverageChange = async () => {
  try {
    const response = await fetch(`http://localhost:5000/api/account/leverage`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ symbol: currentSymbol.value, leverage: leverage.value })
    });
    const data = await response.json();
    if (!response.ok) throw new Error(data.message || '修改杠杆失败');
    toast.info(`[${currentSymbol.value}] 杠杆调整为 ${leverage.value}x`);
    calculateAmountFromPercent();
  } catch (e: any) {
    toast.error(e.message);
  }
};

const getOtherCrossPositions = () => {
  return marketStore.positions
    .filter(p => p.symbol !== currentSymbol.value && p.marginType === 'cross')
    .map(p => {
      const price = marketStore.marketTickers[p.symbol]?.lastPrice || p.entryPrice;
      const amt = Math.abs(p.amount);
      const uPnL = p.side === 'LONG' ? (price - p.entryPrice) * amt : (p.entryPrice - price) * amt;
      return { ...p, unrealizedPnL: uPnL, markPrice: price };
    });
};

const previewLiqLong = computed(() => {
  const qty = parseFloat(amount.value) || 0;
  const p = getPriceForCalc();
  if (qty <= 0 || p <= 0) return 0;
  return calculateLiquidationPrice(
    { symbol: currentSymbol.value, side: 'LONG', amount: qty, entryPrice: p, leverage: leverage.value, marginType: marginType.value },
    marketStore.usdtBalance, getOtherCrossPositions()
  );
});

const previewLiqShort = computed(() => {
  const qty = parseFloat(amount.value) || 0;
  const p = getPriceForCalc();
  if (qty <= 0 || p <= 0) return 0;
  return calculateLiquidationPrice(
    { symbol: currentSymbol.value, side: 'SHORT', amount: qty, entryPrice: p, leverage: leverage.value, marginType: marginType.value },
    marketStore.usdtBalance, getOtherCrossPositions()
  );
});

const riskDistancePct = computed(() => {
  const p = getPriceForCalc();
  if (previewLiqLong.value <= 0 || p <= 0) return 0;
  const drop = Math.abs(p - previewLiqLong.value) / p;
  const danger = Math.max(0, 100 - (drop * 100 * leverage.value));
  return Math.min(100, danger);
});

const riskLevel = computed(() => {
  if (riskDistancePct.value > 80) return 'danger';
  if (riskDistancePct.value > 50) return 'warning';
  return 'safe';
});

const isValid = computed(() => {
  if (orderType.value === 'LIMIT' && (!price.value || parseFloat(price.value) <= 0)) return false;
  if (!amount.value || parseFloat(amount.value) <= 0) return false;
  return true;
});

const isSubmitting = ref(false);
const orderLock = createAsyncLock();

const placeOrder = async (side: string) => {
  const rule = marketStore.symbolRules[currentSymbol.value] || { tickSize: '0.1', stepSize: '0.001' };
  const safeQuantity = parseFloat(formatByStep(parseFloat(amount.value), rule.stepSize));
  const safePrice = orderType.value === 'LIMIT' ? parseFloat(formatByStep(parseFloat(price.value), rule.tickSize)) : null;

  const reqBody = {
    symbol: currentSymbol.value, 
    side: side,
    type: orderType.value,
    quantity: safeQuantity,
    price: safePrice,
    leverage: leverage.value,
    marginType: marginType.value
  };

  try {
    const response = await fetch('http://localhost:5000/api/order/place-ws', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(reqBody)
    });
    const data = await response.json();
    if (!response.ok || data.error || (data.status && data.status !== 200)) {
      const errorMsg = data.error?.msg || data.message || '下单被拒绝';
      const errorCode = data.error?.code || 'Unknown';
      throw new Error(`[${errorCode}] ${errorMsg}`);
    }
    toast.success(`下单成功! 订单号: ${data.result?.orderId || data.id}`); 
    return true;
  } catch (error: any) {
    toast.error(error.message);
    return false;
  }
};

const handleBuy = async () => {
  await orderLock(async () => {
    isSubmitting.value = true;
    const success = await placeOrder('BUY');
    isSubmitting.value = false;
    if (success) { amount.value = ''; positionPercent.value = 0; handleAmountInput(); }
  });
};

const handleSell = async () => {
  await orderLock(async () => {
    isSubmitting.value = true;
    const success = await placeOrder('SELL');
    isSubmitting.value = false;
    if (success) { amount.value = ''; positionPercent.value = 0; handleAmountInput(); }
  });
};
</script>

<style scoped>
.order-module { width: 100%; height: 100%; display: flex; flex-direction: column; background: #0d1117; color: #c9d1d9; font-size: 12px; }

/* 🌟 头部聚合压缩 */
.module-header { display: flex; justify-content: space-between; align-items: center; padding: 8px 12px; background: #161b22; border-bottom: 1px solid #30363d; flex-shrink: 0; }
.symbol-info { display: flex; align-items: baseline; gap: 8px; }
.symbol-badge { font-size: 14px; font-weight: 800; color: #e6edf3; }
.price-ticker { font-size: 13px; font-family: monospace; font-weight: bold; color: #8b949e; }

.margin-mode-toggle { display: flex; background: #010409; border: 1px solid #30363d; border-radius: 4px; overflow: hidden; }
.margin-mode-toggle button { padding: 4px 8px; background: transparent; border: none; color: #8b949e; font-size: 11px; cursor: pointer; transition: 0.2s; }
.margin-mode-toggle button.active { background: #30363d; color: #e6edf3; font-weight: bold; }

.order-tabs { display: flex; border-bottom: 1px solid #21262d; flex-shrink: 0; }
.order-tabs button { flex: 1; padding: 8px 0; background: transparent; border: none; color: #8b949e; cursor: pointer; font-weight: bold; border-bottom: 2px solid transparent; }
.order-tabs button.active { color: #e6edf3; border-bottom-color: #58a6ff; background: rgba(88, 166, 255, 0.05); }

/* 🌟 表单区 (核心压缩区) */
.order-form { flex: 1; overflow-y: auto; padding: 12px; display: flex; flex-direction: column; gap: 12px; }

/* 输入区压缩 */
.inputs-section { display: flex; flex-direction: column; gap: 10px; }
.input-group { display: flex; align-items: center; background: #161b22; border: 1px solid #30363d; border-radius: 4px; padding: 0; transition: 0.2s; overflow: hidden; }
.input-group:focus-within { border-color: #58a6ff; }
.input-group label { color: #8b949e; font-size: 12px; padding-left: 10px; width: 65px; white-space: nowrap; flex-shrink: 0; }
.input-wrapper { flex: 1; display: flex; }
.input-wrapper input { flex: 1; background: transparent; border: none; color: #e6edf3; padding: 8px; outline: none; font-family: monospace; text-align: right; }
.input-group.disabled { background: #0d1117; opacity: 0.8; }
.shortcut-btn { background: #21262d; border: none; border-left: 1px solid #30363d; color: #8b949e; padding: 0 8px; cursor: pointer; font-size: 11px; transition: 0.2s; }
.shortcut-btn:hover { background: #30363d; color: #c9d1d9; }

/* 滑块区压缩 */
.sliders-section { display: flex; flex-direction: column; gap: 10px; background: rgba(22, 27, 34, 0.4); padding: 10px; border-radius: 6px; border: 1px solid #21262d; }
.slider-row { display: flex; flex-direction: column; gap: 4px; }
.slider-header { display: flex; justify-content: space-between; font-size: 11px; color: #8b949e; }
.val-text { color: #e6edf3; font-weight: bold; font-family: monospace; }

.custom-slider { -webkit-appearance: none; width: 100%; height: 4px; background: #30363d; border-radius: 2px; outline: none; }
.custom-slider::-webkit-slider-thumb { -webkit-appearance: none; width: 14px; height: 14px; border-radius: 50%; background: #58a6ff; cursor: pointer; transition: 0.1s; }
.percent-slider::-webkit-slider-thumb { background: #d29922; }

.percentage-marks { display: flex; justify-content: space-between; padding-top: 2px; }
.percentage-marks span { color: #8b949e; font-size: 10px; cursor: pointer; padding: 2px 4px; border-radius: 3px; }
.percentage-marks span:hover { background: #21262d; color: #e6edf3; }

/* 🌟 信息对比 Grid 网格阵列 (极度紧凑) */
.info-grid-section { display: grid; grid-template-columns: 1fr 1fr; gap: 8px; background: #161b22; border: 1px dashed #30363d; padding: 10px; border-radius: 6px; position: relative; }
.info-item { display: flex; flex-direction: column; gap: 2px; }
.info-item .label { font-size: 11px; color: #8b949e; }
.info-item .value { font-size: 12px; color: #e6edf3; font-weight: bold; font-family: monospace; }
.info-item .highlight { color: #58a6ff; }

/* 强平警戒框 */
.liq-box { padding: 4px 6px; background: rgba(13, 17, 23, 0.5); border-radius: 4px; border: 1px solid #21262d; transition: 0.3s; }
.liq-box.danger { border-color: rgba(248, 81, 73, 0.5); background: rgba(248, 81, 73, 0.1); }
.liq-box.long-liq .value { color: #2ea043; }
.liq-box.short-liq .value { color: #f85149; }

.risk-bar-container { grid-column: span 2; height: 2px; background: #21262d; border-radius: 1px; overflow: hidden; margin-top: 2px; }
.risk-bar-fill { height: 100%; background: #2ea043; transition: width 0.3s, background 0.3s; }
.risk-bar-fill.warning { background: #d29922; }
.risk-bar-fill.danger { background: #f85149; }

/* 🌟 底部固定按钮区 */
.order-footer { padding: 12px; border-top: 1px solid #21262d; background: #0d1117; flex-shrink: 0; }
.action-buttons { display: flex; gap: 10px; }
.action-buttons button { flex: 1; padding: 12px 0; border: none; border-radius: 4px; font-weight: bold; font-size: 13px; cursor: pointer; color: white; transition: 0.2s; }
.btn-buy { background: #2ea043; }
.btn-sell { background: #f85149; }
.action-buttons button:hover:not(:disabled) { filter: brightness(1.15); transform: translateY(-1px); }
.action-buttons button:active:not(:disabled) { transform: translateY(0); }
.action-buttons button:disabled { opacity: 0.3; cursor: not-allowed; }
.action-buttons button.loading { animation: pulse 1.5s infinite; }

@keyframes pulse { 0% { opacity: 0.7; } 50% { opacity: 1; } 100% { opacity: 0.7; } }
</style>
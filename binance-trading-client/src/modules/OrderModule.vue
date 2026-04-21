<template>
  <div class="order-module">
    <div class="order-tabs">
      <button :class="{ active: orderType === 'LIMIT' }" @click="orderType = 'LIMIT'">限价单</button>
      <button :class="{ active: orderType === 'MARKET' }" @click="orderType = 'MARKET'">市价单</button>
    </div>

    <div class="order-form">
      <div class="available-balance">
        可用余额: <span>{{ marketStore.usdtBalance.toFixed(2) }} USDT</span>
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
          class="custom-slider"
          @input="calculateAmountFromPercent"
          @change="handleLeverageChange"
          @mousedown.stop
          @touchstart.stop
          @pointerdown.stop
        />
      </div>

      <div class="input-group" v-if="orderType === 'LIMIT'">
        <label>价格 (USDT)</label>
        <input 
          type="number" 
          v-model="price" 
          @input="debouncedCalculate" 
          placeholder="0.00" 
        />
      </div>
      <div class="input-group disabled" v-else>
        <label>价格</label>
        <input type="text" value="市价 (取最新价)" disabled />
      </div>

      <div class="input-group">
        <label>数量 ({{ symbol.replace('USDT', '') }})</label>
        <input 
          type="number" 
          v-model="amount" 
          @input="handleAmountInput" 
          placeholder="0.00" 
        />
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
          class="custom-slider percent-slider"
          @input="calculateAmountFromPercent"
          @mousedown.stop
          @touchstart.stop
          @pointerdown.stop
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

      <div class="action-buttons">
        <button 
          class="btn-buy" 
          :class="{ loading: isSubmitting }" 
          @click="handleBuy" 
          :disabled="isSubmitting || !isValid"
        >
          {{ isSubmitting ? '提交中...' : '买入 / 做多' }}
        </button>
        
        <button 
          class="btn-sell" 
          :class="{ loading: isSubmitting }" 
          @click="handleSell" 
          :disabled="isSubmitting || !isValid"
        >
          {{ isSubmitting ? '提交中...' : '卖出 / 做空' }}
        </button>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, watch ,onMounted} from 'vue';
import { debounce, createAsyncLock } from '@/utils/optimize';
import { useMarketStore } from '@/store/market';
import { useToast } from '@/utils/useToast';

const props = defineProps({
  symbol: { type: String, default: 'BTCUSDT' }
});

const marketStore = useMarketStore();
const toast = useToast(); // 实例化


// 🌟 新增：组件挂载时，确保拉取到了精度规则
onMounted(() => {
  marketStore.fetchExchangeInfo();
});

// ==========================================
// 1. 基础状态与滑块控制
// ==========================================
const orderType = ref('LIMIT');
const price = ref('');
const amount = ref('');

const leverage = ref(20);         // 默认 20 倍杠杆
const positionPercent = ref(0);   // 默认仓位占比 0%

const estimatedValue = ref(0);    // 名义价值
const requiredMargin = ref(0);    // 占用保证金

// ==========================================
// 2. 核心数学计算逻辑
// ==========================================
// 获取当前用于计算的价格
const getPriceForCalc = () => {
  if (orderType.value === 'LIMIT') {
    return parseFloat(price.value) || 0;
  } else {
    // 市价单去 Store 拿最新的 Ticker 价
    return marketStore.marketTickers[props.symbol]?.lastPrice || 0;
  }
};

// 拖动滑块 -> 反推数量
// const calculateAmountFromPercent = () => {
//   const p = getPriceForCalc();
//   if (p <= 0 || positionPercent.value === 0) {
//     amount.value = '';
//     estimatedValue.value = 0;
//     requiredMargin.value = 0;
//     return;
//   }

//   // 计划使用的保证金 = 可用余额 * (仓位百分比 / 100)
//   const plannedMargin = marketStore.usdtBalance * (positionPercent.value / 100);
//   // 名义价值 = 保证金 * 杠杆倍数
//   const notional = plannedMargin * leverage.value;
//   // 计算数量
//   const calculatedAmount = notional / p;

//   amount.value = calculatedAmount.toFixed(4);
//   estimatedValue.value = notional;
//   requiredMargin.value = plannedMargin;
// };

// 在滑块上绑定 @change，只有松开鼠标才触发
const handleLeverageChange = async () => {
  try {
    console.log('leverage',JSON.stringify({ symbol: props.symbol, leverage: leverage.value }))
    await fetch(`http://localhost:5000/api/account/leverage`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ symbol: props.symbol, leverage: leverage.value })
    });
    // 杠杆变了，重新计算一下下单参数
    toast.info(`杠杆已调整为 ${leverage.value}x`);
    calculateAmountFromPercent();
  } catch (e) {
    toast.error(`杠杆修改失败: ${e.message}`);
  }
};

const setPercentage = (pct: number) => {
  positionPercent.value = pct;
  calculateAmountFromPercent();
};

const usdtAmount = ref(''); // 用户输入的 USDT 金额

// 🌟 优化 1：输入 USDT 自动算数量
const calculateQuantityFromUsdt = () => {
  const p = getPriceForCalc();
  const usdt = parseFloat(usdtAmount.value);
  if (p > 0 && usdt > 0) {
    amount.value = (usdt * leverage.value / p).toFixed(4);
    estimatedValue.value = usdt * leverage.value;
    requiredMargin.value = usdt;
  }
};

// ==========================================
// 🌟 终极 JS 浮点数安全截断算法
// ==========================================
const formatByStep = (value: number, stepStr: string) => {
  const step = parseFloat(stepStr);
  if (step <= 0) return value.toString();

  // 避免 JS 浮点数除法导致的 0.9999999 被向下取整成 0
  const factor = (value / step) + Number.EPSILON;
  const rounded = Math.floor(factor) * step;

  // 获取步长要求的小数位数，避免出现 0.1000000001
  const precision = stepStr.includes('.') ? stepStr.split('.')[1].length : 0;
  return rounded.toFixed(precision);
};

// ==========================================
// 重写计算逻辑：应用精度过滤
// ==========================================
const calculateAmountFromPercent = () => {
  const p = getPriceForCalc();
  if (p <= 0 || positionPercent.value === 0) return;

  // 1. 获取当前币种的规则，如果没拉到，给一个保守的默认值
  const rule = marketStore.symbolRules[props.symbol] || { tickSize: '0.1', stepSize: '0.001' };

  // 2. 计算理论上的资金和数量
  const plannedMargin = marketStore.usdtBalance * (positionPercent.value / 100);
  const notional = plannedMargin * leverage.value;
  const rawAmount = notional / p;

  // 3. 🌟 核心：使用规则进行极其严格的截断
  amount.value = formatByStep(rawAmount, rule.stepSize);
  
  if (orderType.value === 'LIMIT' && price.value) {
     price.value = formatByStep(parseFloat(price.value), rule.tickSize);
  }

  // 计算完截断后的真实占用资金
  const actualNotional = parseFloat(amount.value) * p;
  estimatedValue.value = actualNotional;
  requiredMargin.value = actualNotional / leverage.value;
};

// 手动输入数量 -> 反推滑块百分比
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

// 防抖包裹，防止用户输入价格时疯狂触发计算
const debouncedCalculate = debounce(calculateAmountFromPercent, 300);

// 当订单类型或余额发生变化时，重新计算
watch(orderType, calculateAmountFromPercent);

// ==========================================
// 3. 表单验证与后端交互 (API 发单)
// ==========================================
const isValid = computed(() => {
  if (orderType.value === 'LIMIT' && (!price.value || parseFloat(price.value) <= 0)) return false;
  if (!amount.value || parseFloat(amount.value) <= 0) return false;
  return true;
});

const isSubmitting = ref(false);
const orderLock = createAsyncLock();

// ==========================================
// 重写发单拦截逻辑
// ==========================================
const placeOrder = async (side: string) => {
  // 🌟 最终防线：发单前再格式化一次，防止用户手填了非法精度
  const rule = marketStore.symbolRules[props.symbol] || { tickSize: '0.1', stepSize: '0.001' };
  const safeQuantity = parseFloat(formatByStep(parseFloat(amount.value), rule.stepSize));
  const safePrice = orderType.value === 'LIMIT' ? parseFloat(formatByStep(parseFloat(price.value), rule.tickSize)) : null;

  const reqBody = {
    symbol: props.symbol,
    side: side,
    type: orderType.value,
    quantity: safeQuantity,
    price: safePrice
  };

  try {
    const response = await fetch('http://localhost:5000/api/order/place-ws', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(reqBody)
    });

    const data = await response.json();

    // 🌟 解析币安的错误包，避免“假成功”
    if (!response.ok || data.error || (data.status && data.status !== 200)) {
      const errorMsg = data.error?.msg || data.message || '下单被币安拒绝';
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
    // 下单成功后清空表单，重置滑块
    if (success) {
      amount.value = '';
      positionPercent.value = 0;
      handleAmountInput();
    }
  });
};

const handleSell = async () => {
  await orderLock(async () => {
    isSubmitting.value = true;
    const success = await placeOrder('SELL');
    isSubmitting.value = false;
    if (success) {
      amount.value = '';
      positionPercent.value = 0;
      handleAmountInput();
    }
  });
};
</script>

<style scoped>
.order-module { width: 100%; height: 100%; display: flex; flex-direction: column; background: #0d1117; color: #c9d1d9; font-size: 13px; }

.order-tabs { display: flex; border-bottom: 1px solid #21262d; }
.order-tabs button { flex: 1; padding: 10px 0; background: transparent; border: none; color: #8b949e; cursor: pointer; font-weight: bold; border-bottom: 2px solid transparent; transition: all 0.2s; }
.order-tabs button.active { color: #e6edf3; border-bottom-color: #58a6ff; }
.order-tabs button:hover:not(.active) { color: #c9d1d9; background: #161b22; }

.order-form { padding: 15px; display: flex; flex-direction: column; gap: 15px; flex: 1; overflow-y: auto; }

.available-balance { display: flex; justify-content: space-between; color: #8b949e; font-size: 12px; }
.available-balance span { color: #e6edf3; font-weight: bold; }

.input-group { display: flex; flex-direction: column; gap: 6px; }
.input-group label { color: #8b949e; font-size: 12px; }
.input-group input { background: #161b22; border: 1px solid #30363d; border-radius: 4px; padding: 8px 10px; color: #e6edf3; outline: none; font-family: monospace; transition: border-color 0.2s; }
.input-group input:focus { border-color: #58a6ff; }
.input-group.disabled input { background: #0d1117; color: #8b949e; cursor: not-allowed; }

/* 滑块样式 */
.slider-header { display: flex; justify-content: space-between; color: #8b949e; font-size: 12px; margin-bottom: 8px; }
.slider-header span { color: #e6edf3; font-weight: bold; }

.custom-slider {
  -webkit-appearance: none; width: 100%; height: 4px; background: #30363d; border-radius: 2px; outline: none; margin-bottom: 5px;
}
.custom-slider::-webkit-slider-thumb {
  -webkit-appearance: none; appearance: none; width: 16px; height: 16px; border-radius: 50%; background: #58a6ff; cursor: pointer; transition: transform 0.1s;
}
.custom-slider::-webkit-slider-thumb:hover { transform: scale(1.2); }
.percent-slider::-webkit-slider-thumb { background: #f85149; }

.percentage-marks { display: flex; justify-content: space-between; padding-top: 5px; }
.percentage-marks span { color: #8b949e; font-size: 11px; cursor: pointer; transition: color 0.2s; }
.percentage-marks span:hover { color: #e6edf3; }

/* 预估看板 */
.order-summary { padding: 10px 0; border-top: 1px dashed #30363d; border-bottom: 1px dashed #30363d; display: flex; flex-direction: column; gap: 5px; }
.summary-row { display: flex; justify-content: space-between; color: #8b949e; }
.summary-row span:last-child { color: #e6edf3; font-weight: bold; font-family: monospace; }

/* 按钮样式 */
.action-buttons { display: flex; gap: 10px; margin-top: auto; padding-top: 10px; }
.action-buttons button { flex: 1; padding: 12px 0; border: none; border-radius: 4px; font-weight: bold; cursor: pointer; transition: opacity 0.2s, filter 0.2s; color: white; }
.btn-buy { background: #2ea043; }
.btn-sell { background: #f85149; }
.action-buttons button:hover { filter: brightness(1.1); }
.action-buttons button:active { filter: brightness(0.9); }
.action-buttons button:disabled { opacity: 0.5; cursor: not-allowed; filter: none; }
.action-buttons button.loading { animation: pulse 1.5s infinite; }

@keyframes pulse {
  0% { opacity: 0.7; }
  50% { opacity: 1; }
  100% { opacity: 0.7; }
}
</style>
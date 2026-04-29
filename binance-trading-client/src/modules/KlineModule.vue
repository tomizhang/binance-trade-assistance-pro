<template>
  <div class="kline-module" @click="takeFocus">
    <div class="kline-toolbar">
      <div class="intervals">
        <button 
          v-for="tf in timeframes" 
          :key="tf" 
          :class="{ active: tf === currentTf }" 
          @click="changeInterval(tf)"
        >
          {{ tf }}
        </button>
      </div>

      <div class="actions-group">
        <div v-if="currentPosition" class="position-panel" :class="{ 'in-profit': currentPosition.pnl >= 0, 'in-loss': currentPosition.pnl < 0 }">
          <span class="pos-direction" :class="currentPosition.side === 'LONG' ? 'text-up' : 'text-down'">
            {{ currentPosition.side === 'LONG' ? '↗ 多' : '↘ 空' }} 
          </span>
          <span class="pos-amount">{{ Math.abs(currentPosition.amount) }}</span>
          <span class="divider">|</span>
          <span class="pos-val">{{ currentPosition.entryPrice.toFixed(getPrecisionConfig().precision) }}</span>
          <span class="divider">|</span>
          <span class="pos-pnl" :class="currentPosition.pnl >= 0 ? 'text-up' : 'text-down'">
            {{ currentPosition.pnl >= 0 ? '+' : ''}}{{ currentPosition.pnl.toFixed(2) }}
          </span>
          
          <button 
            class="close-pos-btn btn-75" 
            :disabled="isClosing" 
            @click="closePosition(75)" 
            title="以市价平掉 75% 的仓位"
          >
            {{ isClosing ? '...' : '平75%' }}
          </button>
          <button 
            class="close-pos-btn" 
            :disabled="isClosing" 
            @click="closePosition(100)" 
            title="市价平掉当前标的所有仓位"
          >
            {{ isClosing ? '...' : '市价全平' }}
          </button>
        </div>
      </div>
    </div>
    
    <div class="kline-sub-toolbar">
      <div class="drawing-tools">
        <div class="chart-type-selector">
          <button :class="{ active: localChartType === 'standard' }" @click="changeChartType('standard')">标准</button>
          <button :class="{ active: localChartType === 'heikinAshi' }" @click="changeChartType('heikinAshi')">平均(HA)</button>
        </div>
        
        <span class="divider">|</span>
        
        <select class="draw-select" v-model="currentDrawMode" @change="onDrawModeChange" title="选择画线工具">
          <option value="none">🖱️ 拖拽/选择</option>
          <option value="hline">➖ 水平线</option>
          <option value="trend">📏 趋势线</option>
          <option value="ray">↗️ 射线</option>
          <option value="angle">📐 角度线</option>
          <option value="channel">⏸️ 平行通道</option>
          <option value="alert_ray">🔔 提醒射线</option>
        </select>
        
        <button 
          v-if="customShapes.length > 0"
          class="sync-btn clear-btn" 
          @click="clearAllShapes(true)" 
          title="清除所有画线"
        >
          🗑️
        </button>

        <span class="divider">|</span>

        <button class="sync-btn" :class="{ active: showVolume }" @click="toggleVolume" title="副图成交量">
          📊 成交量
        </button>

        <span class="divider">|</span>
        
        <button class="sync-btn" :class="{ active: marketStore.isSyncEnabled }" @click="marketStore.toggleSync()" title="跨屏同步">
          🔗 同步
        </button>

        <span class="divider" v-if="activeOpenOrders.length > 0">|</span>
        <button 
          v-if="activeOpenOrders.length > 0"
          class="sync-btn cancel-all-btn" 
          @click="cancelAllOrders" 
          title="一键撤销当前图表币种的所有挂单"
        >
          🗑️ 一键撤单 ({{ activeOpenOrders.length }})
        </button>
      </div>

      <div class="quick-order-pill">
        <label class="qt-checkbox" :class="{ active: isQuickTradeEnabled }">
          <input type="checkbox" v-model="isQuickTradeEnabled" />
          ⚡ 双击下单
        </label>
        <template v-if="isQuickTradeEnabled">
          <span class="qt-divider"></span>
          <span class="qt-balance" title="动态可用余额">可用 {{ marketStore.dynamicUsdtBalance?.toFixed(2) || '0.00' }}</span>
          
          <div class="qt-input-wrapper">
            <input 
              type="number" 
              v-model="quickTradeAmount" 
              class="qt-input" 
              title="固定保证金(U) - 滚轮调节" 
              @wheel.prevent="handleAmountScroll"
            />
            <span class="qt-unit">U</span>
          </div>

          <div class="qt-input-wrapper">
            <input 
              type="number" 
              v-model="localLeverage" 
              class="qt-input" 
              title="当前杠杆倍数 - 滚轮调节" 
              @wheel.prevent="handleLeverageScroll"
            />
            <span class="qt-unit">X</span>
          </div>
          
          <button class="qt-toggle-btn" @click="quickTradeType = quickTradeType === 'MARKET' ? 'LIMIT' : 'MARKET'">
            {{ quickTradeType === 'MARKET' ? '市价' : '限价' }}
          </button>
          <button 
            class="qt-toggle-btn" 
            :class="quickTradeSide === 'BUY' ? 'qt-buy' : 'qt-sell'" 
            @click="quickTradeSide = quickTradeSide === 'BUY' ? 'SELL' : 'BUY'"
          >
            {{ quickTradeSide === 'BUY' ? '做多' : '做空' }}
          </button>
        </template>
      </div>
    </div>

    <div class="chart-wrapper" :class="{ 'is-drawing-mode': currentDrawMode !== 'none' }">
      <div class="toast-container">
        <div v-for="t in notifications" :key="t.id" class="toast-message">
          {{ t.msg }}
        </div>
      </div>

      <div class="chart-legend" v-if="hoverData">
        <span class="legend-time">{{ hoverData.time }}</span>
        <span class="legend-item">开: <span :class="hoverData.colorClass">{{ hoverData.open }}</span></span>
        <span class="legend-item">高: <span :class="hoverData.colorClass">{{ hoverData.high }}</span></span>
        <span class="legend-item">低: <span :class="hoverData.colorClass">{{ hoverData.low }}</span></span>
        <span class="legend-item">收: <span :class="hoverData.colorClass">{{ hoverData.close }}</span></span>
        <span class="legend-item">幅: <span :class="hoverData.colorClass">{{ hoverData.change }}</span></span>
        <span class="legend-item" v-if="showVolume">量: <span class="vol-text">{{ hoverData.vol }}</span></span>
      </div>

      <div class="line-settings-panel" v-if="selectedShapeId && currentDrawMode === 'none'">
        <span class="setting-title">✏️ 编辑图形</span>
        <input type="color" :value="selectedShapeColor" @input="updateShapeColor" title="调整颜色" />
        <button class="action-btn delete-btn" @click="deleteSelectedShape(true)">🗑️ 删除</button>
        <button class="action-btn" @click="deselectShape">❌ 取消</button>
      </div>

      <div class="open-orders-layer" v-if="candleSeries" @click="editingOrderId = null">
        <div 
          v-for="order in activeOpenOrders" 
          :key="order.algoId || order.orderId || order.clientOrderId"
          class="open-order-tag"
          :class="order.side === 'BUY' ? 'tag-buy' : 'tag-sell'"
          :style="{ top: `${order.y}px`, display: order.y === null || order.y < 0 ? 'none' : 'flex' }"
        >
          <div class="tag-content" @click.stop="openEditOrder(order)" title="点击修改金额或价格">
            {{ order.side === 'BUY' ? '买' : '卖' }} {{ order.origQty || order.amount }}
            <span class="edit-icon">✏️</span>
          </div>
          <button class="tag-cancel-btn" @click.stop="cancelSingleOrder(order)" title="直接撤销该单">✕</button>
          
          <div class="edit-popover" v-if="editingOrderId === (order.algoId || order.orderId || order.clientOrderId)" @click.stop>
            <div class="edit-header">修改订单参数</div>
            <div class="edit-row">
              <span>价格</span>
              <input type="number" step="any" v-model="editOrderForm.price" />
            </div>
            <div class="edit-row">
              <span>数量</span>
              <input type="number" step="any" v-model="editOrderForm.qty" />
            </div>
            <div class="edit-actions">
              <button class="btn-confirm" @click="submitEditOrder(order)">确认修改</button>
              <button class="btn-cancel" @click="editingOrderId = null">取消</button>
            </div>
          </div>
        </div>
      </div>

      <svg class="drawing-layer" ref="drawingSvg" style="pointer-events: none;">
        <g v-for="s in svgShapes" :key="s.id">
          <template v-if="s.type === 'trend' && s.pts.length >= 2">
            <line :x1="s.pts[0].x" :y1="s.pts[0].y" :x2="s.pts[1].x" :y2="s.pts[1].y" :stroke="s.color" :stroke-width="s.id === selectedShapeId ? 4 : 2" pointer-events="auto" />
            <circle v-if="s.id === selectedShapeId" :cx="s.pts[0].x" :cy="s.pts[0].y" r="5" fill="white" :stroke="s.color" stroke-width="2"/>
            <circle v-if="s.id === selectedShapeId" :cx="s.pts[1].x" :cy="s.pts[1].y" r="5" fill="white" :stroke="s.color" stroke-width="2"/>
          </template>

          <template v-if="s.type === 'ray' && s.pts.length >= 3">
            <line :x1="s.pts[0].x" :y1="s.pts[0].y" :x2="s.pts[2].x" :y2="s.pts[2].y" :stroke="s.color" :stroke-width="s.id === selectedShapeId ? 4 : 2" pointer-events="auto"/>
            <circle v-if="s.id === selectedShapeId" :cx="s.pts[0].x" :cy="s.pts[0].y" r="5" fill="white" :stroke="s.color" stroke-width="2"/>
            <circle v-if="s.id === selectedShapeId" :cx="s.pts[1].x" :cy="s.pts[1].y" r="5" fill="white" :stroke="s.color" stroke-width="2"/>
          </template>

          <template v-if="s.type === 'alert_ray' && s.pts.length >= 3">
            <line :x1="s.pts[0].x" :y1="s.pts[0].y" :x2="s.pts[2].x" :y2="s.pts[2].y" :stroke="s.triggered ? '#484f58' : s.color" :stroke-width="s.id === selectedShapeId ? 4 : 2" :stroke-dasharray="s.triggered ? 'none' : '6 4'" pointer-events="auto" />
            <circle v-if="s.id === selectedShapeId" :cx="s.pts[0].x" :cy="s.pts[0].y" r="5" fill="white" :stroke="s.triggered ? '#484f58' : s.color" stroke-width="2"/>
            <circle v-if="s.id === selectedShapeId" :cx="s.pts[1].x" :cy="s.pts[1].y" r="5" fill="white" :stroke="s.triggered ? '#484f58' : s.color" stroke-width="2"/>
            <text :x="s.pts[0].x - 15" :y="s.pts[0].y - 10" font-size="14" :opacity="s.triggered ? 0.3 : 1">🔔</text>
          </template>

          <template v-if="s.type === 'angle' && s.pts.length >= 2">
            <line :x1="s.pts[0].x" :y1="s.pts[0].y" :x2="s.pts[1].x" :y2="s.pts[1].y" :stroke="s.color" :stroke-width="s.id === selectedShapeId ? 4 : 2" pointer-events="auto" />
            <circle v-if="s.id === selectedShapeId" :cx="s.pts[0].x" :cy="s.pts[0].y" r="5" fill="white" :stroke="s.color" stroke-width="2"/>
            <circle v-if="s.id === selectedShapeId" :cx="s.pts[1].x" :cy="s.pts[1].y" r="5" fill="white" :stroke="s.color" stroke-width="2"/>
            <line v-if="s.id === selectedShapeId" :x1="s.pts[0].x" :y1="s.pts[0].y" :x2="s.pts[0].x + 100" :y2="s.pts[0].y" :stroke="s.color" stroke-dasharray="4 4" stroke-width="1" />
            <rect :x="s.pts[1].x + 10" :y="s.pts[1].y - 12" width="55" height="24" fill="rgba(22,27,34,0.9)" rx="4" border="1px solid #30363d"/>
            <text :x="s.pts[1].x + 37" :y="s.pts[1].y + 4" fill="#c9d1d9" font-size="12" font-family="Arial" text-anchor="middle">{{ s.angleStr }}</text>
          </template>
          
          <template v-if="s.type === 'channel' && s.pts.length >= 4">
            <polygon :points="`${s.pts[0].x},${s.pts[0].y} ${s.pts[1].x},${s.pts[1].y} ${s.pts[3].x},${s.pts[3].y} ${s.pts[2].x},${s.pts[2].y}`" :fill="s.color" fill-opacity="0.15" pointer-events="auto" />
            <line :x1="s.pts[0].x" :y1="s.pts[0].y" :x2="s.pts[1].x" :y2="s.pts[1].y" :stroke="s.color" :stroke-width="s.id === selectedShapeId ? 3 : 2" />
            <line :x1="s.pts[2].x" :y1="s.pts[2].y" :x2="s.pts[3].x" :y2="s.pts[3].y" :stroke="s.color" :stroke-width="s.id === selectedShapeId ? 3 : 2" :stroke-dasharray="s.id === selectedShapeId ? 'none' : '4 4'" />
            <line v-if="s.id === selectedShapeId" :x1="s.pts[0].x" :y1="s.pts[0].y" :x2="s.pts[2].x" :y2="s.pts[2].y" :stroke="s.color" stroke-width="1" stroke-dasharray="2 2" />
          </template>
          
          <template v-if="s.type === 'hline' && s.pts.length >= 1">
            <line x1="0" :y1="s.pts[0].y" :x2="containerWidth" :y2="s.pts[0].y" :stroke="s.color" :stroke-width="s.id === selectedShapeId ? 3 : 2" pointer-events="auto" />
            <rect v-if="s.id === selectedShapeId" x="10" :y="s.pts[0].y - 12" width="60" height="24" fill="rgba(22,27,34,0.9)" rx="4" border="1px solid #30363d"/>
            <text v-if="s.id === selectedShapeId" x="40" :y="s.pts[0].y + 4" fill="#c9d1d9" font-size="12" font-family="Arial" text-anchor="middle">{{ s.points[0].price.toFixed(getPrecisionConfig().precision) }}</text>
          </template>
        </g>

        <g v-if="isDraggingPosition && dragPositionPrice !== null">
          <line 
            x1="0" :y1="dragPositionY" 
            :x2="containerWidth" :y2="dragPositionY" 
            :stroke="dragPositionType === 'TP' ? '#2ea043' : '#f85149'" 
            stroke-width="2" stroke-dasharray="4 4" 
          />
          <rect 
            :x="containerWidth - 140" :y="dragPositionY - 24" 
            width="140" height="24" 
            :fill="dragPositionType === 'TP' ? '#2ea043' : '#f85149'" 
            rx="4" 
          />
          <text 
            :x="containerWidth - 70" :y="dragPositionY - 8" 
            fill="white" font-size="12" font-weight="bold" text-anchor="middle"
          >
            {{ dragPositionType === 'TP' ? '止盈' : '止损' }} | {{ dragPositionPnl >= 0 ? '+' : ''}}{{ dragPositionPnl.toFixed(2) }}
          </text>
          <text 
            :x="40" :y="dragPositionY - 8" 
            :fill="dragPositionType === 'TP' ? '#2ea043' : '#f85149'" 
            font-size="12" font-weight="bold"
          >
            松手挂单: {{ dragPositionPrice.toFixed(getPrecisionConfig().precision) }}
          </text>
        </g>
      </svg>

      <div 
        class="chart-container no-drag" 
        :class="{ 
          'is-resizing': isHoveringShape || draggingShapeId,
          'is-hovering-position': isHoveringPositionLine || isDraggingPosition 
        }"
        ref="chartContainer"
        @pointerdown.stop="onPointerDown"
        @pointermove="onPointerMove"
        @pointerup="onPointerUp"
        @pointerleave="onPointerUp"
        @wheel.stop
        @contextmenu.prevent
      ></div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted, onUnmounted, watch, computed } from 'vue';
import { createChart, CandlestickSeries, HistogramSeries, CrosshairMode, LineStyle, IChartApi } from 'lightweight-charts';
import { useMarketStore } from '@/store/market';
import { MarketAPI } from '@/api/market'; 

const props = defineProps<{ symbol: string }>();

const marketStore = useMarketStore();
const chartContainer = ref<HTMLElement | null>(null);

const instanceId = Math.random().toString(36).substring(2, 10);
const isFocused = computed(() => marketStore.currentSymbol === props.symbol);
const takeFocus = () => { if (!isFocused.value) marketStore.setCurrentSymbol(props.symbol); };

const timeframes = ['1m', '3m', '5m', '15m', '30m', '1h', '2h', '4h', '6h', '8h', '12h', '1d', '3d', '1w', '1M'];
const currentTf = ref('1m'); 

const localChartType = ref('standard'); 
const showVolume = ref(true);
const currentChartData = ref<any[]>([]);

let chart: IChartApi | null = null;
let candleSeries: any = null;
let volumeSeries: any = null;
let resizeObserver: ResizeObserver | null = null;
let positionLineId: any = null;
let breakEvenLineId: any = null; 
let openOrderLines: any[] = []; 

// 🌟 用于渲染可交互 HTML 标签的挂单数组
const activeOpenOrders = ref<any[]>([]);
const editingOrderId = ref<string | null>(null);
const editOrderForm = ref({ price: 0, qty: 0 });

const hoverData = ref<any>(null);
const containerWidth = ref(0);

const notifications = ref<{id: number, msg: string}[]>([]);
let notifIdCounter = 0;
const showNotification = (msg: string) => {
  const id = notifIdCounter++;
  notifications.value.push({ id, msg });
  setTimeout(() => { notifications.value = notifications.value.filter(n => n.id !== id); }, 5000);
};

// ==========================================
// 🌟 新增：挂单的一键撤销、编辑与重发
// ==========================================

// 一键撤销当前标的的所有挂单
const cancelAllOrders = async () => {
  const orders = activeOpenOrders.value;
  if (orders.length === 0) return;
  
  showNotification(`⌛ 正在一键撤销 ${orders.length} 个挂单...`);
  try {
    await Promise.all(orders.map(order => {
      const orderId = order.algoId || order.orderId || order.clientOrderId;
      return fetch(`${import.meta.env.VITE_API_BASE_URL}/api/order/cancel-ws`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ 
          symbol: props.symbol, 
          orderId: orderId.toString(),
          isAlgo: !!order.algoId
        })
      });
    }));
    showNotification(`✅ 一键撤单执行完成`);
    await marketStore.fetchOpenOrders();
  } catch (e: any) {
    showNotification(`❌ 一键撤单遇到错误`);
  }
};

// 撤销单笔挂单 (图表 ✕ 按钮)
const cancelSingleOrder = async (order: any) => {
  const orderId = order.algoId || order.orderId || order.clientOrderId;
  if (!orderId) return;
  try {
    showNotification(`⌛ 正在撤销挂单...`);
    const res = await fetch(`${import.meta.env.VITE_API_BASE_URL}/api/order/cancel-ws`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ symbol: props.symbol, orderId: orderId.toString(), isAlgo: !!order.algoId })
    });
    const data = await res.json();
    if (res.ok && !data.error) {
      showNotification(`✅ 撤单成功`);
      editingOrderId.value = null;
      await marketStore.fetchOpenOrders();
    } else throw new Error(data.error?.msg || '撤单被拒');
  } catch (e: any) {
    showNotification(`❌ 撤单失败: ${e.message}`);
  }
};

// 打开编辑面板，初始化数据
const openEditOrder = (order: any) => {
  const orderId = order.algoId || order.orderId || order.clientOrderId;
  editingOrderId.value = orderId;
  const targetPrice = parseFloat(order.price) > 0 ? parseFloat(order.price) : parseFloat(order.triggerPrice || order.stopPrice);
  
  editOrderForm.value = {
    price: targetPrice,
    qty: parseFloat(order.origQty || order.amount)
  };
};

// 提交编辑 (原子操作：撤销旧单 + 发送新单)
const submitEditOrder = async (order: any) => {
  const oldOrderId = order.algoId || order.orderId || order.clientOrderId;
  const { price, qty } = editOrderForm.value;
  
  if (!price || !qty) return showNotification('❌ 价格和数量必须大于0');
  
  try {
    showNotification(`⌛ 正在修改订单参数...`);
    
    // 1. 发送撤单请求
    await fetch(`${import.meta.env.VITE_API_BASE_URL}/api/order/cancel-ws`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ symbol: props.symbol, orderId: oldOrderId.toString(), isAlgo: !!order.algoId })
    });

    // 2. 组装新单 Payload
    const isAlgo = ['STOP_MARKET', 'TAKE_PROFIT_MARKET', 'STOP', 'TAKE_PROFIT'].includes(order.type);
    const newPayload: any = {
      symbol: props.symbol,
      side: order.side,
      type: order.type,
      quantity: qty,
      reduceOnly: order.reduceOnly || false
    };
    
    if (isAlgo) {
      newPayload.stopPrice = price; // 后端 Service 负责转化为 triggerPrice
      if (order.type === 'STOP' || order.type === 'TAKE_PROFIT') {
        newPayload.price = price; // 限价止损需要价格
      }
    } else {
      newPayload.price = price;
    }

    // 3. 发送新单请求
    const res = await fetch(`${import.meta.env.VITE_API_BASE_URL}/api/order/place-ws`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(newPayload)
    });
    
    const data = await res.json();
    if (!res.ok || data.error) throw new Error(data.error?.msg || '新参数下单被拒');

    showNotification(`✅ 订单修改成功`);
    editingOrderId.value = null;
    await marketStore.fetchOpenOrders();
  } catch (e: any) {
    showNotification(`❌ 修改失败: ${e.message}`);
  }
};


// ==========================================
// 1. 快捷双击下单状态与逻辑
// ==========================================
const isQuickTradeEnabled = ref(true);
const quickTradeAmount = ref(5); 
const localLeverage = ref(20); 
const quickTradeType = ref<'MARKET' | 'LIMIT'>('MARKET');
const quickTradeSide = ref<'BUY' | 'SELL'>('BUY');
const isPlacingOrder = ref(false);

watch(() => marketStore.symbolConfigs[props.symbol], (config) => {
  if (config && config.leverage) {
    localLeverage.value = config.leverage;
  }
}, { immediate: true, deep: true });

const handleAmountScroll = (e: WheelEvent) => {
  const step = 5; 
  const maxBalance = Math.floor(marketStore.dynamicUsdtBalance || 0);
  if (e.deltaY < 0) {
    let nextVal = quickTradeAmount.value + step;
    if (maxBalance > 0 && nextVal > maxBalance) nextVal = maxBalance;
    quickTradeAmount.value = Math.max(1, nextVal);
  } else {
    quickTradeAmount.value = Math.max(1, quickTradeAmount.value - step);
  }
};

let leverageTimer: any = null;
const handleLeverageScroll = (e: WheelEvent) => {
  const step = 1;
  if (e.deltaY < 0) {
    localLeverage.value = Math.min(125, localLeverage.value + step);
  } else {
    localLeverage.value = Math.max(1, localLeverage.value - step);
  }

  if (leverageTimer) clearTimeout(leverageTimer);
  leverageTimer = setTimeout(async () => {
    try {
      const res = await fetch(`${import.meta.env.VITE_API_BASE_URL}/api/account/leverage`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ symbol: props.symbol, leverage: localLeverage.value })
      });
      if (!res.ok) throw new Error();
      showNotification(`⚙️ [${props.symbol}] 杠杆已同步为 ${localLeverage.value}x`);
    } catch (e) {
      showNotification(`❌ 杠杆同步失败，已回退`);
      localLeverage.value = marketStore.symbolConfigs[props.symbol]?.leverage || 20;
    }
  }, 800);
};

let lastOrderTime = 0;
const executeQuickTrade = async (clickedPrice: number) => {
  const now = Date.now();
  if (now - lastOrderTime < 1000) return; 
  if (isPlacingOrder.value) return;

  const currentPrice = marketStore.marketTickers[props.symbol]?.lastPrice || clickedPrice;
  const calcPrice = quickTradeType.value === 'MARKET' ? currentPrice : clickedPrice;
  const rule = marketStore.symbolRules[props.symbol] || { stepSize: '0.001', tickSize: '0.1' };

  const notionalValue = quickTradeAmount.value * localLeverage.value;
  let rawQuantity = notionalValue / calcPrice;
  let formattedQtyStr = formatByStep(rawQuantity, rule.stepSize);
  let quantity = parseFloat(formattedQtyStr);

  if (quantity * calcPrice < 5.01) {
    const minRequiredQty = 5.01 / calcPrice;
    const step = parseFloat(rule.stepSize);
    quantity = Math.ceil(minRequiredQty / step) * step;
    quantity = parseFloat(quantity.toFixed(step.toString().includes('.') ? step.toString().split('.')[1].length : 0));
    
    if ((quantity * calcPrice) / localLeverage.value > marketStore.dynamicUsdtBalance) {
       showNotification(`❌ 余额不足以满足最低限制(5U)`);
       return;
    }
    showNotification(`⚠️ 已自动补足至最低要求(约5U)`);
  }

  const formattedPrice = parseFloat(formatByStep(clickedPrice, rule.tickSize));

  try {
    lastOrderTime = Date.now();
    isPlacingOrder.value = true;
    showNotification(`⚡ [狙击指令] 准备${quickTradeType.value === 'MARKET' ? '市价' : '限价'}${quickTradeSide.value === 'BUY' ? '做多' : '做空'}...`);
    
    const res = await fetch(`${import.meta.env.VITE_API_BASE_URL}/api/order/place-ws`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        symbol: props.symbol,
        side: quickTradeSide.value,
        type: quickTradeType.value,
        quantity: quantity,
        price: quickTradeType.value === 'LIMIT' ? formattedPrice : null
      })
    });
    
    const data = await res.json();
    if (!res.ok || data.error) throw new Error(data.error?.msg || '下单被拒');
    
    showNotification(`✅ [快捷下单成功] ${quickTradeSide.value === 'BUY' ? '做多' : '做空'} ${quantity} 个`);
    await marketStore.fetchOpenOrders();
  } catch(e: any) {
    showNotification(`❌ 快捷下单失败: ${e.message}`);
  } finally {
    isPlacingOrder.value = false;
  }
};


// ==========================================
// 2. 平仓逻辑
// ==========================================
const isClosing = ref(false);
let lastCloseTime = 0;

const formatByStep = (value: number, stepStr: string) => {
  const step = parseFloat(stepStr);
  if (isNaN(step) || step <= 0) return value.toString();
  let dec = 0;
  if (step < 1) {
    const stepStrParsed = step.toString();
    if (stepStrParsed.includes('e-')) {
      dec = parseInt(stepStrParsed.split('e-')[1], 10);
    } else if (stepStrParsed.includes('.')) {
      dec = stepStrParsed.split('.')[1].length;
    }
  }
  const truncated = Math.floor(value / step + Number.EPSILON) * step;
  return truncated.toFixed(dec);
};

const closePosition = async (percent: number) => {
  const now = Date.now();
  if (now - lastCloseTime < 1000) return; 
  
  const pos = currentPosition.value;
  if (!pos || isClosing.value) return;

  const amountToClose = Math.abs(pos.amount) * (percent / 100);
  if (amountToClose <= 0) return;

  const rule = marketStore.symbolRules[props.symbol] || { stepSize: '0.001' };
  const formattedQtyStr = formatByStep(amountToClose, rule.stepSize);
  const quantity = parseFloat(formattedQtyStr);
  
  if (quantity <= 0) return showNotification(`[警告] 平仓数量过小`);

  const side = pos.side === 'LONG' ? 'SELL' : 'BUY';

  try {
    lastCloseTime = Date.now();
    isClosing.value = true;
    const res = await fetch(`${import.meta.env.VITE_API_BASE_URL}/api/order/place-ws`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ symbol: props.symbol, side: side, type: 'MARKET', quantity: quantity })
    });
    
    const data = await res.json();
    if (!res.ok || data.error) throw new Error(data.error?.msg || '平仓失败');
    showNotification(`✅ [${props.symbol}] ${percent}% 平仓成功!`);
    await marketStore.fetchOpenOrders();
  } catch(e: any) {
    showNotification(`❌ 平仓失败: ${e.message}`);
  } finally {
    isClosing.value = false;
  }
};


// ==========================================
// 3. 仓位/挂单渲染引擎
// ==========================================
const getCurrentPrice = () => marketStore.marketTickers[props.symbol]?.lastPrice || 0;

const updatePositionLines = (currentPriceOverride?: number) => {
  if (!candleSeries) return;
  const pos = marketStore.positions.find(p => p.symbol === props.symbol);
  
  if (!pos) {
    if (positionLineId) { candleSeries.removePriceLine(positionLineId); positionLineId = null; }
    if (breakEvenLineId) { candleSeries.removePriceLine(breakEvenLineId); breakEvenLineId = null; }
    return;
  }

  const currentPrice = currentPriceOverride || getCurrentPrice() || pos.entryPrice;
  const pnl = pos.side === 'LONG' ? (currentPrice - pos.entryPrice) * Math.abs(pos.amount) : (pos.entryPrice - currentPrice) * Math.abs(pos.amount);
  
  const positionLineOptions = {
    price: pos.entryPrice, 
    color: pnl >= 0 ? '#2ea043' : '#f85149', 
    lineWidth: 2 as any, 
    lineStyle: LineStyle.Dashed, 
    axisLabelVisible: true,
    title: `${pos.side === 'LONG' ? '多' : '空'} ${Math.abs(pos.amount)} | ${pnl >= 0 ? '+' : ''}${pnl.toFixed(2)}`,
  };

  if (!positionLineId) { positionLineId = candleSeries.createPriceLine(positionLineOptions); } 
  else { positionLineId.applyOptions(positionLineOptions); }

  const FEE_RATE = 0.0005;
  const breakEvenPrice = pos.side === 'LONG'
    ? pos.entryPrice * (1 + FEE_RATE) / (1 - FEE_RATE)
    : pos.entryPrice * (1 - FEE_RATE) / (1 + FEE_RATE);

  const breakEvenOptions = {
    price: breakEvenPrice, color: '#d29922', lineWidth: 1 as any, lineStyle: LineStyle.Solid, axisLabelVisible: true, title: '保本价',
  };

  if (!breakEvenLineId) { breakEvenLineId = candleSeries.createPriceLine(breakEvenOptions); } 
  else { breakEvenLineId.applyOptions(breakEvenOptions); }
};

const updateOpenOrderLines = () => {
  if (!candleSeries) return;
  
  openOrderLines.forEach(line => { try { candleSeries.removePriceLine(line); } catch (e) {} });
  openOrderLines = [];

  const orders = marketStore.openOrders?.filter((o: any) => o.symbol === props.symbol) || [];
  
  // 更新到 HTML 响应式数组，渲染交互标签
  activeOpenOrders.value = orders.map((o: any) => ({ ...o, y: -999 })); 
  
  orders.forEach((order: any) => {
    const targetPrice = parseFloat(order.price) > 0 ? parseFloat(order.price) : parseFloat(order.triggerPrice || order.stopPrice);
    if (!targetPrice) return;

    const line = candleSeries.createPriceLine({
      price: targetPrice,
      color: order.side === 'BUY' ? '#2ea043' : '#f85149',
      lineWidth: 1 as any,
      lineStyle: LineStyle.Dotted, 
      axisLabelVisible: true,
      title: '' // 清空 Title，把视觉展示完全交给我们的 HTML 图层
    });
    openOrderLines.push(line);
  });
};

const currentPosition = computed(() => {
  const pos = marketStore.positions.find(p => p.symbol === props.symbol);
  if (!pos) return null;
  const currentPrice = getCurrentPrice() || pos.entryPrice;
  const pnl = pos.side === 'LONG' 
    ? (currentPrice - pos.entryPrice) * Math.abs(pos.amount)
    : (pos.entryPrice - currentPrice) * Math.abs(pos.amount);
  return { ...pos, currentPrice, pnl };
});

const getPrecisionConfig = (lastPrice?: number) => {
  const rule = marketStore.symbolRules[props.symbol];
  if (rule && rule.tickSize) {
    const minM = parseFloat(rule.tickSize);
    let dec = 2;
    if (minM < 1) {
      const str = minM.toString();
      if (str.includes('e')) {
        const match = str.match(/e-(\d+)/);
        if (match) dec = parseInt(match[1], 10);
      } else { dec = str.split('.')[1]?.length || 2; }
    } else { dec = 0; }
    return { precision: dec, minMove: minM };
  }
  return { precision: 2, minMove: 0.01 };
};

watch(() => marketStore.symbolRules[props.symbol], (rule) => {
  if (rule && candleSeries) {
    const config = getPrecisionConfig();
    candleSeries.applyOptions({ priceFormat: { type: 'price', precision: config.precision, minMove: config.minMove } });
  }
}, { deep: true });


// ==========================================
// 4. 绘图引擎与数据逻辑 (含持仓拖拽止盈止损)
// ==========================================
const currentDrawMode = ref('none'); 
const drawStep = ref(0);
type LogicPoint = { logical: number, price: number };
type Shape = { id: string, type: string, points: LogicPoint[], color: string, triggered?: boolean };
const customShapes = ref<Shape[]>([]);
const svgShapes = ref<any[]>([]); 
const selectedShapeId = ref<string | null>(null);
const draggingShapeId = ref<string | null>(null);

const isHoveringShape = ref(false);
const isHoveringPositionLine = ref(false);
const isDraggingPosition = ref(false);
const dragPositionY = ref(0);
const dragPositionPrice = ref<number | null>(null);
const dragPositionType = ref<'TP' | 'SL'>('TP');
const dragPositionPnl = ref(0);

let dragOffsets: { dl: number, dp: number }[] = [];
const selectedShapeColor = computed(() => {
  const shape = customShapes.value.find(s => s.id === selectedShapeId.value);
  return shape ? shape.color : '#58a6ff';
});
const onDrawModeChange = () => { drawStep.value = 0; if (currentDrawMode.value !== 'none') deselectShape(); };
const deselectShape = () => { selectedShapeId.value = null; };

const placeDragOrder = async (targetPrice: number, tpSlType: 'TP' | 'SL') => {
  const pos = currentPosition.value;
  if (!pos) return;
  const side = pos.side === 'LONG' ? 'SELL' : 'BUY';
  const rule = marketStore.symbolRules[props.symbol] || { stepSize: '0.001', tickSize: '0.1' };
  const formattedPrice = parseFloat(formatByStep(targetPrice, rule.tickSize));
  const quantity = Math.abs(pos.amount);

  const orderType = tpSlType === 'TP' ? 'LIMIT' : 'STOP_MARKET';
  const payload: any = { symbol: props.symbol, side: side, type: orderType, quantity: quantity, reduceOnly: true };
  
  if (orderType === 'LIMIT') payload.price = formattedPrice;
  if (orderType === 'STOP_MARKET') payload.stopPrice = formattedPrice;

  try {
      showNotification(`⌛ 正在挂载 ${tpSlType === 'TP' ? '止盈' : '止损'} 订单...`);
      const res = await fetch(`${import.meta.env.VITE_API_BASE_URL}/api/order/place-ws`, {
          method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload)
      });
      const data = await res.json();
      if (!res.ok || data.error) throw new Error(data.error?.msg || '挂单失败');
      showNotification(`✅ 成功设置 ${tpSlType === 'TP' ? '止盈' : '止损'} 挂单`);
      await marketStore.fetchOpenOrders();
  } catch(e: any) {
      showNotification(`❌ 设置 ${tpSlType === 'TP' ? '止盈' : '止损'} 失败: ${e.message}`);
  }
};


let activeShapeIdForDraw: string | null = null;

const onPointerDown = (e: PointerEvent) => {
  if (!chart || !candleSeries || !chartContainer.value) return;
  const rect = chartContainer.value.getBoundingClientRect();
  const x = e.clientX - rect.left; const y = e.clientY - rect.top;
  const logical = chart.timeScale().coordinateToLogical(x as any);
  const price = candleSeries.coordinateToPrice(y);
  if (logical === null || price === null) return;

  if (currentDrawMode.value === 'none' && isHoveringPositionLine.value && !isHoveringShape.value) {
      isDraggingPosition.value = true;
      chart.applyOptions({ handleScroll: false, handleScale: false });
      return;
  }

  if (currentDrawMode.value !== 'none') {
    if (currentDrawMode.value === 'hline') {
      const newId = Math.random().toString(36).substring(2, 10);
      const newShape = { id: newId, type: 'hline', points: [{ logical, price }], color: '#58a6ff' };
      customShapes.value.push(newShape);
      broadcastSync({ action: 'add', shape: newShape });
      currentDrawMode.value = 'none';
    } else if (['trend', 'ray', 'angle', 'alert_ray'].includes(currentDrawMode.value)) {
      if (drawStep.value === 0) {
        const newId = Math.random().toString(36).substring(2, 10);
        const defaultColor = currentDrawMode.value === 'alert_ray' ? '#ff9800' : '#58a6ff';
        customShapes.value.push({ id: newId, type: currentDrawMode.value, points: [{logical, price}, {logical, price}], color: defaultColor, triggered: false });
        activeShapeIdForDraw = newId; drawStep.value = 1;
      } else if (drawStep.value === 1) {
        drawStep.value = 0; currentDrawMode.value = 'none';
        broadcastSync({ action: 'add', shape: customShapes.value.find(s => s.id === activeShapeIdForDraw) });
      }
    } else if (currentDrawMode.value === 'channel') {
      if (drawStep.value === 0) {
        const newId = Math.random().toString(36).substring(2, 10);
        customShapes.value.push({ id: newId, type: 'channel', points: [{logical, price}, {logical, price}, {logical, price}], color: '#58a6ff' });
        activeShapeIdForDraw = newId; drawStep.value = 1;
      } else if (drawStep.value === 1) { drawStep.value = 2; } 
      else if (drawStep.value === 2) {
        drawStep.value = 0; currentDrawMode.value = 'none';
        broadcastSync({ action: 'add', shape: customShapes.value.find(s => s.id === activeShapeIdForDraw) });
      }
    }
    return;
  }

  let hitId = null;
  for (let i = svgShapes.value.length - 1; i >= 0; i--) {
    const s = svgShapes.value[i]; let isHit = false;
    if (s.type === 'hline') { isHit = Math.abs(s.pts[0].y - y) < 10; } 
    else if (s.type === 'trend' || s.type === 'angle') { isHit = distToSegment(x, y, s.pts[0].x, s.pts[0].y, s.pts[1].x, s.pts[1].y) < 10; } 
    else if (s.type === 'ray' || s.type === 'alert_ray') { isHit = distToRay(x, y, s.pts[0].x, s.pts[0].y, s.pts[1].x, s.pts[1].y) < 10; } 
    else if (s.type === 'channel') {
      isHit = distToSegment(x, y, s.pts[0].x, s.pts[0].y, s.pts[1].x, s.pts[1].y) < 10;
      if (!isHit) isHit = distToSegment(x, y, s.pts[2].x, s.pts[2].y, s.pts[3].x, s.pts[3].y) < 10;
    }
    if (isHit) { hitId = s.id; break; }
  }

  if (hitId) {
    selectedShapeId.value = hitId; draggingShapeId.value = hitId;
    chart.applyOptions({ handleScroll: false, handleScale: false }); 
    const shape = customShapes.value.find(s => s.id === hitId)!;
    dragOffsets = shape.points.map(p => ({ dl: p.logical - logical, dp: p.price - price }));
  } else { deselectShape(); }
};

const onPointerMove = (e: PointerEvent) => {
  if (!chart || !candleSeries || !chartContainer.value) return;
  const rect = chartContainer.value.getBoundingClientRect();
  const x = e.clientX - rect.left; const y = e.clientY - rect.top;
  const logical = chart.timeScale().coordinateToLogical(x as any);
  const price = candleSeries.coordinateToPrice(y);
  if (logical === null || price === null) return;

  if (isDraggingPosition.value && currentPosition.value) {
      dragPositionPrice.value = price;
      dragPositionY.value = y;
      const isLong = currentPosition.value.side === 'LONG';
      if (isLong) {
          dragPositionType.value = price > currentPosition.value.entryPrice ? 'TP' : 'SL';
          dragPositionPnl.value = (price - currentPosition.value.entryPrice) * Math.abs(currentPosition.value.amount);
      } else {
          dragPositionType.value = price < currentPosition.value.entryPrice ? 'TP' : 'SL';
          dragPositionPnl.value = (currentPosition.value.entryPrice - price) * Math.abs(currentPosition.value.amount);
      }
      return;
  }

  if (currentDrawMode.value !== 'none' && drawStep.value > 0 && activeShapeIdForDraw) {
    const shape = customShapes.value.find(s => s.id === activeShapeIdForDraw);
    if (shape) {
      if (['trend', 'ray', 'alert_ray', 'angle'].includes(shape.type) && drawStep.value === 1) { shape.points[1] = { logical, price }; } 
      else if (shape.type === 'channel') {
        if (drawStep.value === 1) { shape.points[1] = { logical, price }; shape.points[2] = { logical, price }; } 
        else if (drawStep.value === 2) { shape.points[2] = { logical, price }; }
      }
    }
    return;
  }

  if (draggingShapeId.value) {
    const shape = customShapes.value.find(s => s.id === draggingShapeId.value);
    if (shape) {
      shape.points.forEach((p, idx) => { p.logical = logical + dragOffsets[idx].dl; p.price = price + dragOffsets[idx].dp; });
      if (shape.type === 'alert_ray') { shape.triggered = false; }
    }
    return;
  }

  let hit = false;
  for (const s of svgShapes.value) {
    if (s.type === 'hline' && Math.abs(s.pts[0].y - y) < 10) hit = true;
    else if (s.type === 'trend' || s.type === 'angle') { if (distToSegment(x, y, s.pts[0].x, s.pts[0].y, s.pts[1].x, s.pts[1].y) < 10) hit = true; } 
    else if (s.type === 'ray' || s.type === 'alert_ray') { if (distToRay(x, y, s.pts[0].x, s.pts[0].y, s.pts[1].x, s.pts[1].y) < 10) hit = true; } 
    else if (s.type === 'channel') {
      if (distToSegment(x, y, s.pts[0].x, s.pts[0].y, s.pts[1].x, s.pts[1].y) < 10) hit = true;
      if (!hit && distToSegment(x, y, s.pts[2].x, s.pts[2].y, s.pts[3].x, s.pts[3].y) < 10) hit = true;
    }
    if (hit) break;
  }
  isHoveringShape.value = hit;

  if (!hit && currentDrawMode.value === 'none' && !draggingShapeId.value && currentPosition.value && candleSeries) {
      const posY = candleSeries.priceToCoordinate(currentPosition.value.entryPrice);
      if (posY !== null && Math.abs(y - posY) < 10) { isHoveringPositionLine.value = true; } 
      else { isHoveringPositionLine.value = false; }
  } else { isHoveringPositionLine.value = false; }
};

const onPointerUp = () => {
  if (isDraggingPosition.value) {
      isDraggingPosition.value = false;
      chart?.applyOptions({ handleScroll: true, handleScale: true });
      if (dragPositionPrice.value !== null) placeDragOrder(dragPositionPrice.value, dragPositionType.value);
      dragPositionPrice.value = null;
      return;
  }

  if (draggingShapeId.value) {
    chart?.applyOptions({ handleScroll: true, handleScale: true }); 
    broadcastSync({ action: 'move', shape: customShapes.value.find(s => s.id === draggingShapeId.value) });
    draggingShapeId.value = null;
  }
};

let animationFrameId: number;
const renderSvgLoop = () => {
  if (chart && candleSeries && chartContainer.value) {
    containerWidth.value = chartContainer.value.clientWidth;
    const mapped = [];
    for (const shape of customShapes.value) {
      const pts = shape.points.map(p => {
        const x = chart!.timeScale().logicalToCoordinate(p.logical as any);
        const y = candleSeries.priceToCoordinate(p.price);
        return { x: x as any, y: y as any };
      });
      if (shape.type === 'channel' && pts.length >= 3 && pts[0].x !== null && pts[1].x !== null && pts[2].x !== null) { pts[3] = { x: pts[2].x + (pts[1].x - pts[0].x) as any, y: pts[2].y + (pts[1].y - pts[0].y) }; }
      if ((shape.type === 'ray' || shape.type === 'alert_ray') && pts.length >= 2 && pts[0].x !== null && pts[1].x !== null) {
        const dx = pts[1].x - pts[0].x; const dy = pts[1].y - pts[0].y;
        if (dx !== 0 || dy !== 0) { pts[2] = { x: pts[1].x + dx * 10000 as any, y: pts[1].y + dy * 10000 }; } else { pts[2] = { ...pts[1] }; }
      }
      let angleStr = '';
      if (shape.type === 'angle' && pts.length >= 2 && pts[0].x !== null && pts[1].x !== null) {
        const dx = pts[1].x - pts[0].x; const dy = pts[1].y - pts[0].y; 
        angleStr = (Math.atan2(-dy, dx) * (180 / Math.PI)).toFixed(1) + '°';
      }
      if (pts.every(p => p.x !== null && p.y !== null)) { mapped.push({ ...shape, pts, angleStr }); }
    }
    svgShapes.value = mapped;

    // 🌟 计算所有挂单在当前 K 线图上的实际 Y 坐标 (跟随缩放滑动实时更新)
    activeOpenOrders.value.forEach(o => {
      const targetPrice = parseFloat(o.price) > 0 ? parseFloat(o.price) : parseFloat(o.triggerPrice || o.stopPrice);
      if (targetPrice > 0) {
        o.y = candleSeries.priceToCoordinate(targetPrice);
      }
    });
  }
  animationFrameId = requestAnimationFrame(renderSvgLoop);
};

const distToSegment = (px: number, py: number, x1: number, y1: number, x2: number, y2: number) => {
  const l2 = (x1 - x2)**2 + (y1 - y2)**2; if (l2 === 0) return Math.sqrt((px - x1)**2 + (py - y1)**2);
  let t = ((px - x1) * (x2 - x1) + (py - y1) * (y2 - y1)) / l2; t = Math.max(0, Math.min(1, t)); 
  return Math.sqrt((px - (x1 + t * (x2 - x1)))**2 + (py - (y1 + t * (y2 - y1)))**2);
};

const distToRay = (px: number, py: number, x1: number, y1: number, x2: number, y2: number) => {
  const l2 = (x1 - x2)**2 + (y1 - y2)**2; if (l2 === 0) return Math.sqrt((px - x1)**2 + (py - y1)**2);
  let t = ((px - x1) * (x2 - x1) + (py - y1) * (y2 - y1)) / l2; t = Math.max(0, t); 
  return Math.sqrt((px - (x1 + t * (x2 - x1)))**2 + (py - (y1 + t * (y2 - y1)))**2);
};

const broadcastSync = (detail: any) => {
  if (marketStore.isSyncEnabled) { window.dispatchEvent(new CustomEvent('sync-drawing', { detail: { ...detail, symbol: props.symbol, sourceId: instanceId } })); }
};

const updateShapeColor = (e: Event) => {
  const newColor = (e.target as HTMLInputElement).value;
  const shape = customShapes.value.find(s => s.id === selectedShapeId.value);
  if (shape) { shape.color = newColor; broadcastSync({ action: 'color', id: shape.id, color: newColor }); }
};

const deleteSelectedShape = (isSource = false) => {
  if (selectedShapeId.value) {
    const id = selectedShapeId.value; customShapes.value = customShapes.value.filter(l => l.id !== id);
    if (isSource) broadcastSync({ action: 'delete', id });
    selectedShapeId.value = null;
  }
};

const clearAllShapes = (isSource = false) => {
  customShapes.value = []; selectedShapeId.value = null;
  if (isSource) broadcastSync({ action: 'clear' });
};

const onSyncDrawing = (e: any) => {
  if (!marketStore.isSyncEnabled) return;
  const { action, shape, id, color, symbol, sourceId } = e.detail;
  if (sourceId !== instanceId && symbol === props.symbol) {
    if (action === 'add') customShapes.value.push(JSON.parse(JSON.stringify(shape)));
    else if (action === 'move') {
      const localShape = customShapes.value.find(s => s.id === shape.id);
      if (localShape) { localShape.points = shape.points; if (localShape.type === 'alert_ray') localShape.triggered = false; }
    }
    else if (action === 'color') { const localShape = customShapes.value.find(s => s.id === id); if (localShape) localShape.color = color; }
    else if (action === 'delete') customShapes.value = customShapes.value.filter(l => l.id !== id);
    else if (action === 'clear') clearAllShapes(false);
    else if (action === 'trigger') { const localShape = customShapes.value.find(s => s.id === id); if (localShape) localShape.triggered = true; }
  }
};

const formatDateTime = (timestamp: number) => {
  const date = new Date(timestamp * 1000); 
  const y = date.getFullYear(); const m = String(date.getMonth() + 1).padStart(2, '0'); const d = String(date.getDate()).padStart(2, '0');
  const H = String(date.getHours()).padStart(2, '0'); const M = String(date.getMinutes()).padStart(2, '0'); const S = String(date.getSeconds()).padStart(2, '0');
  return `${y}-${m}-${d} ${H}:${M}:${S}`;
};

const calculateHeikinAshi = (rawData: any[]) => {
  const haData = [];
  let prevHA: any = null;
  for (const raw of rawData) {
    const rawVolume = raw.value !== undefined ? raw.value : (raw.volume !== undefined ? raw.volume : (raw.vol || 0));
    const ha = { 
      time: raw.time, 
      parsedTime: raw.parsedTime,
      open: 0, high: 0, low: 0, close: 0, 
      value: rawVolume, color: raw.color 
    };
    
    ha.close = (Number(raw.open) + Number(raw.high) + Number(raw.low) + Number(raw.close)) / 4;
    if (!prevHA) { ha.open = (Number(raw.open) + Number(raw.close)) / 2; } 
    else { ha.open = (Number(prevHA.open) + Number(prevHA.close)) / 2; }
    ha.high = Math.max(Number(raw.high), ha.open, ha.close);
    ha.low = Math.min(Number(raw.low), ha.open, ha.close);
    ha.color = ha.close >= ha.open ? 'rgba(38, 166, 154, 0.5)' : 'rgba(239, 83, 80, 0.5)';
    
    haData.push(ha);
    prevHA = ha;
  }
  return haData;
};

const applyDataToSeries = (data: any[]) => {
  if (!candleSeries || !volumeSeries || data.length === 0) return;
  
  const normalizedData = data.map(d => {
    const t = Number(d.time);
    return { ...d, parsedTime: t > 9999999999 ? Math.floor(t / 1000) : t };
  }).sort((a, b) => a.parsedTime - b.parsedTime);

  const uniqueData = normalizedData.filter((item, index, self) => index === self.length - 1 || item.parsedTime !== self[index + 1].parsedTime);

  const finalData = localChartType.value === 'heikinAshi' ? calculateHeikinAshi(uniqueData) : uniqueData;
  
  try {
    const lastPrice = finalData[finalData.length - 1]?.close;
    const config = getPrecisionConfig(lastPrice);
    candleSeries.applyOptions({
      priceFormat: { type: 'price', precision: config.precision, minMove: config.minMove }
    });

    candleSeries.setData(finalData.map((d: any) => ({
      time: d.parsedTime !== undefined ? d.parsedTime : d.time, 
      open: Number(d.open), high: Number(d.high), low: Number(d.low), close: Number(d.close)
    })));

    volumeSeries.setData(finalData.map((d: any) => {
      const isUp = Number(d.close) >= Number(d.open);
      return {
        time: d.parsedTime !== undefined ? d.parsedTime : d.time,
        value: Number(d.value !== undefined ? d.value : (d.volume !== undefined ? d.volume : (d.vol || 0))),
        color: d.color || (isUp ? 'rgba(38, 166, 154, 0.5)' : 'rgba(239, 83, 80, 0.5)')
      };
    }));
    
    updatePositionLines();
    updateOpenOrderLines();
  } catch(e) {
    console.error("K线渲染引擎异常:", e);
  }
};

const loadHistory = async (symbol: string, interval: string) => {
  try {
    const history = await MarketAPI.getHistoricalKlines(symbol, interval, 200);
    if (!history || history.length === 0) return;
    currentChartData.value = history;
    applyDataToSeries(currentChartData.value);
  } catch (error) {}
};

let isLoadingMoreHistory = false;
const loadMoreHistory = async () => {
  if (isLoadingMoreHistory || currentChartData.value.length === 0) return;
  isLoadingMoreHistory = true;

  const rawOldest = Number(currentChartData.value[0].time);
  const oldestTimeSec = rawOldest > 9999999999 ? Math.floor(rawOldest / 1000) : rawOldest;
  const targetEndTimeMs = (oldestTimeSec - (parseInt(currentTf.value) * (currentTf.value.endsWith('m') ? 60 : 3600))) * 1000;

  try {
    const olderHistory = await MarketAPI.getHistoricalKlines(props.symbol, currentTf.value, 200, targetEndTimeMs);
    if (olderHistory && olderHistory.length > 0) {
      const safeNewData = olderHistory.filter(( item : any) => {
        const itemTime = Number(item.time) > 9999999999 ? Math.floor(Number(item.time) / 1000) : Number(item.time);
        return itemTime < oldestTimeSec;
      });
      
      if (safeNewData.length > 0) {
        currentChartData.value = [...safeNewData, ...currentChartData.value];
        applyDataToSeries(currentChartData.value);
        const addedCount = safeNewData.length;
        customShapes.value.forEach(shape => shape.points.forEach(p => p.logical += addedCount));
      }
    }
  } catch (e) {
  } finally {
    isLoadingMoreHistory = false;
  }
};

let isSyncingRange = false;
const onSyncRange = (e: any) => {
  if (!chart || !marketStore.isSyncEnabled) return;
  const { range, sourceId, symbol } = e.detail;
  
  if (sourceId !== instanceId && symbol === props.symbol) {
    isSyncingRange = true; 
    chart.timeScale().setVisibleLogicalRange(range);
    setTimeout(() => { isSyncingRange = false; }, 50); 
  }
};

const initCharts = () => {
  if (!chartContainer.value) return;

  chart = createChart(chartContainer.value, {
    layout: { textColor: '#8b949e', background: { type: 'solid' as any, color: '#0d1117' } },
    grid: { vertLines: { color: '#21262d', style: LineStyle.Dotted }, horzLines: { color: '#21262d', style: LineStyle.Dotted } },
    crosshair: { mode: CrosshairMode.Normal, vertLine: { labelBackgroundColor: '#1f6feb' }, horzLine: { labelBackgroundColor: '#1f6feb' } },
    timeScale: { borderColor: '#30363d', timeVisible: true, secondsVisible: true },
    localization: { timeFormatter: (t: any) => typeof t === 'number' ? formatDateTime(t) : String(t) },
    rightPriceScale: { borderColor: '#30363d', scaleMargins: { top: 0.05, bottom: 0.25 } }
  });

  const config = getPrecisionConfig();
  candleSeries = chart.addSeries(CandlestickSeries, { 
    upColor: '#2ea043', downColor: '#f85149', borderVisible: false, wickUpColor: '#2ea043', wickDownColor: '#f85149',
    priceFormat: { type: 'price', precision: config.precision, minMove: config.minMove }
  });
  
  volumeSeries = chart.addSeries(HistogramSeries, { priceFormat: { type: 'volume' }, priceScaleId: '', visible: showVolume.value });
  volumeSeries.priceScale().applyOptions({ scaleMargins: { top: 0.8, bottom: 0 } });

  chart.timeScale().subscribeVisibleLogicalRangeChange((logicalRange) => {
    if (logicalRange && logicalRange.from < 10 && !isLoadingMoreHistory) loadMoreHistory();
    if (marketStore.isSyncEnabled && !isSyncingRange && logicalRange) {
      window.dispatchEvent(new CustomEvent('sync-logical-range', { detail: { range: logicalRange, sourceId: instanceId, symbol: props.symbol } }));
    }
  });

  chart.subscribeCrosshairMove((param) => {
    if (
      param.point === undefined || !param.time ||
      param.point.x < 0 || param.point.x > chartContainer.value!.clientWidth ||
      param.point.y < 0 || param.point.y > chartContainer.value!.clientHeight
    ) {
      hoverData.value = null;
      if (marketStore.isSyncEnabled && marketStore.updateGlobalCrosshair) {
        marketStore.updateGlobalCrosshair(0, 0, props.symbol, instanceId);
      }
      return;
    }

    const candleData: any = param.seriesData.get(candleSeries);
    const volData: any = param.seriesData.get(volumeSeries);

    if (candleData) {
      const isUp = candleData.close >= candleData.open;
      const dec = getPrecisionConfig().precision; 
      
      const changePercent = ((candleData.close - candleData.open) / candleData.open) * 100;
      const changeStr = (changePercent > 0 ? '+' : '') + changePercent.toFixed(2) + '%';

      hoverData.value = {
        time: formatDateTime(Number(param.time)),
        open: candleData.open.toFixed(dec), high: candleData.high.toFixed(dec),
        low: candleData.low.toFixed(dec), close: candleData.close.toFixed(dec),
        change: changeStr,
        vol: volData && volData.value !== undefined ? Number(volData.value).toFixed(2) : '0.00',
        colorClass: isUp ? 'text-up' : 'text-down' 
      };

      if (marketStore.isSyncEnabled && marketStore.updateGlobalCrosshair) {
        marketStore.updateGlobalCrosshair(Number(param.time), candleData.close, props.symbol, instanceId);
      }
    } else hoverData.value = null;
  });

  chart.subscribeDblClick((param) => {
    if (!isQuickTradeEnabled.value || !param.point || !candleSeries) return;
    const clickedPrice = candleSeries.coordinateToPrice(param.point.y);
    if (clickedPrice !== null) {
      executeQuickTrade(clickedPrice);
    }
  });

  if (resizeObserver) resizeObserver.disconnect();
  resizeObserver = new ResizeObserver(entries => {
    if (entries[0].contentRect.width === 0) return;
    chart?.applyOptions({ width: chartContainer.value!.clientWidth, height: chartContainer.value!.clientHeight });
  });
  resizeObserver.observe(chartContainer.value);
};

const disposeCharts = () => {
  if (resizeObserver) resizeObserver.disconnect();
  if (chart) { chart.remove(); chart = null; }
  candleSeries = null;
  volumeSeries = null;
};

const hardReload = async () => {
  disposeCharts();
  currentChartData.value = [];
  customShapes.value = [];
  svgShapes.value = [];
  
  initCharts();
  await loadHistory(props.symbol, currentTf.value);
  marketStore.subscribeKline(props.symbol, currentTf.value); 
  showNotification(`[${props.symbol}] K 线控件引擎已彻底重载`);
};
defineExpose({ hardReload });

onMounted(async () => {
  if (!chartContainer.value) return;

  window.addEventListener('sync-logical-range', onSyncRange);
  window.addEventListener('sync-drawing', onSyncDrawing);

  initCharts();
  await loadHistory(props.symbol, currentTf.value);
  marketStore.subscribeKline(props.symbol, currentTf.value);
  
  renderSvgLoop();
});

watch(() => marketStore.globalCrosshairTime, () => {
  if (!chart || !candleSeries || !marketStore.isSyncEnabled) return;
  const allCrosshairs = marketStore.crosshairData;
  if (!allCrosshairs) return;

  const remoteCrosshair = allCrosshairs[props.symbol];
  if (!remoteCrosshair || remoteCrosshair.sourceId === instanceId || remoteCrosshair.time === 0) {
    chart.clearCrosshairPosition();
    return;
  }

  try {
    chart.setCrosshairPosition(remoteCrosshair.price, remoteCrosshair.time as any, candleSeries);
  } catch (e) {
    chart.clearCrosshairPosition();
  }
});

const currentKlineData = computed(() => marketStore.latestKlines[`${props.symbol}_${currentTf.value}`]);

watch(currentKlineData, (newVal) => {
  if (newVal && candleSeries && volumeSeries && currentChartData.value.length > 0) {
    const rawTime = Number(newVal.time);
    const timeInSeconds = rawTime > 9999999999 ? Math.floor(rawTime / 1000) : rawTime;
    
    let latestLogicalIndex = currentChartData.value.length - 1;
    const lastCandle = currentChartData.value[latestLogicalIndex];
    const lastTimeSec = Number(lastCandle.time) > 9999999999 ? Math.floor(Number(lastCandle.time) / 1000) : Number(lastCandle.time);

    if (timeInSeconds < lastTimeSec) return;

    if (timeInSeconds === lastTimeSec) {
      currentChartData.value[latestLogicalIndex] = newVal; 
    } else {
      currentChartData.value.push(newVal);
      latestLogicalIndex += 1;
    }

    const isUp = Number(newVal.close) >= Number(newVal.open);
    const volValue = Number(newVal.volume !== undefined ? newVal.volume : (newVal.vol || 0));

    if (localChartType.value === 'heikinAshi') {
      applyDataToSeries(currentChartData.value);
    } else {
      try {
        candleSeries.update({
          time: timeInSeconds as any,
          open: Number(newVal.open), high: Number(newVal.high), low: Number(newVal.low), close: Number(newVal.close)
        });
        volumeSeries.update({
          time: timeInSeconds as any, value: volValue, color: isUp ? 'rgba(38, 166, 154, 0.5)' : 'rgba(239, 83, 80, 0.5)'
        });
      } catch (e) {
        console.warn("增量刷新失败，触发兜底全量重载", e);
        applyDataToSeries(currentChartData.value);
      }
    }

    updatePositionLines(Number(newVal.close));

    customShapes.value.filter(s => s.type === 'alert_ray' && !s.triggered).forEach(shape => {
      const p0 = shape.points[0]; const p1 = shape.points[1];
      const dx = p1.logical - p0.logical; const dp = p1.price - p0.price;
      if ((dx > 0 && latestLogicalIndex >= p0.logical) || (dx < 0 && latestLogicalIndex <= p0.logical)) {
        const expectedPrice = p0.price + (dx === 0 ? 0 : (dp / dx) * (latestLogicalIndex - p0.logical));
        if (Number(newVal.low) <= expectedPrice && Number(newVal.high) >= expectedPrice) {
          shape.triggered = true;
          showNotification(`[${props.symbol}] 价格触及提醒射线: ${expectedPrice.toFixed(getPrecisionConfig().precision)}`);
          broadcastSync({ action: 'trigger', id: shape.id });
        }
      }
    });
  }
}, { deep: true });

watch([() => marketStore.positions, () => marketStore.marketTickers[props.symbol]?.lastPrice], () => {
  updatePositionLines();
}, { deep: true });

watch(() => marketStore.openOrders, () => {
  updateOpenOrderLines();
}, { deep: true });

const changeInterval = async (tf: string) => {
  if (tf === currentTf.value) return;
  marketStore.unsubscribeKline(props.symbol, currentTf.value);
  currentTf.value = tf;
  await loadHistory(props.symbol, tf);
  marketStore.subscribeKline(props.symbol, tf);
};

const changeChartType = (type: string) => { localChartType.value = type; applyDataToSeries(currentChartData.value); };
const toggleVolume = () => { showVolume.value = !showVolume.value; if (volumeSeries) volumeSeries.applyOptions({ visible: showVolume.value }); };

onUnmounted(() => {
  cancelAnimationFrame(animationFrameId);
  window.removeEventListener('sync-logical-range', onSyncRange);
  window.removeEventListener('sync-drawing', onSyncDrawing);
  marketStore.unsubscribeKline(props.symbol, currentTf.value);
  disposeCharts();
});
</script>

<style scoped>
.kline-module { width: 100%; height: 100%; display: flex; flex-direction: column; background: #0d1117; border: 1px solid transparent; transition: all 0.2s ease; box-sizing: border-box; }
.kline-module.is-focused { border-color: #58a6ff; box-shadow: inset 0 0 10px rgba(88, 166, 255, 0.1); }

.kline-toolbar { display: flex; justify-content: space-between; align-items: center; padding: 6px 12px; background: #161b22; border-bottom: 1px solid #21262d; flex-shrink: 0; z-index: 2; height: 38px; }

.intervals { display: flex; align-items: center; gap: 4px; overflow-x: auto; padding-right: 8px; flex-wrap: nowrap; scrollbar-width: none; }
.intervals::-webkit-scrollbar { display: none; }
.intervals button { background: transparent; border: 1px solid transparent; color: #8b949e; padding: 3px 6px; border-radius: 4px; cursor: pointer; font-size: 12px; white-space: nowrap; flex-shrink: 0; }
.intervals button:hover { background: #21262d; color: #c9d1d9; }
.intervals button.active { background: #2ea043; color: #ffffff; font-weight: bold; }

.actions-group { display: flex; align-items: center; gap: 8px; flex-shrink: 0; }
.focus-badge { background: #1f6feb; color: #ffffff; font-size: 11px; padding: 4px 8px; border-radius: 4px; font-weight: bold; flex-shrink: 0; }
.action-btn { background: rgba(226, 181, 20, 0.1); border: 1px solid #e2b514; color: #e2b514; padding: 4px 12px; border-radius: 4px; cursor: pointer; font-size: 12px; font-weight: bold; transition: all 0.2s; flex-shrink: 0;}
.copy-btn { border-color: #2ea043; color: #2ea043; background: rgba(46, 160, 67, 0.1); font-weight: normal; padding: 4px 8px; }
.copy-btn:hover { background: #2ea043; color: #ffffff; }

.position-panel { display: flex; align-items: center; gap: 6px; font-size: 12px; font-weight: bold; background: rgba(22, 27, 34, 0.8); padding: 4px 10px; border-radius: 4px; border: 1px solid #30363d; transition: all 0.3s ease; flex-shrink: 0; }
.position-panel.in-profit { border-color: rgba(46, 160, 67, 0.4); box-shadow: inset 0 0 10px rgba(46, 160, 67, 0.1); }
.position-panel.in-loss { border-color: rgba(248, 81, 73, 0.4); box-shadow: inset 0 0 10px rgba(248, 81, 73, 0.1); }
.pos-val { color: #c9d1d9; font-family: monospace;}
.pos-pnl { display: flex; gap: 4px; align-items: baseline; font-family: monospace; }
.pos-amount { font-family: monospace; }

.close-pos-btn {
  background: rgba(248, 81, 73, 0.15);
  border: 1px solid rgba(248, 81, 73, 0.4);
  color: #f85149;
  padding: 2px 6px;
  border-radius: 4px;
  cursor: pointer;
  font-size: 11px;
  font-weight: bold;
  margin-left: 2px;
  transition: all 0.2s;
}
.close-pos-btn:hover:not(:disabled) { background: #f85149; color: #ffffff; }
.close-pos-btn:disabled { opacity: 0.5; cursor: not-allowed; }

.close-pos-btn.btn-75 {
  background: rgba(210, 153, 34, 0.15);
  border-color: rgba(210, 153, 34, 0.4);
  color: #d29922;
  margin-left: 6px;
}
.close-pos-btn.btn-75:hover:not(:disabled) { background: #d29922; color: #0d1117; }

.kline-sub-toolbar { display: flex; align-items: center; justify-content: space-between; padding: 6px 12px; background: #0d1117; border-bottom: 1px solid #21262d; z-index: 1; height: 36px;}
.drawing-tools { display: flex; align-items: center; gap: 6px; }
.chart-type-selector { display: flex; background: #0d1117; border-radius: 4px; padding: 2px; }
.chart-type-selector button { background: transparent; border: none; color: #8b949e; padding: 2px 8px; font-size: 12px; cursor: pointer; border-radius: 2px; }
.chart-type-selector button.active { background: #30363d; color: #c9d1d9; font-weight: bold; }
.draw-select { background: #0d1117; color: #c9d1d9; border: 1px solid #30363d; border-radius: 4px; padding: 3px 6px; font-size: 12px; outline: none; cursor: pointer; }
.sync-btn { background: transparent; border: 1px solid #30363d; color: #8b949e; padding: 3px 8px; border-radius: 4px; cursor: pointer; font-size: 12px; transition: all 0.2s; }
.sync-btn.active { background: #1f6feb; color: white; border-color: #1f6feb; }
.cancel-all-btn { color: #f85149; border-color: rgba(248, 81, 73, 0.4); background: rgba(248, 81, 73, 0.1); }
.cancel-all-btn:hover { background: #f85149; color: white; }
.clear-btn:hover { border-color: #f85149 !important; color: #f85149 !important; }
.divider { color: #30363d; margin: 0 4px; }

.quick-order-pill {
  display: flex;
  align-items: center;
  background: rgba(13, 17, 23, 0.6);
  border: 1px solid #30363d;
  border-radius: 6px;
  padding: 2px 4px;
  gap: 4px;
  transition: all 0.3s ease;
}
.qt-checkbox {
  display: flex;
  align-items: center;
  gap: 4px;
  color: #8b949e;
  font-size: 12px;
  font-weight: bold;
  cursor: pointer;
  padding: 2px 6px;
  border-radius: 4px;
  user-select: none;
}
.qt-checkbox:hover { color: #c9d1d9; background: #21262d; }
.qt-checkbox.active { color: #e2b514; }

.qt-divider { width: 1px; height: 14px; background: #30363d; margin: 0 2px; }
.qt-balance { color: #8b949e; font-size: 11px; font-family: monospace; margin: 0 4px; white-space: nowrap; }

.qt-input-wrapper { display: flex; align-items: center; background: #010409; border: 1px solid #30363d; border-radius: 4px; padding: 0 4px; }
.qt-input { background: transparent; border: none; color: #e6edf3; width: 32px; text-align: center; font-size: 12px; outline: none; font-family: monospace; }
.qt-input::-webkit-outer-spin-button, .qt-input::-webkit-inner-spin-button { -webkit-appearance: none; margin: 0; }
.qt-unit { color: #8b949e; font-size: 12px; font-weight: bold; margin-right: 2px; }

.qt-toggle-btn { background: #21262d; border: 1px solid #30363d; color: #c9d1d9; font-size: 12px; padding: 3px 8px; border-radius: 4px; cursor: pointer; font-weight: bold; transition: all 0.2s; }
.qt-toggle-btn:hover { background: #30363d; }
.qt-buy { color: #2ea043; border-color: rgba(46, 160, 67, 0.4); background: rgba(46, 160, 67, 0.1); }
.qt-buy:hover { background: #2ea043; color: white; }
.qt-sell { color: #f85149; border-color: rgba(248, 81, 73, 0.4); background: rgba(248, 81, 73, 0.1); }
.qt-sell:hover { background: #f85149; color: white; }

.text-up { color: #2ea043; }
.text-down { color: #f85149; }

.chart-wrapper { flex: 1; position: relative; width: 100%; display: flex; flex-direction: column; overflow: hidden; transition: box-shadow 0.2s; }
.is-drawing-mode { box-shadow: inset 0 0 15px rgba(88, 166, 255, 0.2); cursor: crosshair; }
.chart-container { flex: 1; width: 100%; position: relative; z-index: 1; }

.is-resizing { cursor: move !important; }
.is-hovering-position { cursor: ns-resize !important; }

/* 🌟 图内挂单交互层样式 */
.open-orders-layer { position: absolute; top: 0; left: 0; width: 100%; height: 100%; pointer-events: none; z-index: 15; }
.open-order-tag {
  position: absolute; left: 10px; transform: translateY(-50%); display: flex; align-items: stretch; gap: 0;
  border-radius: 4px; font-size: 11px; font-weight: bold; pointer-events: auto;
  box-shadow: 0 2px 6px rgba(0,0,0,0.3); transition: top 0.1s linear; user-select: none;
}
.tag-buy { background: rgba(46, 160, 67, 0.85); color: white; border: 1px solid #2ea043; }
.tag-sell { background: rgba(248, 81, 73, 0.85); color: white; border: 1px solid #f85149; }
.tag-content { padding: 4px 8px; cursor: pointer; display: flex; align-items: center; gap: 6px; transition: 0.2s; border-radius: 3px 0 0 3px; }
.tag-content:hover { background: rgba(255,255,255,0.15); }
.edit-icon { font-size: 9px; opacity: 0.8; }
.tag-cancel-btn {
  background: rgba(0,0,0,0.15); border: none; color: white; width: 22px; 
  display: flex; align-items: center; justify-content: center; font-size: 12px; cursor: pointer;
  border-radius: 0 3px 3px 0; border-left: 1px solid rgba(255,255,255,0.2); transition: 0.2s;
}
.tag-cancel-btn:hover { background: #f85149; }

/* 🌟 图内编辑面板样式 */
.edit-popover {
  position: absolute; left: 100%; top: 50%; transform: translateY(-50%); margin-left: 8px;
  background: rgba(22, 27, 34, 0.95); border: 1px solid #30363d; border-radius: 6px; padding: 8px;
  width: 150px; box-shadow: 0 8px 16px rgba(0,0,0,0.5); display: flex; flex-direction: column; gap: 8px;
  cursor: default; z-index: 20; color: #c9d1d9; backdrop-filter: blur(4px);
}
.edit-header { font-size: 12px; font-weight: bold; color: #8b949e; border-bottom: 1px solid #30363d; padding-bottom: 4px; text-align: center; }
.edit-row { display: flex; align-items: center; justify-content: space-between; font-size: 11px; }
.edit-row span { width: 32px; color: #8b949e; }
.edit-row input { flex: 1; width: 0; background: #010409; border: 1px solid #30363d; color: #c9d1d9; padding: 3px 6px; border-radius: 4px; outline: none; font-family: monospace; }
.edit-row input:focus { border-color: #58a6ff; }
.edit-actions { display: flex; gap: 6px; margin-top: 2px; }
.edit-actions button { flex: 1; padding: 4px; border: none; border-radius: 4px; cursor: pointer; font-size: 11px; font-weight: bold; transition: 0.2s; }
.btn-confirm { background: #2ea043; color: white; }
.btn-confirm:hover { background: #238636; }
.btn-cancel { background: #21262d; color: #c9d1d9; border: 1px solid #30363d; }
.btn-cancel:hover { background: #30363d; }


.drawing-layer { position: absolute; top: 0; left: 0; width: 100%; height: 100%; pointer-events: none; z-index: 5; }

.toast-container { position: absolute; top: 12px; right: 12px; z-index: 50; display: flex; flex-direction: column; gap: 8px; pointer-events: none; }
.toast-message { background: rgba(255, 152, 0, 0.9); color: white; padding: 8px 16px; border-radius: 6px; font-size: 12px; font-weight: bold; box-shadow: 0 4px 12px rgba(0,0,0,0.3); animation: slideIn 0.3s ease-out; backdrop-filter: blur(4px); border: 1px solid rgba(255,255,255,0.2); }
@keyframes slideIn { from { transform: translateX(100%); opacity: 0; } to { transform: translateX(0); opacity: 1; } }

.chart-legend { position: absolute; top: 8px; left: 12px; z-index: 10; display: flex; gap: 12px; font-size: 12px; pointer-events: none; background: rgba(13, 17, 23, 0.75); padding: 4px 8px; border-radius: 4px; }
.legend-time { color: #8b949e; font-weight: bold; margin-right: 4px; }
.legend-item { color: #8b949e; }
.vol-text { color: #c9d1d9; font-weight: bold; }

.line-settings-panel { position: absolute; top: 8px; right: 12px; z-index: 10; display: flex; align-items: center; gap: 8px; background: rgba(22, 27, 34, 0.85); border: 1px solid #30363d; padding: 6px 12px; border-radius: 6px; backdrop-filter: blur(4px); }
.setting-title { font-size: 12px; color: #8b949e; margin-right: 4px; }
.line-settings-panel input[type="color"] { background: transparent; border: none; width: 24px; height: 24px; cursor: pointer; padding: 0; }
.line-settings-panel .action-btn { background: transparent; border: 1px solid #30363d; color: #c9d1d9; padding: 2px 8px; border-radius: 4px; cursor: pointer; font-size: 12px; transition: 0.2s; }
.line-settings-panel .action-btn:hover { background: #30363d; }
.line-settings-panel .delete-btn:hover { border-color: #f85149; color: #f85149; }
</style>
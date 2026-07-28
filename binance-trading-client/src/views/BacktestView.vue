<template>
  <div class="backtest-container">
    <!-- Top summary bar -->
    <div class="backtest-header">
      <div class="header-title">
        <span class="icon">📊</span>
        <h1>策略历史回测系统</h1>
        <p class="subtitle">历史数据沙箱模拟器，支持高并发任务队列，精准还原手续费、滑点与资金费率</p>
      </div>
    </div>

    <!-- Main Workspace Grid -->
    <div class="backtest-grid">
      <!-- Left Panel: Configuration Form -->
      <div class="config-card pane-card">
        <div class="card-header">
          <span class="icon">⚙️</span>
          <h2>新建回测配置</h2>
        </div>
        <form @submit.prevent="submitBacktest" class="config-form">
          <div class="form-row">
            <div class="form-group">
              <label>回测币种 (Symbol)</label>
              <div class="custom-select-container" ref="symbolDropdownContainer">
                <div class="custom-select-trigger" @click="showSymbolDropdown = !showSymbolDropdown" :class="{ 'dropdown-open': showSymbolDropdown }">
                  <div class="trigger-left">
                    <span class="symbol-name">{{ form.symbol }}</span>
                    <span v-if="marketStore.marketTickers[form.symbol]" class="symbol-price-badge">
                      ${{ marketStore.marketTickers[form.symbol].lastPrice }}
                    </span>
                  </div>
                  <div class="trigger-right">
                    <span v-if="marketStore.marketTickers[form.symbol]" :class="getChangeClass(marketStore.marketTickers[form.symbol].priceChangePercent)">
                      {{ formatChange(marketStore.marketTickers[form.symbol].priceChangePercent) }}
                    </span>
                    <span class="arrow-icon">▼</span>
                  </div>
                </div>

                <div class="custom-select-dropdown" v-if="showSymbolDropdown">
                  <div class="dropdown-search-wrapper">
                    <span class="search-icon">🔍</span>
                    <input 
                      type="text" 
                      placeholder="搜索币种..." 
                      v-model="searchQuery" 
                      ref="searchInput" 
                      @click.stop
                    />
                    <button v-if="searchQuery" class="clear-search-btn" @click.stop="searchQuery = ''">✕</button>
                  </div>

                  <div class="dropdown-sort-tabs">
                    <button 
                      type="button" 
                      class="sort-tab-btn" 
                      :class="{ active: sortBy === 'name' }" 
                      @click.stop="sortBy = 'name'"
                    >
                      字母
                    </button>
                    <button 
                      type="button" 
                      class="sort-tab-btn" 
                      :class="{ active: sortBy === 'change' }" 
                      @click.stop="sortBy = 'change'"
                    >
                      涨幅 📈
                    </button>
                    <button 
                      type="button" 
                      class="sort-tab-btn" 
                      :class="{ active: sortBy === 'volume' }" 
                      @click.stop="sortBy = 'volume'"
                    >
                      成交量 📊
                    </button>
                  </div>

                  <div class="dropdown-symbols-list custom-scrollbar">
                    <div v-if="isLoadingSymbols" class="symbols-loading-state">
                      <span class="mini-loader"></span> 正在加载支持币种...
                    </div>
                    <div v-else-if="filteredAndSortedSymbols.length === 0" class="no-symbols-found">
                      未找到匹配交易对
                    </div>
                    <template v-else>
                      <div 
                        v-for="sym in filteredAndSortedSymbols" 
                        :key="sym" 
                        class="symbol-row-item" 
                        :class="{ selected: form.symbol === sym }"
                        @click="selectSymbol(sym)"
                      >
                        <div class="symbol-cell-left">
                          <span class="symbol-label">{{ sym }}</span>
                          <span class="symbol-sub">永续</span>
                        </div>
                        <div class="symbol-cell-right">
                          <span class="symbol-price" v-if="marketStore.marketTickers[sym]">
                            ${{ marketStore.marketTickers[sym].lastPrice?.toFixed(4) || '--' }}
                          </span>
                          <div class="symbol-stats">
                            <span 
                              v-if="marketStore.marketTickers[sym]" 
                              class="symbol-pct-change"
                              :class="getChangeClass(marketStore.marketTickers[sym].priceChangePercent)"
                            >
                              {{ formatChange(marketStore.marketTickers[sym].priceChangePercent) }}
                            </span>
                            <span v-if="marketStore.marketTickers[sym]" class="symbol-vol">
                              Vol: {{ formatVolume(marketStore.marketTickers[sym].volume) }}
                            </span>
                          </div>
                        </div>
                      </div>
                    </template>
                  </div>
                </div>
              </div>
            </div>
            <div class="form-group">
              <label>测试策略 (Strategy)</label>
              <select v-model="form.strategyName" required>
                <option v-for="strat in strategies" :key="strat.name" :value="strat.name">
                  {{ strat.displayName }}
                </option>
              </select>
            </div>
          </div>

          <div class="form-row">
            <div class="form-group">
              <label>K线基础周期</label>
              <select v-model="form.timeframe" required>
                <option value="1m">1分钟 (1m - 推荐)</option>
                <option value="3m">3分钟 (3m)</option>
                <option value="5m">5分钟 (5m)</option>
                <option value="15m">15分钟 (15m)</option>
                <option value="30m">30分钟 (30m)</option>
                <option value="1h">1小时 (1h)</option>
              </select>
            </div>
            <div class="form-group">
              <label>杠杆倍数 (Leverage)</label>
              <div class="slider-container">
                <input type="range" min="1" max="100" v-model.number="form.leverage" class="range-slider" @wheel.prevent="handleInputWheel($event, 'leverage', 1, 1, 100, 0)" />
                <span class="slider-val">{{ form.leverage }}x</span>
              </div>
            </div>
          </div>

          <div class="form-row">
            <div class="form-group">
              <label>开始时间 (UTC+8)</label>
              <input type="datetime-local" v-model="form.startTimeStr" required />
            </div>
            <div class="form-group">
              <label>结束时间 (UTC+8)</label>
              <input type="datetime-local" v-model="form.endTimeStr" required />
            </div>
          </div>

          <div class="form-row">
            <div class="form-group">
              <label>初始资金 (USDT)</label>
              <input type="number" v-model.number="form.initialBalance" min="100" step="100" required @wheel.prevent="handleInputWheel($event, 'initialBalance', 100, 100, undefined, 0)" />
            </div>
            <div class="form-group">
              <label>手续费率 (Fee Rate %)</label>
              <input type="number" v-model.number="form.feeRatePercent" min="0" max="1" step="0.01" required @wheel.prevent="handleInputWheel($event, 'feeRatePercent', 0.01, 0, 1, 2)" />
            </div>
          </div>

          <div class="form-row">
            <div class="form-group">
              <label>资金费率 (Funding Rate %)</label>
              <input type="number" v-model.number="form.fundingRatePercent" min="0" max="1" step="0.005" required @wheel.prevent="handleInputWheel($event, 'fundingRatePercent', 0.005, 0, 1, 3)" />
            </div>
            <div class="form-group">
              <label>Taker 滑点 (%)</label>
              <input type="number" v-model.number="form.takerSlippagePercent" min="0" max="1" step="0.01" required @wheel.prevent="handleInputWheel($event, 'takerSlippagePercent', 0.01, 0, 1, 2)" />
            </div>
          </div>

          <div class="form-row">
            <div class="form-group">
              <label>Maker 滑点 (%)</label>
              <input type="number" v-model.number="form.makerSlippagePercent" min="0" max="1" step="0.01" required @wheel.prevent="handleInputWheel($event, 'makerSlippagePercent', 0.01, 0, 1, 2)" />
            </div>
            <div class="form-group">
              <label>保本损选项</label>
              <div style="display: flex; align-items: center; height: 38px;">
                <label class="toggle-checkbox-label" style="font-size: 13px; color: #c9d1d9;">
                  <input type="checkbox" v-model="form.enableMoveStopToBE" />
                  开启移动止损到开仓价 (保本)
                </label>
              </div>
            </div>
          </div>

          <button type="submit" class="submit-btn" :disabled="submitting">
            <span v-if="submitting" class="loader"></span>
            <span v-else>🚀 开始回测任务</span>
          </button>
        </form>
      </div>

      <!-- Right Panel: Tasks Queue List -->
      <div class="tasks-card pane-card">
        <div class="card-header">
          <span class="icon">📋</span>
          <h2>回测任务队列 (并发上限: 2)</h2>
          <button class="refresh-btn" @click="fetchTasks">🔄 刷新</button>
        </div>
        <div class="tasks-list">
          <div v-if="tasks.length === 0" class="no-tasks">
            暂无回测任务，请在左侧配置并提交
          </div>
          <div v-for="task in sortedTasks" :key="task.taskId" class="task-item" :class="task.status.toLowerCase()">
            <div class="task-info">
              <div class="task-meta">
                <span class="task-id">ID: #{{ task.taskId }}</span>
                <span class="task-badge" :class="task.status.toLowerCase()">{{ statusTextMap[task.status] || task.status }}</span>
              </div>
              <div class="task-title">{{ task.config.symbol }} | {{ task.config.strategyName }} ({{ task.config.leverage }}x)</div>
              <div class="task-time-range">
                {{ formatDateTime(task.config.startTime) }} 至 {{ formatDateTime(task.config.endTime) }}
              </div>
              
              <!-- Progress info -->
              <div class="progress-section">
                <div class="progress-bar-bg">
                  <div class="progress-bar" :style="{ width: task.progress + '%' }"></div>
                </div>
                <div class="progress-stats">
                  <span>进度: {{ task.progress }}%</span>
                  <span>交易笔数: {{ task.tradesCount }}</span>
                  <span>余额: {{ task.currentBalance }} USDT</span>
                </div>
                <!-- Mini stats for completed reports -->
                <div v-if="task.status === 'Completed' && task.report" class="task-report-mini-stats">
                  <span>币种波动: <strong class="text-orange">{{ task.report.symbolVolatility }}%</strong></span>
                  <span>杠杆: <strong class="text-white">{{ task.report.leverage }}x</strong></span>
                </div>
              </div>

              <!-- Controls -->
              <div class="task-actions">
                <button v-if="task.status === 'Running'" @click="pauseTask(task.taskId)" class="action-btn pause">⏸️ 暂停</button>
                <button v-if="task.status === 'Paused'" @click="resumeTask(task.taskId)" class="action-btn resume">▶️ 继续</button>
                <button v-if="['Queued', 'Running', 'Paused'].includes(task.status)" @click="cancelTask(task.taskId)" class="action-btn cancel">⏹️ 取消</button>
                <button v-if="task.status === 'Completed'" @click="viewReport(task.taskId)" class="action-btn view-report" :class="{ active: selectedTaskId === task.taskId }">📊 查看报告</button>
              </div>

              <div v-if="task.status === 'Failed'" class="error-msg">
                ❌ 错误: {{ task.errorMessage }}
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>

    <!-- Bottom Panel: Backtest Report Details -->
    <div v-if="selectedReport" class="report-section pane-card">
      <div class="card-header">
        <span class="icon">📈</span>
        <h2>回测报告详情 (#{{ selectedTaskId }})</h2>
        <div class="report-meta">
          <span>策略: <strong>{{ selectedReport.config?.strategyName }}</strong></span>
          <span>币种: <strong>{{ selectedReport.config?.symbol }}</strong></span>
        </div>
      </div>

      <!-- Stats Grid -->
      <div class="stats-grid">
        <div class="stat-card">
          <div class="stat-label">总盈亏</div>
          <div class="stat-value" :class="getPnlClass(selectedReport.report.totalProfit)">
            {{ selectedReport.report.totalProfit > 0 ? '+' : '' }}{{ selectedReport.report.totalProfit }} USDT
          </div>
          <div class="stat-sub" :class="getPnlClass(selectedReport.report.totalProfitPct)">
            {{ selectedReport.report.totalProfitPct > 0 ? '+' : '' }}{{ selectedReport.report.totalProfitPct }}%
          </div>
        </div>
        <div class="stat-card">
          <div class="stat-label">胜率</div>
          <div class="stat-value text-blue">{{ selectedReport.report.winRate }}%</div>
          <div class="stat-sub">总盈利笔数 / 总交易笔数</div>
        </div>
        <div class="stat-card">
          <div class="stat-label">交易次数</div>
          <div class="stat-value">{{ selectedReport.report.totalTradesCount }} 次</div>
          <div class="stat-sub">多仓 & 空仓平仓总计</div>
        </div>
        <div class="stat-card">
          <div class="stat-label">总手续费</div>
          <div class="stat-value text-orange">{{ totalTransactionFees }} USDT</div>
          <div class="stat-sub">累计开平仓交易手续费 (损耗: {{ selectedReport.report.totalFees }} USDT)</div>
        </div>
        <div class="stat-card">
          <div class="stat-label">总资金费用</div>
          <div class="stat-value text-orange">{{ totalFundingFees }} USDT</div>
          <div class="stat-sub">持仓 8 小时整点结算费</div>
        </div>
        <div class="stat-card">
          <div class="stat-label">最大回撤</div>
          <div class="stat-value text-red">{{ selectedReport.report.maxDrawdown }}%</div>
          <div class="stat-sub">基于权益曲线的峰值最大跌幅</div>
        </div>
        <div class="stat-card">
          <div class="stat-label">币种最大振幅</div>
          <div class="stat-value text-orange">{{ selectedReport.report.symbolVolatility }}%</div>
          <div class="stat-sub">回测区间内最大价格波动百分比</div>
        </div>
        <div class="stat-card">
          <div class="stat-label">使用杠杆</div>
          <div class="stat-value text-blue">{{ selectedReport.report.leverage }}x</div>
          <div class="stat-sub">回测配置的仓位杠杆倍数</div>
        </div>
      </div>

      <!-- Charts & Trade Logs Grid -->
      <div class="report-details-grid">
        <!-- Equity Curve Chart -->
        <div class="chart-container">
          <h3>资金曲线变化 (Equity Curve)</h3>
          <div ref="chartRef" class="equity-chart"></div>
        </div>

        <!-- Trade K-Line Visualization Pane -->
        <div v-if="selectedTrade" class="kline-container">
          <div class="kline-header">
            <div class="kline-title">
              <span class="icon">📈</span>
              <h3>交易 K 线轨迹标记 [#{{ selectedTrade.id }}]</h3>
            </div>
            
            <!-- 新增：时间周期选择器 -->
            <div class="kline-timeframes">
              <button 
                v-for="tf in ['1m', '3m', '5m', '15m', '30m', '1h', '4h', '1d']" 
                :key="tf" 
                class="tf-btn" 
                :class="{ active: klineTimeframe === tf }"
                @click="changeKlineTimeframe(tf)">
                {{ tf }}
              </button>
            </div>

            <!-- 新增：查看高低点选项 -->
            <div class="kline-toggle-options">
              <label class="toggle-checkbox-label">
                <input type="checkbox" v-model="showHighLowPoints" @change="toggleHighLowPoints" />
                显示持仓高低点
              </label>
            </div>

            <div class="kline-meta-info">
              <span class="symbol-badge">{{ selectedTrade.symbol }}</span>
              <span class="dir-badge" :class="selectedTrade.direction.toLowerCase()">
                {{ selectedTrade.direction === 'LONG' ? '做多 🟢' : '做空 🔴' }}
              </span>
              <span class="price-info">
                开仓: <strong class="text-white">{{ selectedTrade.openPrice.toFixed(4) }}</strong> 
                <span v-if="selectedTrade.closePrice">
                  | 平仓: <strong class="text-white">{{ selectedTrade.closePrice.toFixed(4) }}</strong>
                  | 盈亏: <strong :class="getPnlClass(selectedTrade.tradePnL)">{{ selectedTrade.tradePnL > 0 ? '+' : '' }}{{ selectedTrade.tradePnL.toFixed(2) }} USDT</strong>
                </span>
              </span>
              <button class="close-kline-btn" @click="clearSelectedTrade">❌ 关闭图表</button>
            </div>
          </div>
          
          <!-- 新增：高层级定位包裹容器 -->
          <div class="kline-chart-wrapper">
            <div ref="klineChartRef" class="trade-kline-chart"></div>
            <!-- 新增：十字光标详细浮动信息框 -->
            <div class="kline-hover-panel" v-if="klineHoverData">
              <span class="hover-time">{{ klineHoverData.time }}</span>
              <span class="hover-item">开: <span :class="klineHoverData.colorClass">{{ klineHoverData.open }}</span></span>
              <span class="hover-item">高: <span :class="klineHoverData.colorClass">{{ klineHoverData.high }}</span></span>
              <span class="hover-item">低: <span :class="klineHoverData.colorClass">{{ klineHoverData.low }}</span></span>
              <span class="hover-item">收: <span :class="klineHoverData.colorClass">{{ klineHoverData.close }}</span></span>
              <span class="hover-item">幅: <span :class="klineHoverData.colorClass">{{ klineHoverData.change }}</span></span>
              <span class="hover-item">量: <span class="text-orange">{{ klineHoverData.volume }}</span></span>
            </div>
          </div>

          <div class="kline-legend">
            <span>🟢 向上/向下箭头：开仓点位</span>
            <span>🟡 橙黄色箭头/圆形：平仓点位 (止盈/止损/保本/强平)</span>
            <span>🔵 蓝色标记：保本损 (BreakEven)</span>
            <span>🟡 黄色圆形：持仓最高点 (Peaks)</span>
            <span>🔵 青蓝色圆形：持仓最低点 (Valleys)</span>
            <span>📊 柱状图：成交量 (Volume)</span>
          </div>
        </div>

        <!-- Trade History List -->
        <div class="history-container">
          <div class="history-header">
            <h3>交易明细历史 ({{ filteredTrades.length }} 笔)</h3>
            <div class="history-filters">
              <select v-model="filterDirection" class="filter-select">
                <option value="ALL">全部方向</option>
                <option value="LONG">多单 (LONG)</option>
                <option value="SHORT">空单 (SHORT)</option>
              </select>
              <select v-model="filterReason" class="filter-select">
                <option value="ALL">全部平仓理由</option>
                <option value="TakeProfit">止盈 (TakeProfit)</option>
                <option value="StopLoss">止损 (StopLoss)</option>
                <option value="BreakEven">保本 (BreakEven)</option>
                <option value="Liquidation">强平 (Liquidation)</option>
              </select>
            </div>
          </div>

          <div class="table-wrapper">
            <table class="trades-table">
              <thead>
                <tr>
                  <th>订单ID</th>
                  <th>方向</th>
                  <th>数量</th>
                  <th>开仓明细 (价格/类型/手续费/滑点)</th>
                  <th>平仓明细 (价格/原因/手续费/滑点)</th>
                  <th>止盈/止损价格</th>
                  <th>持仓波动</th>
                  <th>持仓时长</th>
                  <th>资金费率</th>
                  <th>单笔盈亏 / ROI</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="trade in paginatedTrades" :key="trade.id"
                    @click="selectTrade(trade)"
                    :class="{ 'active-row': selectedTrade && selectedTrade.id === trade.id }">
                  <td>
                    <span class="order-id" :title="trade.orderNumber">{{ trade.id }}</span>
                  </td>
                  <td>
                    <span class="dir-badge" :class="trade.direction.toLowerCase()">
                      {{ trade.direction === 'LONG' ? '多 🟢' : '空 🔴' }}
                    </span>
                  </td>
                  <td>{{ trade.quantity.toFixed(4) }}</td>
                  <td class="cell-detail">
                    <div>价格: <strong class="text-white">{{ trade.openPrice.toFixed(4) }}</strong></div>
                    <div class="sub-text">时间: {{ formatDateTime(trade.openTime) }}</div>
                    <div class="sub-text">手续费: {{ trade.openFee.toFixed(4) }} USDT | 滑点: {{ trade.openSlippage.toFixed(4) }} | <span class="badge-type">{{ trade.openType }}</span></div>
                  </td>
                  <td class="cell-detail">
                    <template v-if="trade.closePrice">
                      <div>价格: <strong class="text-white">{{ trade.closePrice.toFixed(4) }}</strong></div>
                      <div class="sub-text">时间: {{ formatDateTime(trade.closeTime) }}</div>
                      <div class="sub-text">手续费: {{ trade.closeFee?.toFixed(4) }} USDT | 滑点: {{ trade.closeSlippage?.toFixed(4) }} | <span class="badge-type">{{ trade.closeType }}</span></div>
                      <div class="close-reason" :class="trade.closeReason.toLowerCase()">{{ closeReasonMap[trade.closeReason] || trade.closeReason }}</div>
                    </template>
                    <template v-else>
                      <span class="status-badge running">持有中</span>
                    </template>
                  </td>
                  <td class="cell-detail">
                    <div>止盈: <strong class="text-green">{{ trade.takeProfitPrice ? trade.takeProfitPrice.toFixed(4) : '-' }}</strong></div>
                    <div>止损: <strong class="text-red">{{ trade.stopLossPrice ? trade.stopLossPrice.toFixed(4) : '-' }}</strong></div>
                  </td>
                  <td class="text-orange font-bold">
                    {{ trade.holdPeriodVolatility !== undefined ? trade.holdPeriodVolatility.toFixed(2) + '%' : '0.00%' }}
                  </td>
                  <td>{{ formatDuration(trade.holdDurationMinutes) }}</td>
                  <td class="text-orange">{{ trade.fundingFeePaid.toFixed(4) }} USDT</td>
                  <td>
                    <div class="pnl-value" :class="getPnlClass(trade.tradePnL)">
                      {{ trade.tradePnL > 0 ? '+' : '' }}{{ trade.tradePnL.toFixed(2) }} USDT
                    </div>
                    <div class="pnl-roi" :class="getPnlClass(trade.tradeROI)">
                      {{ trade.tradeROI > 0 ? '+' : '' }}{{ trade.tradeROI.toFixed(2) }}%
                    </div>
                  </td>
                </tr>
                <tr v-if="paginatedTrades.length === 0">
                  <td colspan="10" class="no-data">无符合筛选条件的交易记录</td>
                </tr>
              </tbody>
            </table>
          </div>

          <!-- Pagination -->
          <div class="pagination" v-if="totalPages > 1">
            <button @click="currentPage--" :disabled="currentPage === 1" class="page-btn">◀ 上一页</button>
            <span class="page-info">第 {{ currentPage }} / {{ totalPages }} 页</span>
            <button @click="currentPage++" :disabled="currentPage === totalPages" class="page-btn">下一页 ▶</button>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, computed, onMounted, onUnmounted, nextTick, watch } from 'vue';
import axios from 'axios';
import * as signalR from '@microsoft/signalr';
import * as echarts from 'echarts';
import { createChart } from 'lightweight-charts';
import { MarketAPI } from '@/api/market';
import { useMarketStore } from '@/store/market';

// API Configuration
const baseUrl = import.meta.env.VITE_API_BASE_URL || 'http://localhost:5000';

// Helper to normalize backend enum status (number or string) to standardized titlecase string
const getStatusString = (status: any): string => {
  if (status === undefined || status === null) return 'Queued';
  if (typeof status === 'number') {
    const statusMap = ['Queued', 'Running', 'Paused', 'Completed', 'Cancelled', 'Failed'];
    return statusMap[status] || 'Queued';
  }
  const s = String(status).trim();
  const lower = s.toLowerCase();
  if (lower === 'queued') return 'Queued';
  if (lower === 'running') return 'Running';
  if (lower === 'paused') return 'Paused';
  if (lower === 'completed') return 'Completed';
  if (lower === 'cancelled') return 'Cancelled';
  if (lower === 'failed') return 'Failed';
  return s;
};

// Reactive States
const submitting = ref(false);
const tasks = ref<any[]>([]);
const selectedTaskId = ref<string | null>(null);
const selectedReport = ref<any | null>(null);
const chartRef = ref<HTMLDivElement | null>(null);
const klineChartRef = ref<HTMLDivElement | null>(null);
const selectedTrade = ref<any | null>(null);

// New optimization variables
const klineTimeframe = ref('1m');
const klineHoverData = ref<any | null>(null);
const klineChartData = ref<any[]>([]);
const showHighLowPoints = ref(true); // 🌟 是否显示最高/最低价格点标记
let lwVolumeSeries: any = null;
let lwCandlestickSeries: any = null;
let isKlineLoadingMore = false;

let chartInstance: echarts.ECharts | null = null;
let lwChartInstance: any = null;
let signalrConnection: signalR.HubConnection | null = null;

// Custom symbol dropdown states
const marketStore = useMarketStore();
const showSymbolDropdown = ref(false);
const searchQuery = ref('');
const sortBy = ref<'name' | 'volume' | 'change'>('name');
const availableSymbols = ref<string[]>([]);
const isLoadingSymbols = ref(false);
const symbolDropdownContainer = ref<HTMLElement | null>(null);
const searchInput = ref<HTMLInputElement | null>(null);

// Mouse wheel event handler for numeric inputs
const handleInputWheel = (event: WheelEvent, field: string, step: number, min?: number, max?: number, decimals: number = 2) => {
  event.preventDefault();
  const currentVal = (form as any)[field] ?? 0;
  const direction = event.deltaY < 0 ? 1 : -1;
  let newVal = currentVal + direction * step;
  if (min !== undefined && newVal < min) newVal = min;
  if (max !== undefined && newVal > max) newVal = max;
  (form as any)[field] = parseFloat(newVal.toFixed(decimals));
};

// Fetch available symbols from API
const loadAvailableSymbols = async () => {
  isLoadingSymbols.value = true;
  try {
    const res = await axios.get(`${baseUrl}/api/market/exchangeInfo`);
    if (res.data && res.data.symbols) {
      availableSymbols.value = res.data.symbols
        .filter((s: any) => s.quoteAsset === 'USDT' && s.status === 'TRADING')
        .map((s: any) => s.symbol);
    } else {
      availableSymbols.value = ['BTCUSDT', 'ETHUSDT', 'SOLUSDT', 'XRPUSDT', 'BNBUSDT'];
    }
  } catch (err) {
    console.error('获取回测币种列表失败', err);
    availableSymbols.value = ['BTCUSDT', 'ETHUSDT', 'SOLUSDT', 'XRPUSDT', 'BNBUSDT'];
  } finally {
    isLoadingSymbols.value = false;
  }
};

// Computed property for filtered and sorted symbols
const filteredAndSortedSymbols = computed(() => {
  let list = [...availableSymbols.value];

  // 1. Filter by search query
  if (searchQuery.value.trim()) {
    const q = searchQuery.value.trim().toUpperCase();
    list = list.filter(sym => sym.includes(q));
  }

  // 2. Sort list
  list.sort((a, b) => {
    const tickerA = marketStore.marketTickers[a];
    const tickerB = marketStore.marketTickers[b];

    if (sortBy.value === 'volume') {
      const volA = tickerA?.volume ?? 0;
      const volB = tickerB?.volume ?? 0;
      return volB - volA; // descending
    } else if (sortBy.value === 'change') {
      const chgA = tickerA?.priceChangePercent ?? -999;
      const chgB = tickerB?.priceChangePercent ?? -999;
      return chgB - chgA; // descending
    } else {
      return a.localeCompare(b); // alphabetical
    }
  });

  return list;
});

// Formatters for tickers
const formatVolume = (vol: number | undefined) => {
  if (vol === undefined) return '--';
  if (vol >= 1_000_000_000) return (vol / 1_000_000_000).toFixed(2) + 'B';
  if (vol >= 1_000_000) return (vol / 1_000_000).toFixed(2) + 'M';
  if (vol >= 1_000) return (vol / 1_000).toFixed(2) + 'K';
  return vol.toFixed(2);
};

const formatChange = (pct: number | undefined) => {
  if (pct === undefined) return '--';
  return `${pct >= 0 ? '+' : ''}${pct.toFixed(2)}%`;
};

const getChangeClass = (pct: number | undefined) => {
  if (pct === undefined) return '';
  return pct > 0 ? 'text-green' : pct < 0 ? 'text-red' : '';
};

// Select a symbol and close dropdown
const selectSymbol = (sym: string) => {
  form.symbol = sym;
  showSymbolDropdown.value = false;
  searchQuery.value = '';
};

// Auto focus on search input when dropdown opens
watch(showSymbolDropdown, async (newVal) => {
  if (newVal) {
    await nextTick();
    if (searchInput.value) {
      searchInput.value.focus();
    }
  }
});

// Click outside to close dropdown handler
const handleClickOutside = (e: MouseEvent) => {
  if (symbolDropdownContainer.value && !symbolDropdownContainer.value.contains(e.target as Node)) {
    showSymbolDropdown.value = false;
  }
};

// Dynamic strategies list
const strategies = ref<any[]>([
  { name: 'VReversalStrategyService', displayName: 'VReversal 反转策略 (1m入场+15m判定+1d周期)' },
  { name: 'VolumeExhaustionReversalStrategyService', displayName: '成交量衰竭反转策略' },
  { name: 'HighVolStructureStrategyService', displayName: '爆量超跌反弹策略' },
  { name: 'MinVolumeReversalStrategyService', displayName: '1分钟成交量反转策略' },
  { name: 'VStructureRegressionStrategyService', displayName: 'V型及倒V型形态拟合策略' },
  { name: 'MtfTouchReversalStrategyService', displayName: '多周期高低点触及反转策略' },
  { name: 'MtfRetestReversalStrategyService', displayName: '5m高低点双棒确认反转策略' }
]);

const fetchStrategies = async () => {
  try {
    const res = await axios.get(`${baseUrl}/api/backtest/strategies`);
    if (res.data && res.data.length > 0) {
      strategies.value = res.data;
    }
  } catch (err) {
    console.error('获取策略列表失败', err);
  }
};

// Computed detailed fees for the report details
const totalTransactionFees = computed(() => {
  if (!selectedReport.value || !selectedReport.value.report || !selectedReport.value.report.orderDetails) return 0;
  const sum = selectedReport.value.report.orderDetails.reduce((acc: number, o: any) => {
    return acc + (o.openFee || 0) + (o.closeFee || 0);
  }, 0);
  return parseFloat(sum.toFixed(4));
});

const totalFundingFees = computed(() => {
  if (!selectedReport.value || !selectedReport.value.report || !selectedReport.value.report.orderDetails) return 0;
  const sum = selectedReport.value.report.orderDetails.reduce((acc: number, o: any) => {
    return acc + (o.fundingFeePaid || 0);
  }, 0);
  return parseFloat(sum.toFixed(4));
});

// Filters & Pagination
const filterDirection = ref('ALL');
const filterReason = ref('ALL');
const currentPage = ref(1);
const pageSize = 10;

// Setup default times (default to past 7 days)
const getDefaultDates = () => {
  const end = new Date();
  const start = new Date();
  start.setDate(end.getDate() - 3); // Default to 3 days back for fast response
  
  // Format to local ISO (YYYY-MM-DDTHH:mm)
  const pad = (num: number) => String(num).padStart(2, '0');
  const format = (d: Date) => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
  
  return {
    start: format(start),
    end: format(end)
  };
};

const dates = getDefaultDates();

const form = reactive({
  symbol: 'BTCUSDT',
  strategyName: 'VReversalStrategyService',
  timeframe: '1m',
  startTimeStr: dates.start,
  endTimeStr: dates.end,
  initialBalance: 1000,
  leverage: 20,
  takerSlippagePercent: 0.05, // 0.05%
  makerSlippagePercent: 0.03, // 0.03%
  feeRatePercent: 0.05,       // 0.05%
  fundingRatePercent: 0.02,   // 0.02%
  enableMoveStopToBE: true    // 开启保本移动止损
});

const statusTextMap: Record<string, string> = {
  'Queued': '排队中 ⌛',
  'Running': '进行中 ⚡',
  'Paused': '已暂停 ⏸️',
  'Completed': '已完成 🎉',
  'Cancelled': '已取消 ⏹️',
  'Failed': '已失败 💥'
};

const closeReasonMap: Record<string, string> = {
  'TakeProfit': '止盈 (TP) 🎯',
  'StopLoss': '止损 (SL) 🛑',
  'BreakEven': '保本 (BE) 🛡️',
  'Liquidation': '保证金强平 💀',
  'EndOfBacktest': '回测结束平仓 🏁'
};

// Computed Task sorting (default strictly by creation time descending)
const sortedTasks = computed(() => {
  return [...tasks.value].sort((a, b) => {
    const timeA = a.createdAt ? new Date(a.createdAt).getTime() : 0;
    const timeB = b.createdAt ? new Date(b.createdAt).getTime() : 0;
    if (timeA !== timeB) return timeB - timeA;
    return b.taskId.localeCompare(a.taskId);
  });
});

// Fetch tasks lists
const fetchTasks = async () => {
  try {
    const res = await axios.get(`${baseUrl}/api/backtest/tasks`);
    tasks.value = res.data.map((t: any) => ({ ...t, status: getStatusString(t.status) }));
  } catch (err) {
    console.error('获取回测任务列表失败', err);
  }
};

// Submit backtest task
const submitBacktest = async () => {
  submitting.value = true;
  try {
    // Parse times to ISO standard string
    const startTime = new Date(form.startTimeStr).toISOString();
    const endTime = new Date(form.endTimeStr).toISOString();

    const config = {
      symbol: form.symbol,
      strategyName: form.strategyName,
      timeframe: form.timeframe,
      startTime: startTime,
      endTime: endTime,
      initialBalance: form.initialBalance,
      leverage: form.leverage,
      takerSlippage: form.takerSlippagePercent / 100, // percentage to decimal
      makerSlippage: form.makerSlippagePercent / 100,
      feeRate: form.feeRatePercent / 100,
      fundingRate: form.fundingRatePercent / 100,
      enableMoveStopToBE: form.enableMoveStopToBE
    };

    const res = await axios.post(`${baseUrl}/api/backtest/run`, config);
    // Add to list immediately
    const newTask = { ...res.data, status: getStatusString(res.data.status) };
    tasks.value.push(newTask);
    submitting.value = false;
  } catch (err: any) {
    submitting.value = false;
    alert(`任务提交失败: ${err.response?.data?.message || err.message}`);
  }
};

// Queue operations
const pauseTask = async (id: string) => {
  try {
    await axios.post(`${baseUrl}/api/backtest/pause/${id}`);
    fetchTasks();
  } catch (err) {}
};

const resumeTask = async (id: string) => {
  try {
    await axios.post(`${baseUrl}/api/backtest/resume/${id}`);
    fetchTasks();
  } catch (err) {}
};

const cancelTask = async (id: string) => {
  if (confirm(`确定要取消任务 #${id} 吗？`)) {
    try {
      await axios.post(`${baseUrl}/api/backtest/cancel/${id}`);
      fetchTasks();
    } catch (err) {}
  }
};

// View detailed report for a completed task
const viewReport = async (id: string) => {
  clearSelectedTrade();
  try {
    const res = await axios.get(`${baseUrl}/api/backtest/report/${id}`);
    selectedTaskId.value = id;
    selectedReport.value = res.data;
    
    // Reset filters
    filterDirection.value = 'ALL';
    filterReason.value = 'ALL';
    currentPage.value = 1;

    // Render chart in next tick when DOM is updated
    nextTick(() => {
      renderEquityChart();
    });
  } catch (err) {
    alert(`获取报告失败: ${err}`);
  }
};

// Filtered and paginated trade details
const filteredTrades = computed(() => {
  if (!selectedReport.value || !selectedReport.value.report.orderDetails) return [];
  return selectedReport.value.report.orderDetails.filter((t: any) => {
    const matchDir = filterDirection.value === 'ALL' || t.direction === filterDirection.value;
    const matchReason = filterReason.value === 'ALL' || t.closeReason === filterReason.value;
    return matchDir && matchReason;
  });
});

const totalPages = computed(() => {
  return Math.ceil(filteredTrades.value.length / pageSize);
});

const paginatedTrades = computed(() => {
  const start = (currentPage.value - 1) * pageSize;
  return filteredTrades.value.slice(start, start + pageSize);
});

// Watch filters to reset page
watch([filterDirection, filterReason], () => {
  currentPage.value = 1;
});

// SignalR Realtime Progress Update
const connectSignalR = () => {
  signalrConnection = new signalR.HubConnectionBuilder()
    .withUrl(`${baseUrl}/hubs/backtest`)
    .withAutomaticReconnect()
    .build();

  signalrConnection.on('ReceiveBacktestProgress', (data: any) => {
    // Update local task progress
    const idx = tasks.value.findIndex(t => t.taskId === data.taskId);
    const normalizedStatus = getStatusString(data.status);
    if (idx !== -1) {
      tasks.value[idx].progress = data.progress;
      tasks.value[idx].tradesCount = data.tradesCount;
      tasks.value[idx].currentBalance = data.currentBalance;
      
      const oldStatus = tasks.value[idx].status;
      tasks.value[idx].status = normalizedStatus;

      // If the currently viewed report is the one updating and it just completed, auto-reload
      if (selectedTaskId.value === data.taskId && normalizedStatus === 'Completed') {
        viewReport(data.taskId);
      }

      // If status changed to Completed, refresh tasks to fetch report details (like volatility and leverage)
      if (oldStatus !== 'Completed' && normalizedStatus === 'Completed') {
        fetchTasks();
      }
    } else {
      // If task is not in list, fetch tasks list
      fetchTasks();
    }
  });

  signalrConnection.start()
    .then(() => console.log('🔌 BacktestHub SignalR connected.'))
    .catch(err => console.error('SignalR BacktestHub Connection Error: ', err));
};

// Render ECharts Equity Curve
const renderEquityChart = () => {
  if (!chartRef.value || !selectedReport.value) return;

  if (chartInstance) {
    chartInstance.dispose();
  }

  chartInstance = echarts.init(chartRef.value, 'dark');

  const curveData = selectedReport.value.report.equityCurve || [];
  const dates = curveData.map((pt: any) => formatDateTime(pt.time));
  const balances = curveData.map((pt: any) => parseFloat(pt.balance.toFixed(2)));

  const option = {
    backgroundColor: '#161b22',
    title: {
      text: '账户权益曲线 (USDT)',
      left: 'center',
      textStyle: { color: '#c9d1d9', fontSize: 14 }
    },
    tooltip: {
      trigger: 'axis',
      backgroundColor: '#0d1117',
      borderColor: '#30363d',
      textStyle: { color: '#c9d1d9' },
      formatter: (params: any) => {
        const p = params[0];
        return `时间: ${p.name}<br/>余额: <strong>${p.value} USDT</strong>`;
      }
    },
    grid: {
      top: '15%',
      left: '5%',
      right: '5%',
      bottom: '10%',
      containLabel: true
    },
    xAxis: {
      type: 'category',
      data: dates,
      axisLine: { lineStyle: { color: '#30363d' } },
      axisLabel: { color: '#8b949e' }
    },
    yAxis: {
      type: 'value',
      axisLine: { lineStyle: { color: '#30363d' } },
      axisLabel: { color: '#8b949e' },
      splitLine: { lineStyle: { color: '#21262d' } },
      scale: true
    },
    series: [
      {
        name: 'Balance',
        type: 'line',
        data: balances,
        smooth: true,
        showSymbol: false,
        lineStyle: { width: 3, color: '#58a6ff' },
        areaStyle: {
          color: new echarts.graphic.LinearGradient(0, 0, 0, 1, [
            { offset: 0, color: 'rgba(88, 166, 255, 0.4)' },
            { offset: 1, color: 'rgba(88, 166, 255, 0)' }
          ])
        }
      }
    ]
  };

  chartInstance.setOption(option);
};

// Utilities
const formatDateTime = (val: string) => {
  if (!val) return '';
  const date = new Date(val);
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}`;
};

const formatDuration = (mins: number) => {
  if (!mins) return '0分';
  if (mins < 60) return `${Math.round(mins)}分钟`;
  const hrs = Math.floor(mins / 60);
  const remainingMins = Math.round(mins % 60);
  return `${hrs}小时 ${remainingMins}分钟`;
};

const getPnlClass = (val: number) => {
  if (val > 0) return 'text-green';
  if (val < 0) return 'text-red';
  return 'text-white';
};

// Window resize listener for chart responsiveness
const handleResize = () => {
  if (chartInstance) {
    chartInstance.resize();
  }
  if (lwChartInstance && klineChartRef.value) {
    lwChartInstance.resize(klineChartRef.value.clientWidth, 380);
  }
};

// Clear selected trade and remove chart
const clearSelectedTrade = () => {
  selectedTrade.value = null;
  klineHoverData.value = null;
  klineChartData.value = [];
  if (lwChartInstance) {
    lwChartInstance.remove();
    lwChartInstance = null;
    lwVolumeSeries = null;
    lwCandlestickSeries = null;
  }
};

// Convert timeframe (e.g. "1m", "1h") to seconds
const timeframeToSeconds = (timeframe: string): number => {
  const num = parseInt(timeframe) || 1;
  if (timeframe.endsWith('m')) return num * 60;
  if (timeframe.endsWith('h')) return num * 3600;
  if (timeframe.endsWith('d')) return num * 86400;
  return 60;
};

// Format time in seconds for the hover legend
const formatKlineDateTime = (timeSec: number) => {
  const date = new Date(timeSec * 1000);
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}`;
};

// Select trade to display K-line chart
const selectTrade = async (trade: any) => {
  selectedTrade.value = trade;
  // Initialize the timeframe view to the backtest timeframe
  klineTimeframe.value = selectedReport.value?.config?.timeframe || form.timeframe || '1m';
  klineHoverData.value = null;
  await loadTradeKlines();
};

// Change timeframe of K-line view
const changeKlineTimeframe = async (tf: string) => {
  if (klineTimeframe.value === tf) return;
  klineTimeframe.value = tf;
  klineHoverData.value = null;
  await loadTradeKlines();
};

// Load K-lines for selected trade around its execution time window
const loadTradeKlines = async () => {
  const trade = selectedTrade.value;
  if (!trade) return;
  
  try {
    const openTimeMs = new Date(trade.openTime).getTime();
    const closeTimeMs = trade.closeTime ? new Date(trade.closeTime).getTime() : Date.now();
    
    // Calculate timeframe seconds
    const intervalSec = timeframeToSeconds(klineTimeframe.value);
    
    // Calculate query range: load 50 bars before open and 150 bars after close (limit to 1000 bars total)
    const endMs = closeTimeMs + 150 * intervalSec * 1000;
    
    // Fetch historical klines
    const klines = await MarketAPI.getHistoricalKlines(trade.symbol, klineTimeframe.value, 1000, endMs);
    
    if (!klines || klines.length === 0) {
      console.error('未获取到对应的历史 K 线数据');
      return;
    }
    
    klineChartData.value = klines;
    
    // Wait for DOM to render the container
    nextTick(() => {
      initKlineChart(klineChartData.value, trade);
    });
  } catch (err) {
    console.error('加载交易 K 线数据失败', err);
  }
};

const toggleHighLowPoints = () => {
  if (selectedTrade.value && klineChartData.value.length > 0) {
    updateKlineMarkers(klineChartData.value, selectedTrade.value);
  }
};

// Update K-line trade execution markers on chart
const updateKlineMarkers = async (klines: any[], trade: any) => {
  if (!lwCandlestickSeries || klines.length === 0) return;

  const markers: any[] = [];
  const openTimeSec = Math.floor(new Date(trade.openTime).getTime() / 1000);
  
  // Find closest K-line to openTimeSec
  const closestOpen = klines.reduce((prev, curr) => {
    return Math.abs(curr.time - openTimeSec) < Math.abs(prev.time - openTimeSec) ? curr : prev;
  });

  const isLong = trade.direction === 'LONG';
  
  // 1. Open Position Marker
  markers.push({
    time: closestOpen.time,
    position: isLong ? 'belowBar' : 'aboveBar',
    color: isLong ? '#2ea043' : '#f85149',
    shape: isLong ? 'arrowUp' : 'arrowDown',
    text: `${isLong ? '开多' : '开空'} (${trade.openPrice.toFixed(4)})`,
    size: 1.5,
  });

  // 2. Close Position Marker
  if (trade.closeTime && trade.closePrice !== null) {
    const closeTimeSec = Math.floor(new Date(trade.closeTime).getTime() / 1000);
    const closestClose = klines.reduce((prev, curr) => {
      return Math.abs(curr.time - closeTimeSec) < Math.abs(prev.time - closeTimeSec) ? curr : prev;
    });

    let closeColor = '#d29922'; // default: yellow/orange for TP
    let closeShape: 'arrowUp' | 'arrowDown' | 'circle' = isLong ? 'arrowDown' : 'arrowUp';
    
    if (trade.closeReason === 'StopLoss') {
      closeColor = '#f85149'; // red for SL
    } else if (trade.closeReason === 'BreakEven') {
      closeColor = '#58a6ff'; // blue for BE
    } else if (trade.closeReason === 'Liquidation') {
      closeColor = '#f03e3e'; // dark red for Liq
      closeShape = 'circle';
    }

    markers.push({
      time: closestClose.time,
      position: isLong ? 'aboveBar' : 'belowBar',
      color: closeColor,
      shape: closeShape,
      text: `${closeReasonMap[trade.closeReason] || trade.closeReason} (${trade.closePrice.toFixed(4)})`,
      size: 1.5,
    });
  }

  // 3. High & Low Points Markers during holding period using AnalysisController
  if (showHighLowPoints.value) {
    try {
      const closeTimeSec = trade.closeTime ? Math.floor(new Date(trade.closeTime).getTime() / 1000) : klines[klines.length - 1].time;
      const holdKlines = klines.filter(k => k.time >= openTimeSec && k.time <= closeTimeSec);
      
      if (holdKlines.length > 10) { // AnalysisController peaks requires req.LeftLen + req.RightLen bars (default 5+5=10)
        const times = holdKlines.map(k => k.time * 1000);
        const highs = holdKlines.map(k => k.high);
        const lows = holdKlines.map(k => k.low);

        const response = await axios.post(`${baseUrl}/api/analysis/peaks`, {
          times,
          highs,
          lows,
          leftLen: 5,
          rightLen: 5
        });

        if (response.data) {
          const apiPeaks = response.data.peaks || [];
          const apiValleys = response.data.valleys || [];

          apiPeaks.forEach((p: any) => {
            markers.push({
              time: Math.floor(p.time / 1000),
              position: 'aboveBar',
              color: '#ffc107', // Yellow for local peak
              shape: 'circle',
              text: `高 (${p.value.toFixed(4)})`,
              size: 1.0,
            });
          });

          apiValleys.forEach((v: any) => {
            markers.push({
              time: Math.floor(v.time / 1000),
              position: 'belowBar',
              color: '#17a2b8', // Cyan for local valley
              shape: 'circle',
              text: `低 (${v.value.toFixed(4)})`,
              size: 1.0,
            });
          });
        }
      }
      else if (holdKlines.length > 0) {
        // Fallback: If hold period is too short, just draw the single global max/min in the period
        const maxHighKline = holdKlines.reduce((prev, curr) => curr.high > prev.high ? curr : prev);
        const minLowKline = holdKlines.reduce((prev, curr) => curr.low < prev.low ? curr : prev);

        markers.push({
          time: maxHighKline.time,
          position: 'aboveBar',
          color: '#ffc107',
          shape: 'circle',
          text: `最高 (${maxHighKline.high.toFixed(4)})`,
          size: 1.0,
        });

        markers.push({
          time: minLowKline.time,
          position: 'belowBar',
          color: '#17a2b8',
          shape: 'circle',
          text: `最低 (${minLowKline.low.toFixed(4)})`,
          size: 1.0,
        });
      }
    } catch (err) {
      console.error('获取局部高低波值失败', err);
    }
  }

  // Sort markers chronologically to avoid lightweight-charts rendering order warning/bug
  markers.sort((a, b) => a.time - b.time);

  lwCandlestickSeries.setMarkers(markers);
};

// Drag and load more older historical K-lines (lazy load / prepend)
const loadMoreBacktestKlines = async () => {
  const trade = selectedTrade.value;
  if (!trade || isKlineLoadingMore || klineChartData.value.length === 0) return;
  
  isKlineLoadingMore = true;

  // Find oldest time in seconds
  const oldestTimeSec = klineChartData.value[0].time;
  
  // Calculate target endTime for fetching (1 second/period before oldest)
  const targetEndTimeMs = (oldestTimeSec - 1) * 1000;

  try {
    // Request 500 older candles
    const olderHistory = await MarketAPI.getHistoricalKlines(trade.symbol, klineTimeframe.value, 500, targetEndTimeMs);
    
    if (olderHistory && olderHistory.length > 0) {
      // Filter out any duplicates
      const safeOldData = olderHistory.filter((item: any) => item.time < oldestTimeSec);
      
      if (safeOldData.length > 0) {
        // Prepend to our array
        const newKlines = [...safeOldData, ...klineChartData.value];
        klineChartData.value = newKlines;

        // Apply updated data to Candlestick and Volume series
        lwCandlestickSeries.setData(newKlines);
        lwVolumeSeries.setData(newKlines.map((d: any) => {
          const isUp = d.close >= d.open;
          return {
            time: d.time,
            value: Number(d.volume !== undefined ? d.volume : 0),
            color: isUp ? 'rgba(46, 160, 67, 0.4)' : 'rgba(248, 81, 73, 0.4)',
          };
        }));

        // Re-calculate and set markers with new aligned data
        updateKlineMarkers(newKlines, trade);
      }
    }
  } catch (err) {
    console.error('加载更多历史 K 线失败:', err);
  } finally {
    isKlineLoadingMore = false;
  }
};

// Initialize lightweight-charts Candlestick chart
const initKlineChart = (klines: any[], trade: any) => {
  if (!klineChartRef.value) return;

  // Clear previous chart
  if (lwChartInstance) {
    lwChartInstance.remove();
    lwChartInstance = null;
    lwVolumeSeries = null;
    lwCandlestickSeries = null;
  }

  // Create chart instance
  lwChartInstance = createChart(klineChartRef.value, {
    width: klineChartRef.value.clientWidth || 800,
    height: 380,
    layout: {
      background: { color: '#0d1117' },
      textColor: '#c9d1d9',
    },
    grid: {
      vertLines: { color: '#21262d' },
      horzLines: { color: '#21262d' },
    },
    crosshair: {
      mode: 0, // CrosshairMode.Normal
    },
    rightPriceScale: {
      borderColor: '#30363d',
      scaleMargins: {
        top: 0.05,
        bottom: 0.25, // Bottom 25% margin for volume overlay
      },
    },
    timeScale: {
      borderColor: '#30363d',
      timeVisible: true,
      secondsVisible: false,
    },
  });

  // Candlestick Series
  lwCandlestickSeries = lwChartInstance.addCandlestickSeries({
    upColor: '#2ea043',
    downColor: '#f85149',
    borderUpColor: '#2ea043',
    borderDownColor: '#f85149',
    wickUpColor: '#2ea043',
    wickDownColor: '#f85149',
  });

  // Volume Series
  lwVolumeSeries = lwChartInstance.addHistogramSeries({
    priceFormat: { type: 'volume' },
    priceScaleId: '', // Overlay on same chart pane
  });

  // Scale margins for Volume Series
  lwVolumeSeries.priceScale().applyOptions({
    scaleMargins: {
      top: 0.8, // volume series occupies bottom 20%
      bottom: 0,
    },
  });

  // Load datasets
  lwCandlestickSeries.setData(klines);
  lwVolumeSeries.setData(klines.map((d: any) => {
    const isUp = d.close >= d.open;
    return {
      time: d.time,
      value: Number(d.volume !== undefined ? d.volume : 0),
      color: isUp ? 'rgba(46, 160, 67, 0.4)' : 'rgba(248, 81, 73, 0.4)',
    };
  }));

  // Build and apply Markers
  updateKlineMarkers(klines, trade);
  
  // Fit viewport content
  lwChartInstance.timeScale().fitContent();

  // 1. Subscribe to crosshair move for hover data legend
  lwChartInstance.subscribeCrosshairMove((param: any) => {
    if (
      !param.point ||
      !param.time ||
      param.point.x < 0 ||
      param.point.x > klineChartRef.value!.clientWidth ||
      param.point.y < 0 ||
      param.point.y > klineChartRef.value!.clientHeight
    ) {
      klineHoverData.value = null;
      return;
    }

    const candleData = param.seriesData.get(lwCandlestickSeries);
    const volumeData = param.seriesData.get(lwVolumeSeries);

    if (candleData) {
      const isUp = candleData.close >= candleData.open;
      const changePct = ((candleData.close - candleData.open) / candleData.open) * 100;
      klineHoverData.value = {
        time: formatKlineDateTime(Number(param.time)),
        open: candleData.open.toFixed(4),
        high: candleData.high.toFixed(4),
        low: candleData.low.toFixed(4),
        close: candleData.close.toFixed(4),
        change: `${changePct >= 0 ? '+' : ''}${changePct.toFixed(2)}%`,
        volume: volumeData ? Number(volumeData.value).toFixed(2) : '0.00',
        colorClass: isUp ? 'text-green' : 'text-red',
      };
    } else {
      klineHoverData.value = null;
    }
  });

  // 2. Subscribe to drag / visible range changes for lazy loading
  lwChartInstance.timeScale().subscribeVisibleLogicalRangeChange((logicalRange: any) => {
    if (logicalRange && logicalRange.from < 10 && !isKlineLoadingMore) {
      loadMoreBacktestKlines();
    }
  });
};

// Life Cycles
onMounted(() => {
  marketStore.connectAllTickers();
  loadAvailableSymbols();
  fetchTasks();
  fetchStrategies();
  connectSignalR();
  window.addEventListener('resize', handleResize);
  document.addEventListener('click', handleClickOutside);
});

onUnmounted(() => {
  if (signalrConnection) {
    signalrConnection.stop();
  }
  if (chartInstance) {
    chartInstance.dispose();
  }
  if (lwChartInstance) {
    lwChartInstance.remove();
    lwChartInstance = null;
    lwVolumeSeries = null;
    lwCandlestickSeries = null;
  }
  window.removeEventListener('resize', handleResize);
  document.removeEventListener('click', handleClickOutside);
});
</script>

<style scoped>
.backtest-container {
  padding: 20px;
  background-color: #010409;
  height: calc(100vh - 50px);
  overflow-y: auto;
  box-sizing: border-box;
  display: flex;
  flex-direction: column;
  gap: 20px;
}

.backtest-header {
  background: linear-gradient(135deg, #161b22 0%, #0d1117 100%);
  border: 1px solid #30363d;
  border-radius: 12px;
  padding: 16px 24px;
}

.header-title {
  display: flex;
  flex-direction: column;
}
.header-title h1 {
  margin: 0;
  font-size: 20px;
  font-weight: 800;
  color: #e6edf3;
  display: inline-flex;
  align-items: center;
  gap: 10px;
}
.header-title .icon {
  font-size: 24px;
}
.header-title .subtitle {
  margin: 4px 0 0 0;
  font-size: 13px;
  color: #8b949e;
}

/* Grid Layout */
.backtest-grid {
  display: grid;
  grid-template-columns: 450px 1fr;
  gap: 20px;
  align-items: start;
}

/* Card Pane styling */
.pane-card {
  background-color: #161b22;
  border: 1px solid #30363d;
  border-radius: 12px;
  padding: 20px;
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.3);
}

.card-header {
  display: flex;
  align-items: center;
  gap: 10px;
  border-bottom: 1px solid #21262d;
  padding-bottom: 12px;
  margin-bottom: 16px;
}
.card-header h2 {
  margin: 0;
  font-size: 15px;
  font-weight: 700;
  color: #f0f6fc;
  flex: 1;
}
.card-header .icon {
  font-size: 18px;
  color: #58a6ff;
}

/* Form Styles */
.config-form {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.form-row {
  display: flex;
  gap: 15px;
}

.form-group {
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.form-group label {
  font-size: 12px;
  color: #8b949e;
  font-weight: 600;
}

.form-group input,
.form-group select {
  background-color: #0d1117;
  border: 1px solid #30363d;
  border-radius: 6px;
  color: #c9d1d9;
  padding: 8px 12px;
  font-size: 13px;
  outline: none;
  transition: border-color 0.2s;
}

.form-group input:focus,
.form-group select:focus {
  border-color: #58a6ff;
}

.slider-container {
  display: flex;
  align-items: center;
  gap: 10px;
  height: 36px;
}

.range-slider {
  flex: 1;
  accent-color: #58a6ff;
  cursor: pointer;
}

.slider-val {
  font-size: 13px;
  font-weight: 700;
  color: #58a6ff;
  width: 40px;
  text-align: right;
}

.submit-btn {
  background: linear-gradient(180deg, #2ea043 0%, #238636 100%);
  border: 1px solid #30363d;
  border-radius: 6px;
  color: white;
  padding: 12px;
  font-size: 14px;
  font-weight: 700;
  cursor: pointer;
  display: flex;
  justify-content: center;
  align-items: center;
  gap: 8px;
  transition: filter 0.2s;
  margin-top: 10px;
}
.submit-btn:hover:not(:disabled) {
  filter: brightness(1.1);
}
.submit-btn:disabled {
  background: #21262d;
  color: #8b949e;
  cursor: not-allowed;
}

.loader {
  width: 16px;
  height: 16px;
  border: 2px solid #8b949e;
  border-bottom-color: transparent;
  border-radius: 50%;
  display: inline-block;
  animation: rotation 1s linear infinite;
}

@keyframes rotation {
  0% { transform: rotate(0deg); }
  100% { transform: rotate(360deg); }
}

/* Tasks List Pane */
.refresh-btn {
  background-color: #21262d;
  border: 1px solid #30363d;
  color: #c9d1d9;
  padding: 4px 10px;
  border-radius: 4px;
  font-size: 11px;
  cursor: pointer;
  font-weight: bold;
}
.refresh-btn:hover {
  background-color: #30363d;
}

.tasks-list {
  display: flex;
  flex-direction: column;
  gap: 12px;
  max-height: 520px;
  overflow-y: auto;
}

.no-tasks {
  text-align: center;
  padding: 40px 0;
  color: #8b949e;
  font-size: 13px;
  border: 1px dashed #30363d;
  border-radius: 8px;
}

.task-item {
  background-color: #0d1117;
  border: 1px solid #30363d;
  border-radius: 8px;
  padding: 16px;
  transition: all 0.2s;
}
.task-item:hover {
  border-color: #8b949e;
}
.task-item.completed { border-left: 4px solid #2ea043; }
.task-item.running { border-left: 4px solid #58a6ff; }
.task-item.paused { border-left: 4px solid #d29922; }
.task-item.failed { border-left: 4px solid #f85149; }
.task-item.cancelled { border-left: 4px solid #8b949e; }
.task-item.queued { border-left: 4px solid #c9d1d9; }

.task-info {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.task-meta {
  display: flex;
  justify-content: space-between;
  align-items: center;
}
.task-id {
  font-family: monospace;
  font-weight: 700;
  color: #8b949e;
}

.task-badge {
  font-size: 11px;
  padding: 2px 8px;
  border-radius: 12px;
  font-weight: 700;
}
.task-badge.completed { background-color: rgba(46, 160, 67, 0.15); color: #3fb950; border: 1px solid rgba(46, 160, 67, 0.4); }
.task-badge.running { background-color: rgba(88, 166, 255, 0.15); color: #58a6ff; border: 1px solid rgba(88, 166, 255, 0.4); }
.task-badge.paused { background-color: rgba(210, 153, 34, 0.15); color: #d29922; border: 1px solid rgba(210, 153, 34, 0.4); }
.task-badge.failed { background-color: rgba(248, 81, 73, 0.15); color: #f85149; border: 1px solid rgba(248, 81, 73, 0.4); }
.task-badge.cancelled { background-color: rgba(139, 148, 158, 0.15); color: #8b949e; border: 1px solid rgba(139, 148, 158, 0.4); }
.task-badge.queued { background-color: rgba(201, 209, 217, 0.15); color: #c9d1d9; border: 1px solid rgba(201, 209, 217, 0.4); }

.task-title {
  font-size: 14px;
  font-weight: 700;
  color: #e6edf3;
}
.task-time-range {
  font-size: 11px;
  color: #8b949e;
}

/* Progress bar inside task */
.progress-section {
  display: flex;
  flex-direction: column;
  gap: 4px;
  margin-top: 4px;
}
.progress-bar-bg {
  background-color: #21262d;
  height: 6px;
  border-radius: 3px;
  overflow: hidden;
}
.progress-bar {
  background-color: #58a6ff;
  height: 100%;
  border-radius: 3px;
  transition: width 0.3s;
}
.progress-stats {
  display: flex;
  justify-content: space-between;
  font-size: 11px;
  color: #8b949e;
}

.task-actions {
  display: flex;
  gap: 8px;
  margin-top: 8px;
}

.action-btn {
  padding: 4px 10px;
  border-radius: 4px;
  font-size: 11px;
  font-weight: 700;
  cursor: pointer;
  border: 1px solid #30363d;
  background-color: #21262d;
  color: #c9d1d9;
}
.action-btn:hover {
  background-color: #30363d;
}
.action-btn.cancel { background-color: rgba(248, 81, 73, 0.1); border-color: rgba(248, 81, 73, 0.3); color: #f85149; }
.action-btn.cancel:hover { background-color: rgba(248, 81, 73, 0.2); }
.action-btn.view-report { background-color: rgba(46, 160, 67, 0.1); border-color: rgba(46, 160, 67, 0.3); color: #3fb950; }
.action-btn.view-report:hover { background-color: rgba(46, 160, 67, 0.2); }
.action-btn.view-report.active { background-color: #238636; border-color: #2ea043; color: white; }

.error-msg {
  font-size: 11px;
  color: #f85149;
  background-color: rgba(248, 81, 73, 0.1);
  padding: 6px;
  border-radius: 4px;
  border: 1px solid rgba(248, 81, 73, 0.2);
  word-break: break-all;
}

/* Report Panel Details */
.report-section {
  display: flex;
  flex-direction: column;
  gap: 20px;
  margin-bottom: 20px;
}
.report-meta {
  display: flex;
  gap: 15px;
  font-size: 12px;
  color: #8b949e;
}
.report-meta strong {
  color: #e6edf3;
}

/* Stats grid */
.stats-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(140px, 1fr));
  gap: 15px;
}

.task-report-mini-stats {
  display: flex;
  justify-content: space-between;
  font-size: 11px;
  color: #8b949e;
  border-top: 1px dashed #30363d;
  padding-top: 6px;
  margin-top: 6px;
}

.stat-card {
  background-color: #0d1117;
  border: 1px solid #30363d;
  border-radius: 8px;
  padding: 16px;
  text-align: center;
}

.stat-label {
  font-size: 12px;
  color: #8b949e;
  margin-bottom: 4px;
  font-weight: 600;
}
.stat-value {
  font-size: 18px;
  font-weight: 800;
}
.stat-sub {
  font-size: 11px;
  color: #8b949e;
  margin-top: 4px;
}

/* Report Details Grid (Chart and Table) */
.report-details-grid {
  display: grid;
  grid-template-columns: 1fr;
  gap: 20px;
}

.chart-container {
  background-color: #0d1117;
  border: 1px solid #30363d;
  border-radius: 8px;
  padding: 16px;
}
.chart-container h3 {
  margin: 0 0 16px 0;
  font-size: 14px;
  color: #f0f6fc;
}

.equity-chart {
  height: 350px;
  width: 100%;
}

.history-container {
  background-color: #0d1117;
  border: 1px solid #30363d;
  border-radius: 8px;
  padding: 16px;
}

.history-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 16px;
}
.history-header h3 {
  margin: 0;
  font-size: 14px;
  color: #f0f6fc;
}
.history-filters {
  display: flex;
  gap: 10px;
}
.filter-select {
  background-color: #161b22;
  border: 1px solid #30363d;
  border-radius: 4px;
  color: #c9d1d9;
  padding: 4px 8px;
  font-size: 12px;
  outline: none;
}

/* Table styling */
.table-wrapper {
  overflow-x: auto;
}

.trades-table {
  width: 100%;
  border-collapse: collapse;
  font-size: 12px;
  text-align: left;
}
.trades-table th {
  background-color: #161b22;
  color: #8b949e;
  padding: 10px 12px;
  font-weight: 600;
  border-bottom: 1px solid #30363d;
}
.trades-table td {
  padding: 12px;
  border-bottom: 1px solid #21262d;
  vertical-align: middle;
}
.trades-table tr:hover td {
  background-color: rgba(48, 54, 61, 0.2);
}

.order-id {
  font-family: monospace;
  font-weight: 700;
  background-color: #21262d;
  padding: 2px 6px;
  border-radius: 4px;
  color: #c9d1d9;
}

.dir-badge {
  font-weight: bold;
  padding: 2px 6px;
  border-radius: 4px;
  font-size: 11px;
}
.dir-badge.long { background-color: rgba(46, 160, 67, 0.15); color: #3fb950; }
.dir-badge.short { background-color: rgba(248, 81, 73, 0.15); color: #f85149; }

.cell-detail {
  line-height: 1.5;
}
.sub-text {
  font-size: 10px;
  color: #8b949e;
}
.badge-type {
  background-color: #30363d;
  color: #c9d1d9;
  padding: 1px 4px;
  border-radius: 3px;
  font-size: 9px;
  text-transform: uppercase;
}

.close-reason {
  display: inline-block;
  font-size: 10px;
  padding: 1px 6px;
  border-radius: 4px;
  font-weight: 700;
  margin-top: 4px;
}
.close-reason.takeprofit { background-color: rgba(46, 160, 67, 0.2); color: #2ea043; }
.close-reason.stoploss { background-color: rgba(248, 81, 73, 0.2); color: #f85149; }
.close-reason.breakeven { background-color: rgba(88, 166, 255, 0.2); color: #58a6ff; }
.close-reason.liquidation { background-color: rgba(240, 62, 62, 0.3); color: #f03e3e; font-weight: bold; text-decoration: underline; }
.close-reason.endofbacktest { background-color: rgba(139, 148, 158, 0.2); color: #8b949e; }

.status-badge {
  font-size: 11px;
  padding: 2px 6px;
  border-radius: 4px;
  font-weight: 700;
}
.status-badge.running { background-color: rgba(88, 166, 255, 0.15); color: #58a6ff; }

.pnl-value {
  font-weight: 700;
  font-size: 13px;
}
.pnl-roi {
  font-size: 11px;
}

.no-data {
  text-align: center;
  color: #8b949e;
  padding: 30px 0;
}

/* K-line inline visualization container */
.kline-container {
  display: flex;
  flex-direction: column;
  gap: 15px;
  background-color: #161b22;
  border: 1px solid #30363d;
  border-radius: 12px;
  padding: 20px;
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.3);
}

.kline-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  border-bottom: 1px solid #21262d;
  padding-bottom: 12px;
  flex-wrap: wrap;
  gap: 12px;
}

.kline-title {
  display: flex;
  align-items: center;
  gap: 8px;
}

.kline-title h3 {
  margin: 0;
  font-size: 14px;
  font-weight: 700;
  color: #f0f6fc;
}

.kline-title .icon {
  font-size: 16px;
  color: #58a6ff;
}

.kline-meta-info {
  display: flex;
  align-items: center;
  gap: 12px;
  font-size: 12px;
  color: #8b949e;
}

.symbol-badge {
  background-color: #21262d;
  color: #c9d1d9;
  padding: 2px 8px;
  border-radius: 4px;
  font-weight: bold;
}

.price-info {
  display: inline-flex;
  gap: 8px;
  align-items: center;
}

.close-kline-btn {
  background-color: rgba(248, 81, 73, 0.1);
  border: 1px solid rgba(248, 81, 73, 0.3);
  color: #f85149;
  padding: 4px 10px;
  border-radius: 4px;
  font-size: 11px;
  font-weight: bold;
  cursor: pointer;
}

.close-kline-btn:hover {
  background-color: rgba(248, 81, 73, 0.2);
}

.kline-chart-wrapper {
  position: relative;
  width: 100%;
}

.kline-hover-panel {
  position: absolute;
  top: 10px;
  left: 10px;
  background-color: rgba(13, 17, 23, 0.85);
  border: 1px solid #30363d;
  border-radius: 6px;
  padding: 6px 12px;
  display: flex;
  flex-wrap: wrap;
  gap: 12px;
  font-family: monospace;
  font-size: 11px;
  color: #c9d1d9;
  pointer-events: none;
  z-index: 10;
  backdrop-filter: blur(4px);
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.4);
}

.hover-time {
  color: #8b949e;
  font-weight: bold;
}

.hover-item {
  display: flex;
  gap: 3px;
}

.kline-timeframes {
  display: flex;
  gap: 4px;
  background-color: #0d1117;
  padding: 2px;
  border-radius: 6px;
  border: 1px solid #30363d;
}

.tf-btn {
  background: transparent;
  border: none;
  color: #8b949e;
  padding: 3px 8px;
  font-size: 11px;
  font-weight: bold;
  border-radius: 4px;
  cursor: pointer;
  transition: all 0.2s;
}

.tf-btn:hover {
  background-color: #21262d;
  color: #c9d1d9;
}

.tf-btn.active {
  background-color: #2ea043;
  color: #ffffff;
}

.kline-toggle-options {
  display: flex;
  align-items: center;
  gap: 6px;
  background-color: #0d1117;
  padding: 4px 10px;
  border-radius: 6px;
  border: 1px solid #30363d;
  font-size: 12px;
  color: #8b949e;
  user-select: none;
}

.toggle-checkbox-label {
  display: flex;
  align-items: center;
  gap: 6px;
  cursor: pointer;
}

.toggle-checkbox-label input {
  cursor: pointer;
  accent-color: #2ea043;
}

.trade-kline-chart {
  height: 380px;
  width: 100%;
  border-radius: 8px;
  overflow: hidden;
  background-color: #0d1117;
  border: 1px solid #30363d;
}

.kline-legend {
  display: flex;
  gap: 20px;
  font-size: 11px;
  color: #8b949e;
  flex-wrap: wrap;
  padding-top: 4px;
}

.trades-table tbody tr {
  cursor: pointer;
  transition: background-color 0.2s;
}

.trades-table tbody tr.active-row td {
  background-color: rgba(56, 139, 253, 0.15) !important;
  border-bottom: 1px solid #388bfd !important;
}

/* Pagination styles */
.pagination {
  display: flex;
  justify-content: center;
  align-items: center;
  gap: 15px;
  margin-top: 16px;
  padding-top: 12px;
  border-top: 1px solid #21262d;
}
.page-btn {
  background-color: #21262d;
  border: 1px solid #30363d;
  color: #c9d1d9;
  padding: 4px 12px;
  border-radius: 4px;
  font-size: 12px;
  cursor: pointer;
}
.page-btn:hover:not(:disabled) {
  background-color: #30363d;
}
.page-btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}
.page-info {
  font-size: 12px;
  color: #8b949e;
}

/* Helper text colors */
.text-green { color: #3fb950 !important; }
.text-red { color: #f85149 !important; }
.text-blue { color: #58a6ff !important; }
.text-orange { color: #d29922 !important; }
.text-white { color: #f0f6fc !important; }

/* Custom Select Container */
.custom-select-container {
  position: relative;
  width: 100%;
}

.custom-select-trigger {
  background-color: #0d1117;
  border: 1px solid #30363d;
  border-radius: 6px;
  color: #c9d1d9;
  padding: 8px 12px;
  font-size: 13px;
  cursor: pointer;
  display: flex;
  justify-content: space-between;
  align-items: center;
  user-select: none;
  transition: all 0.2s;
  min-height: 38px;
  box-sizing: border-box;
}

.custom-select-trigger:hover,
.custom-select-trigger.dropdown-open {
  border-color: #58a6ff;
  box-shadow: 0 0 0 2px rgba(88, 166, 255, 0.15);
}

.trigger-left {
  display: flex;
  align-items: center;
  gap: 8px;
}

.symbol-name {
  font-weight: 700;
  color: #f0f6fc;
}

.symbol-price-badge {
  font-size: 11px;
  color: #8b949e;
  background-color: #21262d;
  padding: 1px 6px;
  border-radius: 4px;
}

.trigger-right {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 11px;
}

.arrow-icon {
  font-size: 10px;
  color: #8b949e;
  transition: transform 0.2s;
}

.custom-select-trigger.dropdown-open .arrow-icon {
  transform: rotate(180deg);
}

/* Dropdown Card */
.custom-select-dropdown {
  position: absolute;
  top: calc(100% + 6px);
  left: 0;
  right: 0;
  background-color: #161b22;
  border: 1px solid #30363d;
  border-radius: 8px;
  box-shadow: 0 8px 24px rgba(0, 0, 0, 0.5);
  z-index: 999;
  display: flex;
  flex-direction: column;
  overflow: hidden;
  animation: dropdown-fade-in 0.15s ease-out;
}

@keyframes dropdown-fade-in {
  from { opacity: 0; transform: translateY(-4px); }
  to { opacity: 1; transform: translateY(0); }
}

/* Search Wrapper */
.dropdown-search-wrapper {
  position: relative;
  padding: 10px 12px;
  border-bottom: 1px solid #21262d;
  display: flex;
  align-items: center;
  background-color: #0d1117;
}

.search-icon {
  position: absolute;
  left: 20px;
  font-size: 12px;
  color: #8b949e;
}

.dropdown-search-wrapper input {
  width: 100%;
  background-color: #161b22 !important;
  border: 1px solid #30363d !important;
  border-radius: 6px;
  color: #c9d1d9;
  padding: 6px 10px 6px 30px !important;
  font-size: 12px;
  outline: none;
  transition: border-color 0.2s;
  box-sizing: border-box;
}

.dropdown-search-wrapper input:focus {
  border-color: #58a6ff !important;
  box-shadow: 0 0 0 2px rgba(88, 166, 255, 0.1) !important;
}

.clear-search-btn {
  position: absolute;
  right: 20px;
  background: transparent;
  border: none;
  color: #8b949e;
  font-size: 11px;
  cursor: pointer;
  padding: 2px;
}

.clear-search-btn:hover {
  color: #f0f6fc;
}

/* Sort Tabs */
.dropdown-sort-tabs {
  display: flex;
  padding: 6px 12px;
  background-color: #161b22;
  border-bottom: 1px solid #21262d;
  gap: 6px;
}

.sort-tab-btn {
  flex: 1;
  background-color: transparent;
  border: 1px solid transparent;
  color: #8b949e;
  padding: 4px 0;
  font-size: 11px;
  font-weight: 600;
  border-radius: 4px;
  cursor: pointer;
  transition: all 0.2s;
  text-align: center;
}

.sort-tab-btn:hover {
  background-color: #21262d;
  color: #c9d1d9;
}

.sort-tab-btn.active {
  background-color: #21262d;
  border-color: #30363d;
  color: #58a6ff;
}

/* Symbols List */
.dropdown-symbols-list {
  max-height: 250px;
  overflow-y: auto;
  padding: 4px;
  background-color: #0d1117;
}

.symbols-loading-state,
.no-symbols-found {
  padding: 20px 0;
  text-align: center;
  color: #8b949e;
  font-size: 12px;
}

.mini-loader {
  display: inline-block;
  width: 12px;
  height: 12px;
  border: 2px solid #8b949e;
  border-bottom-color: transparent;
  border-radius: 50%;
  animation: rotation 1s linear infinite;
  margin-right: 6px;
  vertical-align: middle;
}

.symbol-row-item {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 8px 12px;
  border-radius: 6px;
  cursor: pointer;
  transition: all 0.15s;
  user-select: none;
}

.symbol-row-item:hover {
  background-color: #21262d;
}

.symbol-row-item.selected {
  background-color: rgba(56, 139, 253, 0.15);
}

.symbol-row-item.selected .symbol-label {
  color: #58a6ff;
}

.symbol-cell-left {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.symbol-label {
  font-size: 12px;
  font-weight: 700;
  color: #f0f6fc;
}

.symbol-sub {
  font-size: 10px;
  color: #8b949e;
}

.symbol-cell-right {
  display: flex;
  flex-direction: column;
  align-items: flex-end;
  gap: 2px;
}

.symbol-price {
  font-size: 12px;
  font-weight: 600;
  color: #c9d1d9;
}

.symbol-stats {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 10px;
}

.symbol-pct-change {
  font-weight: 600;
}

.symbol-vol {
  color: #8b949e;
}

/* Scrollbar styling for custom dropdown list */
.custom-scrollbar::-webkit-scrollbar {
  width: 6px;
}
.custom-scrollbar::-webkit-scrollbar-track {
  background: #0d1117;
}
.custom-scrollbar::-webkit-scrollbar-thumb {
  background: #30363d;
  border-radius: 3px;
}
.custom-scrollbar::-webkit-scrollbar-thumb:hover {
  background: #8b949e;
}
</style>

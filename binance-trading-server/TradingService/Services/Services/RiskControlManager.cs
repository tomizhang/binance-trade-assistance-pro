using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Text.Json;
using TradingTerminal.Models;

namespace TradingTerminal.Services
{
    public class RiskControlManager
    {
        private const string STATE_FILE = "RiskState.json";
        private readonly ILogger<RiskControlManager> _logger;
        private readonly OrderChannel _orderChannel; // 🌟 1. 注入订单管道
        // 记录每个币种当前的连续亏损次数
        private readonly ConcurrentDictionary<string, int> _consecutiveLosses = new();
        private readonly ConcurrentDictionary<string, bool> _blacklistedSymbols = new();
        public bool IsGlobalTradingSuspended { get; private set; } = false;
        public string GlobalSuspendReason { get; private set; } = string.Empty;
        private const int MAX_CONSECUTIVE_LOSSES = 5;
        public event Action<string, int> OnSymbolBlacklisted;
        public event Action<string> OnGlobalMeltdown;
        public RiskControlManager(
            ILogger<RiskControlManager> logger,
            UserDataEventBus userDataBus,
        OrderChannel orderChannel) // 🌟 注入事件总线
        {
            _logger = logger;
            _orderChannel = orderChannel;
            // 🌟 挂载监听：只要有订单变动，就自动触发我的风控核查逻辑！
            userDataBus.OnOrderTradeUpdated += HandleOrderTradeUpdate;
        }
        private void HandleOrderTradeUpdate(OrderTradeUpdateEvent tradeEvent)
        {
            // 风控中心只关心 "有实际盈亏产生" 的平仓交易
            if (tradeEvent.RealizedPnl != 0)
            {
                // 调用原有的记录方法
                RecordTradeResult(tradeEvent.Symbol, tradeEvent.RealizedPnl);
                // 🌟 2. 核心联动：只要仓位平了，立刻通知消费者去把剩下的挂单撤掉！
                // 使用 Task.Run 丢进线程池，绝对不阻塞当前的事件总线
                _ = Task.Run(async () =>
                {
                    await _orderChannel.WriteAsync(new OrderSignal
                    {
                        Symbol = tradeEvent.Symbol,
                        Action = OrderAction.CancelAll,
                        StrategyName = "System_Cleanup",
                        Reason = "检测到仓位已平，自动清扫遗留挂单",
                        Message = "清理战场"
                    });
                });
            }
        }

        // ==============================
        // 1. 核心大闸：开仓权限校验s
        // ==============================
        public bool CanOpenPosition(string symbol, out string blockReason)
        {

            blockReason = string.Empty;
            return true;
            // 1. 检查全局熔断
            if (IsGlobalTradingSuspended)
            {
                blockReason = $"[全局熔断激活] 原因: {GlobalSuspendReason}";
                return false;
            }

            // 2. 检查单币种黑名单
            if (_blacklistedSymbols.TryGetValue(symbol.ToUpper(), out bool isBlacklisted) && isBlacklisted)
            {
                blockReason = $"[单币熔断激活] {symbol} 已连续亏损达到 {MAX_CONSECUTIVE_LOSSES} 次，交易已锁定";
                return false;
            }

            return true;
        }

        // ==============================
        // 2. 接收平仓结果，更新状态机
        // ==============================
        /// <summary>
        /// 当一笔交易平仓后，必须调用此方法来告诉风控中心是亏是赚
        /// </summary>
        public void RecordTradeResult(string symbol, decimal pnl)
        {
            string safeSymbol = symbol.ToUpper();

            if (pnl > 0)
            {
                // 如果盈利了，清空该币种的连亏记录
                _consecutiveLosses.AddOrUpdate(safeSymbol, 0, (_, _) => 0);
                _logger.LogInformation($"✅ [风控监控] {safeSymbol} 盈利平仓，连亏计数器已清零。");
            }
            else if (pnl < 0)
            {
                // 如果亏损，连亏次数 +1
                int currentLosses = _consecutiveLosses.AddOrUpdate(safeSymbol, 1, (_, old) => old + 1);
                _logger.LogWarning($"⚠️ [风控监控] {safeSymbol} 亏损平仓，当前连续亏损次数: {currentLosses}");

                // 触发单币熔断！
                if (currentLosses >= MAX_CONSECUTIVE_LOSSES)
                {
                    _blacklistedSymbols[safeSymbol] = true;
                    _logger.LogCritical($"🚨 [单币熔断] {safeSymbol} 连续亏损达到 {MAX_CONSECUTIVE_LOSSES} 次！已加入风控黑名单！");

                    // TODO: 蓝图第 3 阶段 - 这里要触发邮件/Telegram 通知模块
                    OnSymbolBlacklisted?.Invoke(safeSymbol, currentLosses);
                }
            }
            SaveState();
        }

        // ==============================
        // 3. 运维操作：解锁与全盘熔断
        // ==============================

        // 解锁单币种 (可通过 API 控制器调用)
        public void UnlockSymbol(string symbol)
        {
            string safeSymbol = symbol.ToUpper();
            _blacklistedSymbols[safeSymbol] = false;
            _consecutiveLosses[safeSymbol] = 0; // 连亏清零
            _logger.LogInformation($"🔓 [风控解除] {safeSymbol} 已被人工/系统解锁，恢复交易权限。");
            SaveState();
        }

        // 触发全局熔断 (例如：账户轮询发现回撤达到 30%)
        public void TriggerGlobalMeltdown(string reason)
        {
            IsGlobalTradingSuspended = true;
            GlobalSuspendReason = reason;
            _logger.LogCritical($"💥 [系统级熔断] 停止所有新开仓操作！原因: {reason}");
            // TODO: 发送紧急邮件
            OnGlobalMeltdown?.Invoke(reason);
        }

        // 解除全局熔断
        public void LiftGlobalMeltdown()
        {
            IsGlobalTradingSuspended = false;
            GlobalSuspendReason = string.Empty;
            _logger.LogInformation($"🕊️ [系统恢复] 全局熔断已解除，系统恢复正常接单。");
        }

        public void LoadState()
        {
            try
            {
                if (File.Exists(STATE_FILE))
                {
                    string json = File.ReadAllText(STATE_FILE);
                    using var doc = JsonDocument.Parse(json);

                    var losses = doc.RootElement.GetProperty("ConsecutiveLosses");
                    foreach (var item in losses.EnumerateObject())
                        _consecutiveLosses[item.Name] = item.Value.GetInt32();

                    var blacklists = doc.RootElement.GetProperty("BlacklistedSymbols");
                    foreach (var item in blacklists.EnumerateObject())
                        _blacklistedSymbols[item.Name] = item.Value.GetBoolean();

                    _logger.LogInformation("💾 [灾备恢复] 风控状态机已从本地硬盘成功恢复！");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ [灾备恢复] 读取风控状态失败: {ex.Message}");
            }
        }

        // 2. 保存记忆 (在状态发生改变时调用)
        private void SaveState()
        {
            try
            {
                var state = new
                {
                    ConsecutiveLosses = _consecutiveLosses,
                    BlacklistedSymbols = _blacklistedSymbols
                };
                string json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(STATE_FILE, json);
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ 保存风控状态失败: {ex.Message}");
            }
        }
    }
}
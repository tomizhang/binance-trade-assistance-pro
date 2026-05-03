using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TradingTerminal.Services
{
    /// <summary>
    /// 系统灾备恢复服务：开机第一剑，恢复所有上下文记忆！
    /// </summary>
    public class SystemRecoveryService : IHostedService
    {
        private readonly ILogger<SystemRecoveryService> _logger;
        private readonly RiskControlManager _riskManager;
        private readonly BinanceTradeWsService _tradeService;
        private readonly HeikinAshiService _haService;

        public SystemRecoveryService(
            ILogger<SystemRecoveryService> logger,
            RiskControlManager riskManager,
            BinanceTradeWsService tradeService,
            HeikinAshiService haService)
        {
            _logger = logger;
            _riskManager = riskManager;
            _tradeService = tradeService;
            _haService = haService;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("================================================");
            _logger.LogInformation("🛡️ [灾备启动] 系统正在执行全量状态恢复协议...");
            _logger.LogInformation("================================================");

            try
            {
                // 1. 恢复风控大脑记忆 (黑名单、连亏次数)
                _riskManager.LoadState();

                // 2. 远端查岗：去币安拉取当前未平仓的真实订单
                var activeSymbols = await _tradeService.GetActivePositionSymbolsAsync();

                if (activeSymbols.Any())
                {
                    _logger.LogWarning($"⚠️ [接管提醒] 发现遗留的持仓币种，正在强制唤醒策略引擎接管它们！");

                    // 3. 强制唤醒：把这些遗留的币种丢给 HA 策略引擎，
                    // 引擎会自动拉取它们的历史 1000 根 K线，并开启实盘监控，绝不脱管！
                    await _haService.UpdateWatchListAsync(activeSymbols);
                }
                else
                {
                    _logger.LogInformation("✅ [灾备完毕] 远端无遗留持仓，系统纯净启动。");
                }
            }
            catch (Exception ex)
            {
                _logger.LogCritical($"💥 [灾备致命错误] 系统恢复失败，请立即人工检查！错误: {ex.Message}");
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Binance.Net.Enums;

namespace WinFormsApp2
{
    /// <summary>
    /// 用户界面与 Linux 控制台参数配置持久化存储模型 (全面支持 settings.conf 配置文件)
    /// </summary>
    public class UserSettings
    {
        public string Symbol { get; set; } = "BTCUSDT";
        public List<string> SubscribedSymbols { get; set; } = new List<string> { "BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT" };
        public KlineInterval KlineInterval { get; set; } = KlineInterval.OneMinute;
        public DateTime StartDate { get; set; } = DateTime.Today.AddDays(-1);
        public DateTime EndDate { get; set; } = DateTime.Today;
        public int PlaybackIntervalMs { get; set; } = 500;
        public bool EnableTickPush { get; set; } = true;
        public bool AutoFitPrice { get; set; } = true;
        public bool HighlightHighVolume { get; set; } = true;

        // 趋势线回调策略参数
        public bool EnableStrategy { get; set; } = true;
        public int MinLineX1X2 { get; set; } = 40;
        public int MinLineAge { get; set; } = 80;
        public decimal TakeProfitPct { get; set; } = 1.5m;
        public decimal StopLossPct { get; set; } = 0.8m;
        public bool EnableWarmup { get; set; } = true;

        public static string SettingsConfPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.conf");

        /// <summary>
        /// 从本地 settings.conf 配置文件读取参数设置，若不存在则自动创建带详细注释的标准 settings.conf 文件
        /// </summary>
        public static UserSettings Load()
        {
            UserSettings settings = new UserSettings();

            try
            {
                if (!File.Exists(SettingsConfPath))
                {
                    settings.SaveToConfFile(SettingsConfPath);
                    return settings;
                }

                settings.LoadFromConfFile(SettingsConfPath);
                return settings;
            }
            catch
            {
                // 读取失败时返回默认配置
                return new UserSettings();
            }
        }

        public void Save()
        {
            try
            {
                SaveToConfFile(SettingsConfPath);
            }
            catch
            {
                // 忽略保存异常
            }
        }

        public void LoadFromConfFile(string path)
        {
            if (!File.Exists(path)) return;

            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            foreach (var rawLine in lines)
            {
                string line = rawLine.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#") || line.StartsWith(";"))
                {
                    continue; // 忽略注释与空行
                }

                int idx = line.IndexOf('=');
                if (idx < 0) idx = line.IndexOf(':');
                if (idx < 0) continue;

                string key = line.Substring(0, idx).Trim().ToLowerInvariant();
                string val = line.Substring(idx + 1).Trim();

                switch (key)
                {
                    case "symbols":
                    case "subscribedsymbols":
                        var syms = val.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim().ToUpper()).ToList();
                        if (syms.Count > 0)
                        {
                            SubscribedSymbols = syms;
                            Symbol = syms[0];
                        }
                        break;
                    case "symbol":
                        if (!string.IsNullOrEmpty(val))
                        {
                            Symbol = val.Trim().ToUpper();
                        }
                        break;
                    case "klineinterval":
                    case "interval":
                        KlineInterval = ParseKlineInterval(val);
                        break;
                    case "startdate":
                        if (DateTime.TryParse(val, out var sd)) StartDate = sd;
                        break;
                    case "enddate":
                        if (DateTime.TryParse(val, out var ed)) EndDate = ed;
                        break;
                    case "playbackintervalms":
                        if (int.TryParse(val, out var pms)) PlaybackIntervalMs = pms;
                        break;
                    case "enabletickpush":
                        if (bool.TryParse(val, out var etp)) EnableTickPush = etp;
                        break;
                    case "autofitprice":
                        if (bool.TryParse(val, out var afp)) AutoFitPrice = afp;
                        break;
                    case "highlighthighvolume":
                        if (bool.TryParse(val, out var hhv)) HighlightHighVolume = hhv;
                        break;
                    case "enablestrategy":
                        if (bool.TryParse(val, out var es)) EnableStrategy = es;
                        break;
                    case "enablewarmup":
                        if (bool.TryParse(val, out var ew)) EnableWarmup = ew;
                        break;
                    case "minlinex1x2":
                        if (int.TryParse(val, out var mlx)) MinLineX1X2 = mlx;
                        break;
                    case "minlineage":
                        if (int.TryParse(val, out var mla)) MinLineAge = mla;
                        break;
                    case "takeprofitpct":
                        if (decimal.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out var tp)) TakeProfitPct = tp;
                        break;
                    case "stoplosspct":
                        if (decimal.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out var sl)) StopLossPct = sl;
                        break;
                }
            }
        }

        public void SaveToConfFile(string path)
        {
            string symbolsStr = SubscribedSymbols != null && SubscribedSymbols.Count > 0
                ? string.Join(", ", SubscribedSymbols)
                : Symbol;

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# =================================================================");
            sb.AppendLine("#   币安交易助手 Pro 配置文件 (settings.conf)");
            sb.AppendLine("# =================================================================");
            sb.AppendLine();
            sb.AppendLine("# 1. 订阅与交易币种配置 (多个币种用英文逗号分隔)");
            sb.AppendLine($"Symbols = {symbolsStr}");
            sb.AppendLine();
            sb.AppendLine("# 2. K线周期 (可选: 1m, 3m, 5m, 15m, 30m, 1h, 2h, 4h, 6h, 8h, 12h, 1d, 3d, 1w, 1M)");
            sb.AppendLine($"KlineInterval = {FormatKlineInterval(KlineInterval)}");
            sb.AppendLine();
            sb.AppendLine("# 3. 数据回播开始与结束日期 (格式: yyyy-MM-dd)");
            sb.AppendLine($"StartDate = {StartDate:yyyy-MM-dd}");
            sb.AppendLine($"EndDate = {EndDate:yyyy-MM-dd}");
            sb.AppendLine();
            sb.AppendLine("# 4. 回播间隔毫秒数 (建议: 500)");
            sb.AppendLine($"PlaybackIntervalMs = {PlaybackIntervalMs}");
            sb.AppendLine();
            sb.AppendLine("# 5. 开关控制 (true / false)");
            sb.AppendLine($"EnableTickPush = {EnableTickPush.ToString().ToLower()}");
            sb.AppendLine($"AutoFitPrice = {AutoFitPrice.ToString().ToLower()}");
            sb.AppendLine($"HighlightHighVolume = {HighlightHighVolume.ToString().ToLower()}");
            sb.AppendLine();
            sb.AppendLine("# 6. 趋势线回调策略参数设置");
            sb.AppendLine($"EnableStrategy = {EnableStrategy.ToString().ToLower()}");
            sb.AppendLine($"EnableWarmup = {EnableWarmup.ToString().ToLower()}");
            sb.AppendLine($"MinLineX1X2 = {MinLineX1X2}");
            sb.AppendLine($"MinLineAge = {MinLineAge}");
            sb.AppendLine($"TakeProfitPct = {TakeProfitPct.ToString(CultureInfo.InvariantCulture)}");
            sb.AppendLine($"StopLossPct = {StopLossPct.ToString(CultureInfo.InvariantCulture)}");

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }

        public static KlineInterval ParseKlineInterval(string str)
        {
            str = str.Trim();
            switch (str.ToLowerInvariant())
            {
                case "1m": return KlineInterval.OneMinute;
                case "3m": return KlineInterval.ThreeMinutes;
                case "5m": return KlineInterval.FiveMinutes;
                case "15m": return KlineInterval.FifteenMinutes;
                case "30m": return KlineInterval.ThirtyMinutes;
                case "1h": return KlineInterval.OneHour;
                case "2h": return KlineInterval.TwoHour;
                case "4h": return KlineInterval.FourHour;
                case "6h": return KlineInterval.SixHour;
                case "8h": return KlineInterval.EightHour;
                case "12h": return KlineInterval.TwelveHour;
                case "1d": return KlineInterval.OneDay;
                case "3d": return KlineInterval.ThreeDay;
                case "1w": return KlineInterval.OneWeek;
                case "1m_month":
                case "1m_m":
                case "1M": return KlineInterval.OneMonth;
            }

            if (Enum.TryParse<KlineInterval>(str, true, out var result))
            {
                return result;
            }

            return KlineInterval.OneMinute;
        }

        public static string FormatKlineInterval(KlineInterval interval)
        {
            switch (interval)
            {
                case KlineInterval.OneMinute: return "1m";
                case KlineInterval.ThreeMinutes: return "3m";
                case KlineInterval.FiveMinutes: return "5m";
                case KlineInterval.FifteenMinutes: return "15m";
                case KlineInterval.ThirtyMinutes: return "30m";
                case KlineInterval.OneHour: return "1h";
                case KlineInterval.TwoHour: return "2h";
                case KlineInterval.FourHour: return "4h";
                case KlineInterval.SixHour: return "6h";
                case KlineInterval.EightHour: return "8h";
                case KlineInterval.TwelveHour: return "12h";
                case KlineInterval.OneDay: return "1d";
                case KlineInterval.ThreeDay: return "3d";
                case KlineInterval.OneWeek: return "1w";
                case KlineInterval.OneMonth: return "1M";
                default: return "1m";
            }
        }
    }
}

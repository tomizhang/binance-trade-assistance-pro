using Binance.Net.Enums;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinFormsApp2
{
    /// <summary>
    /// 单个币种的差异化参数配置项 (支持不同币种独立配置不同周期、杠杆与下单金额)
    /// </summary>
    public class SymbolConfigItem
    {
        public string Symbol { get; set; } = "BTCUSDT";
        public string KlineInterval { get; set; } = "1m";
        public int Leverage { get; set; } = 20;
        public decimal OrderQuantityUsdt { get; set; } = 1m;
        public bool Enabled { get; set; } = true;

        [JsonIgnore]
        public Binance.Net.Enums.KlineInterval ParsedInterval => UserSettings.ParseKlineInterval(KlineInterval);
    }

    /// <summary>
    /// 全局 JSON 参数配置模型 (读写 settings.json 配置文件)
    /// </summary>
    public class UserSettings
    {
        public bool IsLiveTrading { get; set; } = false;
        public string ApiKey { get; set; } = "";
        public string ApiSecret { get; set; } = "";
        public decimal DefaultOrderQuantityUsdt { get; set; } = 1m;

        public DateTime StartDate { get; set; } = DateTime.Today.AddDays(-1);
        public DateTime EndDate { get; set; } = DateTime.Today;
        public int PlaybackIntervalMs { get; set; } = 500;
        public bool EnableTickPush { get; set; } = true;
        public bool AutoFitPrice { get; set; } = true;
        public bool HighlightHighVolume { get; set; } = true;

        public bool EnableStrategy { get; set; } = true;
        public bool EnableWarmup { get; set; } = true;
        public int MinLineX1X2 { get; set; } = 40;
        public int MinLineAge { get; set; } = 4;
        public decimal TakeProfitPct { get; set; } = 1.5m;
        public decimal StopLossPct { get; set; } = 0.8m;

        /// <summary>
        /// 多币种独立周期、独立杠杆与独立资金配置列表
        /// </summary>
        public List<SymbolConfigItem> SymbolConfigs { get; set; } = new List<SymbolConfigItem>();

        /// <summary>
        /// 兼容旧 UI 控件的 Symbol 属性 (同时具备 Get 与 Set 访问器)
        /// </summary>
        [JsonIgnore]
        public string Symbol
        {
            get
            {
                if (SymbolConfigs == null || SymbolConfigs.Count == 0) return "BTCUSDT";
                var active = SymbolConfigs.Where(c => c.Enabled).Select(c => c.Symbol).ToList();
                return active.Count > 0 ? string.Join(", ", active) : "BTCUSDT";
            }
            set
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                var symbols = value.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (symbols.Length > 0)
                {
                    if (SymbolConfigs == null) SymbolConfigs = new List<SymbolConfigItem>();
                    var firstConfig = SymbolConfigs.FirstOrDefault(c => c.Enabled) ?? SymbolConfigs.FirstOrDefault();
                    if (firstConfig != null)
                    {
                        firstConfig.Symbol = symbols[0].Trim().ToUpper();
                    }
                    else
                    {
                        SymbolConfigs.Add(new SymbolConfigItem { Symbol = symbols[0].Trim().ToUpper() });
                    }
                }
            }
        }

        /// <summary>
        /// 兼容旧 UI 控件的 KlineInterval 属性 (同时具备 Get 与 Set 访问器)
        /// </summary>
        [JsonIgnore]
        public Binance.Net.Enums.KlineInterval KlineInterval
        {
            get
            {
                if (SymbolConfigs == null || SymbolConfigs.Count == 0) return Binance.Net.Enums.KlineInterval.OneMinute;
                return SymbolConfigs.FirstOrDefault(c => c.Enabled)?.ParsedInterval ?? Binance.Net.Enums.KlineInterval.OneMinute;
            }
            set
            {
                if (SymbolConfigs == null) SymbolConfigs = new List<SymbolConfigItem>();
                var firstConfig = SymbolConfigs.FirstOrDefault(c => c.Enabled) ?? SymbolConfigs.FirstOrDefault();
                string intervalStr = FormatKlineInterval(value);
                if (firstConfig != null)
                {
                    firstConfig.KlineInterval = intervalStr;
                }
                else
                {
                    SymbolConfigs.Add(new SymbolConfigItem { KlineInterval = intervalStr });
                }
            }
        }

        [JsonIgnore]
        public int Leverage => SymbolConfigs?.FirstOrDefault(c => c.Enabled)?.Leverage ?? 20;

        [JsonIgnore]
        public decimal OrderQuantityUsdt => SymbolConfigs?.FirstOrDefault(c => c.Enabled)?.OrderQuantityUsdt ?? DefaultOrderQuantityUsdt;

        public static string SettingsJsonPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");

        /// <summary>
        /// 从 settings.json 加载配置，若不存在则自动生成包含默认币种 (BTC, ETH, SOL, BNB) 差异化配置的 JSON 文件
        /// </summary>
        public static UserSettings Load()
        {
            try
            {
                if (File.Exists(SettingsJsonPath))
                {
                    string json = File.ReadAllText(SettingsJsonPath);
                    var settings = JsonSerializer.Deserialize<UserSettings>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (settings != null)
                    {
                        if (settings.SymbolConfigs == null || settings.SymbolConfigs.Count == 0)
                        {
                            settings.SymbolConfigs = GetDefaultSymbolConfigs();
                        }
                        return settings;
                    }
                }
            }
            catch
            {
                // 忽略反序列化异常
            }

            var defaultSettings = new UserSettings
            {
                SymbolConfigs = GetDefaultSymbolConfigs()
            };
            defaultSettings.Save();
            return defaultSettings;
        }

        /// <summary>
        /// 将当前 JSON 配置写入 settings.json
        /// </summary>
        public void Save()
        {
            try
            {
                if (SymbolConfigs == null || SymbolConfigs.Count == 0)
                {
                    SymbolConfigs = GetDefaultSymbolConfigs();
                }

                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(SettingsJsonPath, json);
            }
            catch
            {
                // 忽略写入异常
            }
        }

        public static List<SymbolConfigItem> GetDefaultSymbolConfigs()
        {
            return new List<SymbolConfigItem>
            {
                new SymbolConfigItem { Symbol = "BTCUSDT", KlineInterval = "1m", Leverage = 20, OrderQuantityUsdt = 1m, Enabled = true },
                new SymbolConfigItem { Symbol = "ETHUSDT", KlineInterval = "5m", Leverage = 15, OrderQuantityUsdt = 1m, Enabled = true },
                new SymbolConfigItem { Symbol = "SOLUSDT", KlineInterval = "15m", Leverage = 10, OrderQuantityUsdt = 1m, Enabled = true },
                new SymbolConfigItem { Symbol = "BNBUSDT", KlineInterval = "1h", Leverage = 10, OrderQuantityUsdt = 1m, Enabled = true }
            };
        }

        public static Binance.Net.Enums.KlineInterval ParseKlineInterval(string val)
        {
            return (val ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "1m" => Binance.Net.Enums.KlineInterval.OneMinute,
                "3m" => Binance.Net.Enums.KlineInterval.ThreeMinutes,
                "5m" => Binance.Net.Enums.KlineInterval.FiveMinutes,
                "15m" => Binance.Net.Enums.KlineInterval.FifteenMinutes,
                "30m" => Binance.Net.Enums.KlineInterval.ThirtyMinutes,
                "1h" => Binance.Net.Enums.KlineInterval.OneHour,
                "2h" => Binance.Net.Enums.KlineInterval.TwoHour,
                "4h" => Binance.Net.Enums.KlineInterval.FourHour,
                "6h" => Binance.Net.Enums.KlineInterval.SixHour,
                "8h" => Binance.Net.Enums.KlineInterval.EightHour,
                "12h" => Binance.Net.Enums.KlineInterval.TwelveHour,
                "1d" => Binance.Net.Enums.KlineInterval.OneDay,
                "3d" => Binance.Net.Enums.KlineInterval.ThreeDay,
                "1w" => Binance.Net.Enums.KlineInterval.OneWeek,
                "1m_month" or "1M" => Binance.Net.Enums.KlineInterval.OneMonth,
                _ => Binance.Net.Enums.KlineInterval.OneMinute
            };
        }

        public static string FormatKlineInterval(Binance.Net.Enums.KlineInterval interval)
        {
            return interval switch
            {
                Binance.Net.Enums.KlineInterval.OneMinute => "1m",
                Binance.Net.Enums.KlineInterval.ThreeMinutes => "3m",
                Binance.Net.Enums.KlineInterval.FiveMinutes => "5m",
                Binance.Net.Enums.KlineInterval.FifteenMinutes => "15m",
                Binance.Net.Enums.KlineInterval.ThirtyMinutes => "30m",
                Binance.Net.Enums.KlineInterval.OneHour => "1h",
                Binance.Net.Enums.KlineInterval.TwoHour => "2h",
                Binance.Net.Enums.KlineInterval.FourHour => "4h",
                Binance.Net.Enums.KlineInterval.SixHour => "6h",
                Binance.Net.Enums.KlineInterval.EightHour => "8h",
                Binance.Net.Enums.KlineInterval.TwelveHour => "12h",
                Binance.Net.Enums.KlineInterval.OneDay => "1d",
                Binance.Net.Enums.KlineInterval.ThreeDay => "3d",
                Binance.Net.Enums.KlineInterval.OneWeek => "1w",
                Binance.Net.Enums.KlineInterval.OneMonth => "1M",
                _ => "1m"
            };
        }
    }
}

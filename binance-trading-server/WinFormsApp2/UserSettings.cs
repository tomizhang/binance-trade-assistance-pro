using System;
using System.IO;
using System.Text.Json;
using Binance.Net.Enums;

namespace WinFormsApp2
{
    /// <summary>
    /// 用户界面右侧参数配置持久化存储模型
    /// </summary>
    public class UserSettings
    {
        public string Symbol { get; set; } = "BTCUSDT";
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

        private static string SettingsDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TradingAssistancePro");

        private static string SettingsFilePath => Path.Combine(SettingsDirectory, "user_settings.json");

        /// <summary>
        /// 从本地配置文件读取参数设置，若不存在或失败则返回默认配置
        /// </summary>
        public static UserSettings Load()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    string json = File.ReadAllText(SettingsFilePath);
                    var settings = JsonSerializer.Deserialize<UserSettings>(json);
                    if (settings != null)
                    {
                        return settings;
                    }
                }
            }
            catch
            {
                // 读取失败则回退至默认配置
            }

            return new UserSettings();
        }

        /// <summary>
        /// 保存当前右侧参数设置到本地 JSON 配置文件
        /// </summary>
        public void Save()
        {
            try
            {
                if (!Directory.Exists(SettingsDirectory))
                {
                    Directory.CreateDirectory(SettingsDirectory);
                }

                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsFilePath, json);
            }
            catch
            {
                // 忽略保存时的偶然异常
            }
        }
    }
}

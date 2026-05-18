using System;
using System.Collections.Generic;
using TradingTerminal.Models;
using TradingTerminal.Services; // 假设 KlineMessage 在这里

namespace TradingTerminal.Utils
{
    /// <summary>
    /// 累积平滑 Heikin-Ashi 算法工具类
    /// </summary>
    public static class CumulativeHeikinAshiHelper
    {
        // 定义输出的 HA 数据模型
        public class HeikinAshiResult
        {
            public long OpenTime { get; set; }
            public decimal Open { get; set; }
            public decimal High { get; set; }
            public decimal Low { get; set; }
            public decimal Close { get; set; }
            public decimal Volume { get; set; }
            public bool IsBullish { get; set; } // 替代前端的 Color
        }

        /// <summary>
        /// 根据原始 K 线计算 CMA Heikin-Ashi 数据
        /// </summary>
        /// <param name="rawData">原始 K 线列表 (按时间正序排列)</param>
        /// <returns>计算好的 HA K线列表</returns>
        public static List<HeikinAshiResult> Calculate(List<IKline> rawData)
        {
            var haData = new List<HeikinAshiResult>(rawData.Count);
            HeikinAshiResult prevHA = null;

            // 维护四价的累加总和，极大地提升遍历性能
            decimal sumOpen = 0m;
            decimal sumHigh = 0m;
            decimal sumLow = 0m;
            decimal sumClose = 0m;

            for (int i = 0; i < rawData.Count; i++)
            {
                var raw = rawData[i];

                // 1. 将当前值并入累加总和中
                sumOpen += raw.Open;
                sumHigh += raw.High;
                sumLow += raw.Low;
                sumClose += raw.Close;

                // 2. 计算当前的累积平均值 (索引 i 从 0 开始，所以除数是 i + 1)
                decimal count = i + 1m;
                decimal cmaOpen = sumOpen / count;
                decimal cmaHigh = sumHigh / count;
                decimal cmaLow = sumLow / count;
                decimal cmaClose = sumClose / count;

                // 初始化当前 HA 实体
                var ha = new HeikinAshiResult
                {
                    OpenTime = raw.OpenTime,
                    Volume = raw.Volume // 延用原始成交量
                };

                // 3. 将累积平均值 (CMA) 代入 Heikin-Ashi 公式

                // HA_Close = 平均值的平均
                ha.Close = (cmaOpen + cmaHigh + cmaLow + cmaClose) / 4m;

                // HA_Open = 依赖上一根 HA
                if (prevHA == null)
                {
                    ha.Open = (cmaOpen + cmaClose) / 2m;
                }
                else
                {
                    ha.Open = (prevHA.Open + prevHA.Close) / 2m;
                }

                // HA_High / Low = 比较得出极值
                // C# 的 Math.Max/Min 只支持两个参数，所以需要嵌套调用
                ha.High = Math.Max(cmaHigh, Math.Max(ha.Open, ha.Close));
                ha.Low = Math.Min(cmaLow, Math.Min(ha.Open, ha.Close));

                // 4. 方向判定 (替代 JS 的颜色判定)
                ha.IsBullish = ha.Close >= ha.Open;

                // 5. 存入结果并滚动指针
                haData.Add(ha);
                prevHA = ha;
            }

            return haData;
        }
    }
}
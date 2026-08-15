using System;

namespace WinFormsApp2
{
    /// <summary>
    /// 趋势线结构体与核心特征指标
    /// </summary>
    public struct TrendLine
    {
        /// <summary>
        /// 起始点 K 线数组索引 (x1)
        /// </summary>
        public int X1 { get; set; }

        /// <summary>
        /// 起始点价格 (y1)
        /// </summary>
        public decimal Y1 { get; set; }

        /// <summary>
        /// 起始点开盘时间
        /// </summary>
        public DateTime Time1 { get; set; }

        /// <summary>
        /// 终止点 K 线数组索引 (x2)
        /// </summary>
        public int X2 { get; set; }

        /// <summary>
        /// 终止点价格 (y2)
        /// </summary>
        public decimal Y2 { get; set; }

        /// <summary>
        /// 终止点开盘时间
        /// </summary>
        public DateTime Time2 { get; set; }

        /// <summary>
        /// 原始绝对价格斜率: (y2 - y1) / (x2 - x1)
        /// </summary>
        public decimal RawK { get; set; }

        /// <summary>
        /// 归一化百分比斜率 (%/bar): ((y2 - y1) / y1) / (x2 - x1) * 100
        /// 解决不同币种价格量级过大 (如 BTC 90,000) 或过小 (如 0.00001) 导致的斜率失真与不可比问题
        /// </summary>
        public decimal K { get; set; }

        /// <summary>
        /// x1 到 x2 的 K 线跨度差值 (line_x1_x2 = x2 - x1)
        /// </summary>
        public int LineX1X2 { get; set; }

        /// <summary>
        /// x2 到最新点 (最后一根 K 线) 的差值 (line_age = latestIndex - x2)
        /// </summary>
        public int LineAge { get; set; }

        /// <summary>
        /// 趋势线向后延伸穿过/碰撞第一根 K 线的范围 (line_extension_range = x_collide - x2)。
        /// 若从 x2 向后延伸未穿透任何 K 线，则值为从 x2 延伸至最新 K 线的距离。
        /// </summary>
        public int LineExtensionRange { get; set; }

        /// <summary>
        /// 首次穿越/碰撞的 K 线索引 (若未碰撞则为 -1)
        /// </summary>
        public int CollidedKlineIndex { get; set; }

        /// <summary>
        /// 趋势线类型 (高点阻力线 / 低点支撑线)
        /// </summary>
        public PivotType Type { get; set; }

        /// <summary>
        /// 根据趋势线方程推算指定 K 线索引 x 处的延长线价格: y(x) = y1 + RawK * (x - x1)
        /// </summary>
        public decimal GetPriceAt(int x)
        {
            return Y1 + RawK * (x - X1);
        }

        /// <summary>
        /// 基于 K 线开盘时间戳推算延伸线价格 (兼容在线实盘 LiveStream 模式与窗口滑动)
        /// </summary>
        public decimal GetPriceAtTime(DateTime targetTime)
        {
            if (Time2 <= Time1 || Time1 == DateTime.MinValue || targetTime == DateTime.MinValue)
            {
                return Y2;
            }

            double totalSeconds = (Time2 - Time1).TotalSeconds;
            if (totalSeconds <= 0) return Y2;

            double elapsedSeconds = (targetTime - Time1).TotalSeconds;
            decimal priceDelta = Y2 - Y1;

            return Y1 + priceDelta * (decimal)(elapsedSeconds / totalSeconds);
        }

        public override string ToString()
        {
            return $"[{Type}趋势线] ({X1}, {Y1:F4}) -> ({X2}, {Y2:F4}) | 归一化斜率K: {K:F4}%/bar, line_x1_x2: {LineX1X2}, line_age: {LineAge}, line_extension_range: {LineExtensionRange}";
        }
    }
}

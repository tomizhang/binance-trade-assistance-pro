using System;

namespace WinFormsApp1
{
    public class AngleTrendLineInfo
    {
        public int Pivot1Index { get; set; }
        public int Pivot2Index { get; set; }
        public long Pivot1TimeMs { get; set; }
        public long Pivot2TimeMs { get; set; }
        public double X1 { get; set; }
        public double Y1 { get; set; }
        public double X2 { get; set; }
        public double Y2 { get; set; }
        public double K { get; set; }
        public double NormK { get; set; }
        public bool IsPeak { get; set; }
        public bool IsBroken { get; set; }
        public int TouchCount { get; set; } = 2; // 默认由 2 个极值点构成
        public bool Keep { get; set; }
        public bool IsLatest { get; set; }
        public bool IsExpectedProfitBoundary { get; set; }

        /// <summary>
        /// 权重 1：趋势线两锚点在 X 轴上的跨度差值 (X2 - X1)
        /// </summary>
        public double Weight1 => Math.Abs(X2 - X1);

        /// <summary>
        /// 趋势线时间年龄 (Line_Age)：当前趋势线 X2 和最新 K 线 X 的差值 (X_latest - X2)
        /// </summary>
        public double Line_Age { get; set; }

        /// <summary>
        /// 综合权重得分 (结合跨度 Weight1、触碰次数 TouchCount 与线条年龄 Line_Age)
        /// </summary>
        public double CompositeWeight => (Weight1 * TouchCount) / (1.0 + 0.05 * Line_Age);

        public double GetY(double x) => Y1 + K * (x - X1);
    }
}

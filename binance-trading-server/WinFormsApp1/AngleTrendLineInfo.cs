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
        /// line_x1_x2: x1 到 x2 的跨度差值 (|x2 - x1|)
        /// </summary>
        public double Line_X1_X2 => Math.Abs(X2 - X1);

        /// <summary>
        /// 兼容属性 Weight1 -> Line_X1_X2
        /// </summary>
        public double Weight1 => Line_X1_X2;

        /// <summary>
        /// line_age: x2 到最新 K 线点 (X_latest) 的差值 (X_latest - X2)
        /// </summary>
        public double Line_Age { get; set; }

        /// <summary>
        /// line_extension_range: 趋势线向后延伸穿过碰撞第一根 K 线的范围 (x_break - x2)
        /// </summary>
        public double Line_Extension_Range { get; set; }

        /// <summary>
        /// 综合权重得分 (结合跨度 Line_X1_X2、触碰次数 TouchCount 与线条年龄 Line_Age)
        /// </summary>
        public double CompositeWeight => (Line_X1_X2 * TouchCount) / (1.0 + 0.05 * Line_Age);

        public double GetY(double x) => Y1 + K * (x - X1);
    }
}

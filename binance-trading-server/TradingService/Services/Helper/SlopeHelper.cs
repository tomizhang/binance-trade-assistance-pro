using System;

namespace TradingTerminal.Utils
{
    /// <summary>
    /// 量化专用：斜率与角度计算工具类
    /// 解决极小价格导致角度坍塌，以及大额价格导致角度触顶的问题
    /// </summary>
    public static class SlopeHelper
    {
        /// <summary>
        /// 核心方法：计算归一化角度 (百分比变动法 - 推荐)
        /// 无论价格是 60000 的 BTC，还是 0.00001 的 SHIB，只要涨幅相同，算出的角度就完全一致。
        /// </summary>
        /// <param name="y1">起点价格 (如前一根K线价格)</param>
        /// <param name="y2">终点价格 (如当前K线价格)</param>
        /// <param name="xDistance">X轴距离 (默认为1，代表相邻两根K线)</param>
        /// <param name="percentAmplifier">百分比放大器 (默认 100，表示用 % 变动作为Y轴增量)</param>
        /// <returns>角度 (-90 到 90 度)</returns>
        public static double CalculateNormalizedAngle(decimal y1, decimal y2, int xDistance = 1, double percentAmplifier = 100.0)
        {
            if (y1 == 0 || xDistance == 0) return 0; // 防止除零异常

            // 1. 计算价格变动百分比
            // 比如 0.00001 -> 0.0000101，变动了 0.01%
            double percentageChange = (double)((y2 - y1) / y1);

            // 2. 将百分比放大为合理的 Y 轴数值 (默认乘 100，即 1% 的涨幅对应的 Y 轴增量为 1)
            double normalizedY = percentageChange * percentAmplifier;

            // 3. 计算斜率 (归一化后的Y / X)
            double slope = normalizedY / xDistance;

            // 4. 通过反正切函数求弧度，再转换为角度
            return Math.Atan(slope) * (180 / Math.PI);
        }

        /// <summary>
        /// 备用方法：固定精度缩放法
        /// 如果你确切知道某个币种的精度级别，可以手动传入放大倍数 (比如 100000)
        /// </summary>
        public static double CalculateScaledAngle(decimal y1, decimal y2, int xDistance = 1, decimal scaleFactor = 1m)
        {
            if (xDistance == 0) return 0;

            double scaledY1 = (double)(y1 * scaleFactor);
            double scaledY2 = (double)(y2 * scaleFactor);

            double slope = (scaledY2 - scaledY1) / xDistance;
            return Math.Atan(slope) * (180 / Math.PI);
        }

        /// <summary>
        /// 计算两条线的背离/夹角差值 (用于判断金叉/死叉时的发散力度)
        /// </summary>
        public static double CalculateAngleDifference(double angle1, double angle2)
        {
            return Math.Abs(angle1 - angle2);
        }
    }
}
using System;
using System.Globalization;

namespace Common.Helper
{
    /// <summary>
    /// 统一 UTC+0 时间转换与格式化工具类
    /// 规则：全局统一使用 UTC+0，标准展示格式为 yyyy-MM-dd HH:mm:ss
    /// </summary>
    public static class TimeHelper
    {
        public const string StandardFormat = "yyyy-MM-dd HH:mm:ss";
        public const string CompactDateFormat = "yyyyMMdd";
        public const string StandardDateFormat = "yyyy-MM-dd";

        private static readonly DateTime UnixEpochUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// 将 Unix 毫秒时间戳转换为 UTC+0 DateTime
        /// </summary>
        public static DateTime FromUnixTimeMilliseconds(long unixTimeMs)
        {
            return UnixEpochUtc.AddMilliseconds(unixTimeMs);
        }

        /// <summary>
        /// 将 Unix 秒时间戳转换为 UTC+0 DateTime
        /// </summary>
        public static DateTime FromUnixTimeSeconds(long unixTimeSec)
        {
            return UnixEpochUtc.AddSeconds(unixTimeSec);
        }

        /// <summary>
        /// 将 DateTime 转换为 Unix 毫秒时间戳 (确保按 UTC 转换)
        /// </summary>
        public static long ToUnixTimeMilliseconds(DateTime dateTime)
        {
            DateTime utc = dateTime.Kind == DateTimeKind.Utc ? dateTime : dateTime.ToUniversalTime();
            return (long)(utc - UnixEpochUtc).TotalMilliseconds;
        }

        /// <summary>
        /// 将 DateTime 转换为 Unix 秒时间戳
        /// </summary>
        public static long ToUnixTimeSeconds(DateTime dateTime)
        {
            DateTime utc = dateTime.Kind == DateTimeKind.Utc ? dateTime : dateTime.ToUniversalTime();
            return (long)(utc - UnixEpochUtc).TotalSeconds;
        }

        /// <summary>
        /// 确保时间为 UTC+0 规格
        /// </summary>
        public static DateTime EnsureUtc(DateTime dateTime)
        {
            if (dateTime.Kind == DateTimeKind.Utc)
            {
                return dateTime;
            }
            if (dateTime.Kind == DateTimeKind.Unspecified)
            {
                return DateTime.SpecifyKind(dateTime, DateTimeKind.Utc);
            }
            return dateTime.ToUniversalTime();
        }

        /// <summary>
        /// 格式化为标准 UTC+0 字符串: yyyy-MM-dd HH:mm:ss (供 UI 层/业务层按需调用，底层无多余开销)
        /// </summary>
        public static string ToUtc0String(this DateTime dateTime, string format = StandardFormat)
        {
            DateTime utc = EnsureUtc(dateTime);
            return utc.ToString(format, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 解析 UTC+0 时间字符串
        /// </summary>
        public static DateTime ParseUtc(string dateString, string format = StandardFormat)
        {
            if (DateTime.TryParseExact(dateString, format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime parsed))
            {
                return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            }
            return DateTime.SpecifyKind(DateTime.Parse(dateString, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal), DateTimeKind.Utc);
        }
    }
}

using Common.Models;
using System;

namespace Common.Interfaces
{
    /// <summary>
    /// 高性能原始数据游标接口
    /// 基于列索引直接读取原始数据类型，彻底避免 ToString、装箱与大对象堆分配造成的 GC 停顿
    /// </summary>
    public interface IRawDataCursor : IDisposable
    {
        /// <summary>
        /// 当前行索引 (0 开始，未开始为 -1)
        /// </summary>
        int CurrentIndex { get; }

        /// <summary>
        /// 总记录条数
        /// </summary>
        int TotalCount { get; }

        /// <summary>
        /// 当前记录集的列数
        /// </summary>
        int FieldCount { get; }

        /// <summary>
        /// 是否有下一行
        /// </summary>
        bool HasNext { get; }

        /// <summary>
        /// 是否有上一行
        /// </summary>
        bool HasPrevious { get; }

        /// <summary>
        /// 移动到下一行
        /// </summary>
        bool MoveNext();

        /// <summary>
        /// 移动到上一行
        /// </summary>
        bool MovePrevious();

        /// <summary>
        /// 重置游标至初始位置
        /// </summary>
        void Reset();

        /// <summary>
        /// 定位至指定行索引
        /// </summary>
        bool Seek(int index);

        #region 原始列数据零装箱提取方法

        long GetInt64(int ordinal);
        int GetInt32(int ordinal);
        decimal GetDecimal(int ordinal);
        double GetDouble(int ordinal);
        bool GetBoolean(int ordinal);
        DateTime GetDateTime(int ordinal);
        string GetString(int ordinal);
        bool IsDBNull(int ordinal);

        #endregion

        #region 实体快速装配方法 (直接读取原始数值，避免任何中间字符串构造)

        /// <summary>
        /// 直接从当前列数据提取 MarketKline 强类型结构
        /// </summary>
        MarketKline ReadCurrentKline(string symbol, string interval);

        /// <summary>
        /// 直接从当前列数据提取 MarketTick 强类型结构
        /// </summary>
        MarketTick ReadCurrentTick(string symbol);

        #endregion
    }
}

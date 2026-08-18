using System.Collections.Generic;

namespace Common.Interfaces
{
    /// <summary>
    /// 双向游标接口 (支持前后移动与 100 条滑动历史缓存)
    /// </summary>
    /// <typeparam name="T">行情数据类型 (如 MarketKline 或 MarketTick)</typeparam>
    public interface ICursor<T>
    {
        /// <summary>
        /// 当前游标指向的数据项
        /// </summary>
        T Current { get; }

        /// <summary>
        /// 当前索引位置 (0 开始，未开始为 -1)
        /// </summary>
        int CurrentIndex { get; }

        /// <summary>
        /// 数据集总条数
        /// </summary>
        int TotalCount { get; }

        /// <summary>
        /// 是否有下一条数据
        /// </summary>
        bool HasNext { get; }

        /// <summary>
        /// 是否可以后退
        /// </summary>
        bool HasPrevious { get; }

        /// <summary>
        /// 前进一步
        /// </summary>
        /// <returns>若前进成功返回 true，若已到达末尾返回 false</returns>
        bool MoveNext();

        /// <summary>
        /// 后退一步 (回退当前数据)
        /// </summary>
        /// <returns>若后退成功返回 true，若已在最前位置返回 false</returns>
        bool MovePrevious();

        /// <summary>
        /// 重置游标至初始状态 (位置为 -1)
        /// </summary>
        void Reset();

        /// <summary>
        /// 定位到指定索引位置
        /// </summary>
        bool Seek(int index);

        /// <summary>
        /// 获取当前位置及之前的滑动窗口缓存数据 (默认最多 100 条，按时间正序排列)
        /// 供策略直接获取最近历史切片以计算指标 (如 MA、EMA、波峰波谷等)
        /// </summary>
        IReadOnlyList<T> GetBuffer();

        /// <summary>
        /// 获取最近 N 条数据 (不超过当前缓存上限 100 条)
        /// </summary>
        IReadOnlyList<T> GetRecent(int count);

        /// <summary>
        /// 预览前向数据 (不移动游标指针)
        /// </summary>
        T? PeekNext(int offset = 1);

        /// <summary>
        /// 预览后向数据 (不移动游标指针)
        /// </summary>
        T? PeekPrevious(int offset = 1);
    }
}

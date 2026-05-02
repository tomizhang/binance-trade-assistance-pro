using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;

namespace TradingTerminal.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AnalysisController : ControllerBase
    {
        // 接收前端传入的 K 线数组
        public class PeakRequest
        {
            public List<long> Times { get; set; }
            public List<decimal> Highs { get; set; }
            public List<decimal> Lows { get; set; }
            public int LeftLen { get; set; } = 5;
            public int RightLen { get; set; } = 5;
        }

        // 返回的坐标点
        public class PeakPoint
        {
            public long Time { get; set; }
            public decimal Value { get; set; }
        }

        /// <summary>
        /// 获取 K 线高低点 (Fractals / Local Extrema)
        /// </summary>
        [HttpPost("peaks")]
        public IActionResult CalculatePeaks([FromBody] PeakRequest req)
        {
            if (req.Times == null || req.Highs == null || req.Lows == null ||
                req.Times.Count != req.Highs.Count || req.Times.Count != req.Lows.Count)
            {
                return BadRequest(new { error = "数据格式不正确或长度不一致" });
            }

            var peaks = new List<PeakPoint>();
            var valleys = new List<PeakPoint>();
            int n = req.Times.Count;

            // 完美还原 peak.worker.js 的核心计算逻辑
            for (int i = req.LeftLen; i < n - req.RightLen; i++)
            {
                bool isPeak = true;
                bool isValley = true;

                // 检查高点
                for (int j = i - req.LeftLen; j <= i + req.RightLen; j++)
                {
                    if (j == i) continue;
                    if (req.Highs[j] >= req.Highs[i])
                    {
                        isPeak = false;
                        break;
                    }
                }

                // 检查低点
                for (int j = i - req.LeftLen; j <= i + req.RightLen; j++)
                {
                    if (j == i) continue;
                    if (req.Lows[j] <= req.Lows[i])
                    {
                        isValley = false;
                        break;
                    }
                }

                if (isPeak) peaks.Add(new PeakPoint { Time = req.Times[i], Value = req.Highs[i] });
                if (isValley) valleys.Add(new PeakPoint { Time = req.Times[i], Value = req.Lows[i] });
            }

            return Ok(new { peaks = peaks, valleys = valleys });
        }
    }
}
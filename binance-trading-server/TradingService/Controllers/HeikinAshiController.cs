using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using System.Threading.Tasks;
using TradingTerminal.Services;

namespace TradingTerminal.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class HeikinAshiController : ControllerBase
    {
        private readonly HeikinAshiService _haService;

        public HeikinAshiController(HeikinAshiService haService)
        {
            _haService = haService;
        }

        public class WatchlistReq
        {
            public List<string> Symbols { get; set; }
        }

        [HttpPost("watchlist")]
        public async Task<IActionResult> UpdateWatchList([FromBody] WatchlistReq req)
        {
            if (req.Symbols == null || req.Symbols.Count == 0)
                return BadRequest("必须提供至少一个币种");

            // 这里会调用服务，服务内部会自动打断当前 WS，重拉缺失数据并建立新连接
            await _haService.UpdateWatchListAsync(req.Symbols);
            return Ok(new { msg = "监听名单已更新，系统正在自动同步及重新建立监听链路" });
        }
    }
}
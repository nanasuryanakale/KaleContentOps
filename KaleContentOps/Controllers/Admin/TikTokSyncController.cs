using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using KaleContentOps.Services.TikTok;

namespace KaleContentOps.Controllers.Admin;

[ApiController]
[Route("internal/tiktok")]
public class TikTokSyncController : ControllerBase
{
    private readonly ITikTokVideoService _videoService;

    public TikTokSyncController(ITikTokVideoService videoService)
    {
        _videoService = videoService;
    }

    // Development-only manual trigger for running sync for a specific shop
    [HttpPost("sync/{shopCipher}")]
    public async Task<IActionResult> Sync(string shopCipher, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(shopCipher)) return BadRequest();

        try
        {
            var count = await _videoService.FetchAndSaveVideoListAsync(shopCipher, cancellationToken: cancellationToken);
            return Ok(new { processed = count });
        }
        catch (Exception ex)
        {
            return Problem(detail: ex.Message);
        }
    }
}

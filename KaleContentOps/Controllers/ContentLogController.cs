using KaleContentOps.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KaleContentOps.Controllers;

public class ContentLogController : Controller
{
    private readonly AppDbContext _db;

    public ContentLogController(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IActionResult> Index(
        string? search,
        int page = 1)
    {
        const int pageSize = 20;

        var query = _db.ContentLogs
            .Include(x => x.ContentType)
            .Include(x => x.ProductionMethod)
            .Include(x => x.Pic)
            .Include(x => x.Metrics)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(x =>
                x.Title != null &&
                x.Title.Contains(search));
        }

        var data = await query
            .OrderByDescending(x => x.VideoPostTime)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return View(data);
    }
}
using KaleContentOps.Data;
using KaleContentOps.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KaleContentOps.Controllers;

public class MasterPicController : Controller
{
    private readonly AppDbContext _db;

    public MasterPicController(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IActionResult> Index()
    {
        var pics = await _db.MasterPics
            .OrderBy(x => x.Name)
            .ToListAsync();

        return View(pics);
    }

    [HttpPost]
    public async Task<IActionResult> Create(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return RedirectToAction(nameof(Index));

        var pic = new MasterPic
        {
            Name = name.Trim(),
            IsActive = true
        };

        _db.MasterPics.Add(pic);
        await _db.SaveChangesAsync();

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Toggle(int id)
    {
        var pic = await _db.MasterPics.FindAsync(id);

        if (pic == null)
            return NotFound();

        pic.IsActive = !pic.IsActive;
        pic.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        return RedirectToAction(nameof(Index));
    }
}
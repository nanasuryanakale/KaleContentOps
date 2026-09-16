using KaleContentOps.Data;
using KaleContentOps.Models;
using KaleContentOps.ViewModels;
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
    public async Task<IActionResult> CreateAjax([FromBody] CreateMasterPicDto dto)
    {
        if (dto == null || string.IsNullOrWhiteSpace(dto.Name))
            return BadRequest(new { error = "Name is required" });

        var name = dto.Name.Trim();

        var exists = await _db.MasterPics.AnyAsync(x => x.Name == name);
        if (exists)
            return Conflict(new { error = "PIC already exists" });

        var pic = new MasterPic
        {
            Name = name,
            IsActive = true
        };

        _db.MasterPics.Add(pic);
        await _db.SaveChangesAsync();

        return Ok(new
        {
            id = pic.Id,
            name = pic.Name
        });
    }

    [HttpPost]
    public async Task<IActionResult> DeactivateAjax([FromBody] DeactivateMasterPicDto dto)
    {
        if (dto == null)
            return BadRequest(new { error = "Invalid payload" });

        var pic = await _db.MasterPics.FindAsync(dto.Id);

        if (pic == null)
            return NotFound(new { error = "PIC not found" });

        if (!pic.IsActive)
        {
            return Ok(new
            {
                success = true,
                id = pic.Id
            });
        }

        // soft deactivate
        pic.IsActive = false;
        pic.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        return Ok(new
        {
            success = true,
            id = pic.Id
        });
    }

    [HttpPost]
    public async Task<IActionResult> Deactivate(int id)
    {
        var pic = await _db.MasterPics.FindAsync(id);

        if (pic == null)
            return NotFound(new { error = "PIC not found" });

        if (!pic.IsActive)
        {
            return Ok(new
            {
                success = true,
                id = pic.Id
            });
        }

        pic.IsActive = false;
        pic.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        return Ok(new
        {
            success = true,
            id = pic.Id
        });
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
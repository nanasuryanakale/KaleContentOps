using System.Globalization;
using KaleContentOps.Data;
using KaleContentOps.Models;
using KaleContentOps.Services;
using KaleContentOps.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KaleContentOps.Controllers;

/// <summary>
/// Master PIC lifecycle management (Phase 2B).
///
/// Locked business semantics:
/// - IsActive  = CURRENT status only (never an employment period).
/// - JoinDate  = first business date the PIC joined (required, business calendar date, no future).
/// - ResignDate = last business date the PIC is still a member (nullable; NULL = unknown/no end).
/// - JoinDate / ResignDate are DateOnly business dates sourced from IShopTimeZone (Asia/Jakarta).
/// - UpdatedAt / CreatedAt are record-maintenance timestamps and are NEVER used as ResignDate.
///
/// Reactivation safety: the schema holds exactly ONE employment period per PIC, so a closed
/// period (ResignDate populated) must never be silently reopened - only an explicit edit of
/// the lifecycle data can correct it.
///
/// Authorization: no separate permission exists for Master PIC (Phase 2B must not invent one),
/// so the controller relies on the application-wide fallback policy (authenticated users only,
/// anonymous -> /Account/Login), exactly as before this phase.
/// </summary>
public class MasterPicController : Controller
{
    /// <summary>Must stay in sync with <see cref="MasterPic.Name"/> [MaxLength(100)].</summary>
    private const int NameMaxLength = 100;

    /// <summary>Canonical lifecycle date input format (HTML date input -> yyyy-MM-dd).</summary>
    private const string DateInputFormat = "yyyy-MM-dd";

    private readonly AppDbContext _db;
    private readonly IShopTimeZone _shopTimeZone;

    // shopTimeZone stays optional so existing single-argument construction (unit tests)
    // keeps compiling; DI supplies the configured shop timezone (Asia/Jakarta) at runtime.
    // Phase 2A: NEW PIC inserts must carry a real business JoinDate (Asia/Jakarta),
    // never a raw UTC/empty date. Phase 2B: the same source stamps ResignDate on resignation.
    public MasterPicController(AppDbContext db, IShopTimeZone? shopTimeZone = null)
    {
        _db = db;
        _shopTimeZone = shopTimeZone ?? new ShopTimeZone(Options.Create(new ShopTimeZoneOptions()));
    }

    // ------------------------------------------------------------------
    // LIST (dedicated Master PIC management page - Phase 2B)
    // ------------------------------------------------------------------

    public async Task<IActionResult> Index()
    {
        return View(await BuildPageAsync());
    }

    private async Task<MasterPicPageViewModel> BuildPageAsync() => new MasterPicPageViewModel
    {
        Pics = await _db.MasterPics.OrderBy(x => x.Name).ToListAsync(),
        BusinessToday = _shopTimeZone.Today()
    };

    // ------------------------------------------------------------------
    // CREATE (management page form)
    // Success keeps the existing redirect-to-Index contract; rejected input now
    // returns explicit validation feedback instead of being dropped silently.
    // ------------------------------------------------------------------

    [HttpPost]
    public async Task<IActionResult> Create(string? name, string? joinDate = null)
    {
        var today = _shopTimeZone.Today();
        var trimmedName = name?.Trim() ?? string.Empty;
        var join = today; // default JoinDate = current Asia/Jakarta business date

        if (string.IsNullOrWhiteSpace(trimmedName))
        {
            ModelState.AddModelError(nameof(name), "Nama PIC wajib diisi.");
        }
        else if (trimmedName.Length > NameMaxLength)
        {
            ModelState.AddModelError(nameof(name), $"Nama PIC maksimal {NameMaxLength} karakter.");
        }

        if (!string.IsNullOrWhiteSpace(joinDate))
        {
            if (!TryParseDate(joinDate, out var parsedJoin))
            {
                ModelState.AddModelError(nameof(joinDate), "JoinDate tidak valid. Gunakan format YYYY-MM-DD.");
            }
            else if (parsedJoin > today)
            {
                ModelState.AddModelError(nameof(joinDate), "JoinDate tidak boleh melebihi tanggal bisnis hari ini.");
            }
            else
            {
                join = parsedJoin;
            }
        }

        // Same application-level duplicate convention as CreateAjax (no DB unique index exists
        // and Phase 2B must not add a migration).
        if (!string.IsNullOrWhiteSpace(trimmedName)
            && trimmedName.Length <= NameMaxLength
            && await _db.MasterPics.AnyAsync(x => x.Name == trimmedName))
        {
            ModelState.AddModelError(nameof(name), "Nama PIC sudah digunakan oleh PIC lain.");
        }

        if (!ModelState.IsValid)
            return View(nameof(Index), await BuildPageAsync());

        var pic = new MasterPic
        {
            Name = trimmedName,
            IsActive = true,
            // Phase 2A: required employment date for new PICs = current business
            // date (Asia/Jakarta). ResignDate stays NULL until a real resignation
            // is recorded (Phase 2B owns that lifecycle).
            JoinDate = join,
            ResignDate = null
        };

        _db.MasterPics.Add(pic);
        await _db.SaveChangesAsync();

        TempData["MasterPicSuccess"] = $"PIC \"{pic.Name}\" berhasil ditambahkan.";
        return RedirectToAction(nameof(Index));
    }

    // ------------------------------------------------------------------
    // CREATE (AJAX - Content Log quick-add). Contract unchanged: { name } only,
    // 400 / 409 on failure, defaults JoinDate to the business date, ResignDate NULL.
    // ------------------------------------------------------------------

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
            IsActive = true,
            // Phase 2A: same business-date contract as Create - this is the
            // endpoint the Content Log workflow uses to add a PIC inline.
            JoinDate = _shopTimeZone.Today(),
            ResignDate = null
        };

        _db.MasterPics.Add(pic);
        await _db.SaveChangesAsync();

        return Ok(new
        {
            id = pic.Id,
            name = pic.Name
        });
    }

    // ------------------------------------------------------------------
    // EDIT (Name / JoinDate / ResignDate only - Id, CreatedAt and UpdatedAt are not editable)
    // ------------------------------------------------------------------

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, string? name, string? joinDate, string? resignDate)
    {
        var pic = await _db.MasterPics.FindAsync(id);
        if (pic == null)
            return NotFound(new { error = "PIC not found" });

        var today = _shopTimeZone.Today();
        var trimmedName = name?.Trim() ?? string.Empty;
        DateOnly join = default;
        var joinValid = false;
        DateOnly? resign = null;

        // 1. Name: required, max length, unique among OTHER PIC ids (own name is retained).
        if (string.IsNullOrWhiteSpace(trimmedName))
        {
            ModelState.AddModelError(nameof(name), "Nama PIC wajib diisi.");
        }
        else if (trimmedName.Length > NameMaxLength)
        {
            ModelState.AddModelError(nameof(name), $"Nama PIC maksimal {NameMaxLength} karakter.");
        }
        else if (await _db.MasterPics.AnyAsync(x => x.Name == trimmedName && x.Id != id))
        {
            ModelState.AddModelError(nameof(name), "Nama PIC sudah digunakan oleh PIC lain.");
        }

        // 2. JoinDate: required, exact yyyy-MM-dd, never in the future (business date).
        if (string.IsNullOrWhiteSpace(joinDate))
        {
            ModelState.AddModelError(nameof(joinDate), "JoinDate wajib diisi.");
        }
        else if (!TryParseDate(joinDate, out join))
        {
            ModelState.AddModelError(nameof(joinDate), "JoinDate tidak valid. Gunakan format YYYY-MM-DD.");
        }
        else if (join > today)
        {
            ModelState.AddModelError(nameof(joinDate), "JoinDate tidak boleh melebihi tanggal bisnis hari ini.");
        }
        else
        {
            joinValid = true;
        }

        // 3. ResignDate: optional, exact yyyy-MM-dd when provided (blank = NULL, not "today").
        if (!string.IsNullOrWhiteSpace(resignDate))
        {
            if (TryParseDate(resignDate, out var parsedResign))
            {
                resign = parsedResign;
            }
            else
            {
                ModelState.AddModelError(nameof(resignDate), "ResignDate tidak valid. Gunakan format YYYY-MM-DD.");
            }
        }

        // 4. Ordering: ResignDate >= JoinDate (only comparable once JoinDate is valid).
        if (joinValid && resign.HasValue && resign.Value < join)
        {
            ModelState.AddModelError(nameof(resignDate), "ResignDate tidak boleh lebih awal dari JoinDate.");
        }

        if (!ModelState.IsValid)
            return View(nameof(Index), await BuildPageAsync());

        pic.Name = trimmedName;
        pic.JoinDate = join;
        pic.ResignDate = resign;
        pic.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        TempData["MasterPicSuccess"] = $"Data PIC \"{pic.Name}\" diperbarui.";
        return RedirectToAction(nameof(Index));
    }

    // ------------------------------------------------------------------
    // DEACTIVATE / RESIGN
    // Explicit deactivation of an ACTIVE PIC closes the employment period with the
    // Asia/Jakarta business date (never UpdatedAt/CreatedAt).
    // Already-inactive PICs stay untouched, so an administrative deactivation with an
    // unknown resignation date keeps ResignDate NULL - no fabricated history.
    // ------------------------------------------------------------------

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

        ApplyResignation(pic);
        await _db.SaveChangesAsync();

        return Ok(new
        {
            success = true,
            id = pic.Id,
            resignDate = pic.ResignDate?.ToString(DateInputFormat)
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

        ApplyResignation(pic);
        await _db.SaveChangesAsync();

        return Ok(new
        {
            success = true,
            id = pic.Id,
            resignDate = pic.ResignDate?.ToString(DateInputFormat)
        });
    }

    // ------------------------------------------------------------------
    // TOGGLE (safe reactivation / deactivation switch)
    // - Active PIC -> deactivation, closes the period with the business date.
    // - Inactive + ResignDate NULL -> reactivation is safe, ResignDate stays NULL.
    // - Inactive + ResignDate set -> BLOCKED with a clear business error; the closed
    //   employment period is never silently reopened and JoinDate is never overwritten.
    // ------------------------------------------------------------------

    [HttpPost]
    public async Task<IActionResult> Toggle(int id)
    {
        var pic = await _db.MasterPics.FindAsync(id);

        if (pic == null)
            return NotFound();

        if (pic.IsActive)
        {
            ApplyResignation(pic);
            await _db.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }

        if (pic.ResignDate.HasValue)
        {
            // Closed employment period: this schema cannot represent a second period,
            // so reopening must go through an explicit lifecycle correction (Edit).
            return BadRequest(new
            {
                error = $"Masa kerja PIC \"{pic.Name}\" sudah ditutup pada {pic.ResignDate.Value.ToString(DateInputFormat)}. " +
                        "Aplikasi saat ini belum mendukung lebih dari satu periode kerja untuk satu PIC, " +
                        "sehingga PIC tidak dapat diaktifkan kembali secara otomatis. " +
                        "Perbaiki data lifecycle (JoinDate/ResignDate) melalui Edit bila memang terjadi kesalahan.",
                id = pic.Id,
                joinDate = pic.JoinDate.ToString(DateInputFormat),
                resignDate = pic.ResignDate.Value.ToString(DateInputFormat)
            });
        }

        pic.IsActive = true;
        pic.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        return RedirectToAction(nameof(Index));
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Closes the employment period of an ACTIVE PIC: IsActive=false, ResignDate = current
    /// Asia/Jakarta business date (only when none is recorded yet - an existing resignation
    /// date is history and is never overwritten), UpdatedAt = maintenance timestamp.
    /// </summary>
    private void ApplyResignation(MasterPic pic)
    {
        pic.IsActive = false;
        pic.ResignDate ??= _shopTimeZone.Today();
        pic.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Strict lifecycle date parsing: exact yyyy-MM-dd, culture-independent.</summary>
    private static bool TryParseDate(string? value, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return DateOnly.TryParseExact(
            value.Trim(),
            DateInputFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);
    }
}

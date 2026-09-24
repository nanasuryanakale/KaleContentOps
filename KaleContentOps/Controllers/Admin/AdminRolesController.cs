using System.Linq;
using System.Threading.Tasks;
using KaleContentOps.Security;
using KaleContentOps.Services.Admin;
using KaleContentOps.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace KaleContentOps.Controllers.Admin;

/// <summary>
/// Master Role &amp; Permission admin screen. Permission claims live in the existing
/// AspNetRoleClaims table (unchanged source of truth); the UI only ever offers the
/// known permission constants - there is no free-text permission input.
/// </summary>
[Authorize(Policy = PermissionPolicyProvider.PolicyPrefix + AuthConstants.Permissions.UserView)]
[Route("Admin/Roles")]
public class AdminRolesController : Controller
{
    private readonly AdminUserAccessService _adminService;

    public AdminRolesController(AdminUserAccessService adminService)
    {
        _adminService = adminService;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var model = new MasterRolesPageViewModel
        {
            Roles = await _adminService.GetRolesAsync(),
            KnownPermissions = AuthConstants.AllKnownPermissions.ToList()
        };
        // Explicit path: admin views are grouped under ~/Views/Admin (same as TikTokAdmin).
        return View("~/Views/Admin/Roles/Index.cshtml", model);
    }

    [HttpPost("SetPermissions")]
    [Authorize(Policy = PermissionPolicyProvider.PolicyPrefix + AuthConstants.Permissions.RolePermissionManage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetPermissions([FromBody] AdminSetRolePermissionsRequest request)
    {
        var result = await _adminService.SetRolePermissionsAsync(request.RoleId, request.Permissions);
        return Json(new
        {
            success = result.Succeeded,
            errorCode = result.ErrorCode,
            errors = result.Errors
        });
    }
}

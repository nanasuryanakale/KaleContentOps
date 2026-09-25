using System;
using System.Linq;
using System.Threading.Tasks;
using KaleContentOps.Security;
using KaleContentOps.Services.Admin;
using KaleContentOps.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace KaleContentOps.Controllers.Admin;

/// <summary>
/// Master User admin screen. GET is view-only (User.View); every mutation requires
/// User.Manage server-side - hiding buttons in the UI is never the authorization layer.
/// </summary>
[Authorize(Policy = PermissionPolicyProvider.PolicyPrefix + AuthConstants.Permissions.UserView)]
[Route("Admin/Users")]
public class AdminUsersController : Controller
{
    private readonly AdminUserAccessService _adminService;

    public AdminUsersController(AdminUserAccessService adminService)
    {
        _adminService = adminService;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var roles = await _adminService.GetAssignableRolesAsync();
        var model = new MasterUserPageViewModel
        {
            Users = await _adminService.GetUsersAsync(),
            AssignableRoles = roles.Select(r => new AssignableRoleOption { Id = r.Id, Name = r.Name }).ToList()
        };
        // Explicit path: admin views are grouped under ~/Views/Admin (same as TikTokAdmin).
        return View("~/Views/Admin/Users/Index.cshtml", model);
    }

    [HttpPost("Create")]
    [Authorize(Policy = PermissionPolicyProvider.PolicyPrefix + AuthConstants.Permissions.UserManage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([FromBody] CreateAdminUserInput input)
    {
        var result = await _adminService.CreateUserAsync(input);
        return Json(ToResponse(result));
    }

    [HttpPost("Update")]
    [Authorize(Policy = PermissionPolicyProvider.PolicyPrefix + AuthConstants.Permissions.UserManage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update([FromBody] AdminUpdateUserRequest request)
    {
        var result = await _adminService.UpdateUserAsync(request.UserId, new UpdateAdminUserInput
        {
            DisplayName = request.DisplayName,
            Email = request.Email,
            RoleName = request.RoleName,
            IsActive = request.IsActive
        });
        return Json(ToResponse(result));
    }

    [HttpPost("SetActive")]
    [Authorize(Policy = PermissionPolicyProvider.PolicyPrefix + AuthConstants.Permissions.UserManage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetActive([FromBody] AdminSetActiveRequest request)
    {
        var result = await _adminService.SetUserActiveAsync(request.UserId, request.IsActive);
        return Json(ToResponse(result));
    }

    [HttpPost("SetPassword")]
    [Authorize(Policy = PermissionPolicyProvider.PolicyPrefix + AuthConstants.Permissions.UserManage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetPassword([FromBody] AdminSetPasswordRequest request)
    {
        var result = await _adminService.SetPasswordAsync(request.UserId, request.NewPassword ?? string.Empty);
        return Json(ToResponse(result));
    }

    private static object ToResponse(AdminOperationResult result) => new
    {
        success = result.Succeeded,
        errorCode = result.ErrorCode,
        errors = result.Errors
    };
}

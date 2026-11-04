using KaleContentOps.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;

namespace KaleContentOps.Controllers;

/// <summary>
/// Login / Logout for the Authentication &amp; Access Foundation.
/// Only these endpoints are anonymous ([AllowAnonymous]); everything else in the
/// app requires authentication via the global fallback policy.
/// </summary>
[AllowAnonymous]
public class AccountController : Controller
{
    private readonly Microsoft.AspNetCore.Identity.SignInManager<Models.ApplicationUser> _signInManager;
    private readonly Microsoft.AspNetCore.Identity.UserManager<Models.ApplicationUser> _userManager;
    private readonly ILogger<AccountController> _logger;

    public AccountController(
        Microsoft.AspNetCore.Identity.SignInManager<Models.ApplicationUser> signInManager,
        Microsoft.AspNetCore.Identity.UserManager<Models.ApplicationUser> userManager,
        ILogger<AccountController> logger)
    {
        _signInManager = signInManager;
        _userManager = userManager;
        _logger = logger;
    }

    // ------------------------------------------------------------------
    // GET /Account/Login  (also the cookie LoginPath)
    // ------------------------------------------------------------------
    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToLocal(returnUrl);
        }

        ViewData["ReturnUrl"] = returnUrl;
        return View(new LoginViewModel());
    }

    // ------------------------------------------------------------------
    // POST /Account/Login
    // ------------------------------------------------------------------
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel model, string? returnUrl = null)
    {
        ViewData["ReturnUrl"] = returnUrl;

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var user = await _userManager.FindByNameAsync(model.Username.Trim());
        if (user is null)
        {
            // Generic message: never reveal whether the account exists.
            // Safe diagnostic (no credentials logged): makes the lookup step observable.
            _logger.LogWarning("Login failed: no user matches the submitted username.");
            ModelState.AddModelError(string.Empty, "Username atau password salah.");
            return View(model);
        }

        if (!user.IsActive)
        {
            _logger.LogWarning("Login attempt for inactive user {UserName} rejected.", user.UserName);
            ModelState.AddModelError(string.Empty, "Akun ini tidak aktif. Hubungi administrator.");
            return View(model);
        }

        // lockoutOnFailure: true -> Identity lockout after 5 failed attempts.
        var result = await _signInManager.PasswordSignInAsync(
            user, model.Password, model.RememberMe, lockoutOnFailure: true);

        if (result.Succeeded)
        {
            // Admin Management MVP: record last successful login for the Master User screen.
            // Set via UserManager only - never written from user input.
            user.LastLoginAt = DateTime.UtcNow;
            await _userManager.UpdateAsync(user);

            _logger.LogInformation("User {UserName} logged in.", user.UserName);
            return RedirectToLocal(returnUrl);
        }

        if (result.IsLockedOut)
        {
            _logger.LogWarning("User {UserName} locked out.", user.UserName);
            ModelState.AddModelError(string.Empty, "Akun terkunci sementara karena terlalu banyak percobaan gagal. Coba lagi nanti.");
            return View(model);
        }

        // Generic message for wrong password / not-allowed.
        // Safe diagnostic (no credentials logged): proves PasswordSignInAsync was reached
        // and shows which Identity result flag caused the rejection.
        _logger.LogWarning(
            "Password sign-in failed for {UserName}: Succeeded={Succeeded}, IsLockedOut={IsLockedOut}, IsNotAllowed={IsNotAllowed}, RequiresTwoFactor={RequiresTwoFactor}.",
            user.UserName, result.Succeeded, result.IsLockedOut, result.IsNotAllowed, result.RequiresTwoFactor);
        ModelState.AddModelError(string.Empty, "Username atau password salah.");
        return View(model);
    }

    // ------------------------------------------------------------------
    // POST /Account/Logout  (form post from the topbar)
    // ------------------------------------------------------------------
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        var userName = User.Identity?.Name;
        await _signInManager.SignOutAsync();
        _logger.LogInformation("User {UserName} logged out.", userName);
        return RedirectToAction(nameof(Login));
    }

    // ------------------------------------------------------------------
    // GET /Account/AccessDenied  (cookie AccessDeniedPath)
    // ------------------------------------------------------------------
    [HttpGet]
    public IActionResult AccessDenied() => View();

    private IActionResult RedirectToLocal(string? returnUrl)
    {
        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return Redirect(returnUrl);
        }

        return RedirectToAction("Index", "Home");
    }
}

/// <summary>Login form model (username + password; no email flow for MVP).</summary>
public class LoginViewModel
{
    [Required(ErrorMessage = "Username wajib diisi.")]
    [Display(Name = "Username")]
    public string Username { get; set; } = string.Empty;

    [Required(ErrorMessage = "Password wajib diisi.")]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [Display(Name = "Ingat saya")]
    public bool RememberMe { get; set; }
}

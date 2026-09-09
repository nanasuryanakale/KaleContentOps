using System;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using KaleContentOps.Services.TikTok;

namespace KaleContentOps.Controllers
{
    [Route("tiktok")]
    public class TikTokController : Controller
    {
        private readonly TikTokOptions _options;
        private readonly ITikTokAuthService _authService;

        private const string SessionStateKey = "tiktok_oauth_state";

        public TikTokController(Microsoft.Extensions.Options.IOptions<TikTokOptions> options, ITikTokAuthService authService)
        {
            _options = options.Value;
            _authService = authService;
        }

        [HttpGet("connect")]
        public IActionResult Connect()
        {
            if (string.IsNullOrWhiteSpace(_options.ServiceId) || string.IsNullOrWhiteSpace(_options.RedirectUrl))
            {
                return Problem(detail: "TikTok ServiceId or RedirectUrl not configured.");
            }

            // generate strong random state and store in session
            var bytes = new byte[32];
            RandomNumberGenerator.Fill(bytes);
            var state = WebEncoders.Base64UrlEncode(bytes);
            HttpContext.Session.SetString(SessionStateKey, state);

            var qs = new System.Collections.Generic.Dictionary<string, string?>
            {
                ["service_id"] = _options.ServiceId,
                ["state"] = state,
                ["redirect_uri"] = _options.RedirectUrl
            };

            var authorizeUrl = QueryHelpers.AddQueryString("https://services.tiktokshop.com/open/authorize", qs!);

            return Redirect(authorizeUrl);
        }

        [HttpGet("callback")]
        public async System.Threading.Tasks.Task<IActionResult> Callback([FromQuery] string? code, [FromQuery] string? state)
        {
            // Retrieve and clear stored state from session to prevent reuse
            var expected = HttpContext.Session.GetString(SessionStateKey);
            HttpContext.Session.Remove(SessionStateKey);

            // If TikTok provided a state, validate it against the stored value.
            // If TikTok did NOT provide state (it's optional for some flows), do not reject the callback solely for that.
            if (!string.IsNullOrWhiteSpace(state))
            {
                if (string.IsNullOrWhiteSpace(expected) || !string.Equals(expected, state, StringComparison.Ordinal))
                {
                    return BadRequest("Invalid state parameter.");
                }
            }

            if (string.IsNullOrWhiteSpace(code))
            {
                return BadRequest("Missing authorization code.");
            }

            try
            {
                var resp = await _authService.ExchangeAuthCodeAsync(code);
                if (resp == null)
                {
                    return Problem(detail: "TikTok token exchange returned no data.");
                }

                // Success - do not reveal tokens in UI
                return View("CallbackSuccess");
            }
            catch (TikTokAuthException ex)
            {
                // safe error details
                var msg = $"TikTok authorization failed: {ex.Message} (code={ex.ErrorCode} request_id={ex.RequestId})";
                return Problem(detail: msg);
            }
            catch (Exception ex)
            {
                return Problem(detail: ex.Message);
            }
        }
    }
}

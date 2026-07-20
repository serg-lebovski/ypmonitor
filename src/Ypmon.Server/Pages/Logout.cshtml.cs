using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ypmon.Server.Pages;

public class LogoutModel : PageModel
{
    private readonly Ypmon.Server.Services.ServerLogService _logs;
    public LogoutModel(Ypmon.Server.Services.ServerLogService logs) => _logs = logs;

    public async Task<IActionResult> OnGetAsync()
    {
        if (User.Identity?.IsAuthenticated == true)
            _logs.Info(Ypmon.Server.Data.LogArea.Auth, "Выход из системы", User.Identity.Name,
                Ypmon.Server.Services.ServerLogService.ClientIp(HttpContext));
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToPage("/Login");
    }
}

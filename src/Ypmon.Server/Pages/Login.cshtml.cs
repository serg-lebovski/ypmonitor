using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Ypmon.Server.Data;
using Ypmon.Server.Services;

namespace Ypmon.Server.Pages;

public class LoginModel : PageModel
{
    private readonly AppDbContext _db;
    public LoginModel(AppDbContext db) => _db = db;

    [BindProperty] public string Username { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    public string? Error { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        // Первый запуск — пользователей ещё нет, ведём на мастер настройки.
        if (!await _db.Users.AnyAsync())
            return RedirectToPage("/Setup");
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await _db.Users.AnyAsync())
            return RedirectToPage("/Setup");

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == Username);
        if (user is null || !PasswordHasher.Verify(Password, user.PasswordHash, user.PasswordSalt))
        {
            Error = "Неверный логин или пароль";
            return Page();
        }

        user.LastLoginAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Role, user.Role.ToString())
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity));

        return RedirectToPage("/Index");
    }
}

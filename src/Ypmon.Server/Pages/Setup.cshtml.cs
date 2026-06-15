using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Ypmon.Server.Data;
using Ypmon.Server.Services;

namespace Ypmon.Server.Pages;

public class SetupModel : PageModel
{
    private readonly AppDbContext _db;
    public SetupModel(AppDbContext db) => _db = db;

    [BindProperty] public string Username { get; set; } = "";
    [BindProperty] public string DisplayName { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty] public string PasswordConfirm { get; set; } = "";
    public string? Error { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        // Если администратор уже создан — мастер не нужен.
        if (await _db.Users.AnyAsync())
            return RedirectToPage("/Login");
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (await _db.Users.AnyAsync())
            return RedirectToPage("/Login");

        if (string.IsNullOrWhiteSpace(Username) || Username.Length < 3)
            Error = "Логин должен быть не короче 3 символов";
        else if (Password.Length < 6)
            Error = "Пароль должен быть не короче 6 символов";
        else if (Password != PasswordConfirm)
            Error = "Пароли не совпадают";

        if (Error is not null) return Page();

        var (hash, salt) = PasswordHasher.Hash(Password);
        _db.Users.Add(new AppUser
        {
            Username = Username.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? Username.Trim() : DisplayName.Trim(),
            PasswordHash = hash,
            PasswordSalt = salt,
            Role = UserRole.Admin
        });
        await _db.SaveChangesAsync();
        return RedirectToPage("/Login");
    }
}

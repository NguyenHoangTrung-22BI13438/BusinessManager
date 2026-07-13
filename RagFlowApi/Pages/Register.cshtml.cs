using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RagFlowApi.Models;
using RagFlowApi.Services;

namespace RagFlowApi.Pages;

[Authorize(Roles = "admin")]
public class RegisterModel : PageModel
{
    private readonly UserStore _store;
    private readonly ILogger<RegisterModel> _log;

    // ── Create-user form fields ───────────────────────────────────────────
    [BindProperty] public string NewUsername    { get; set; } = string.Empty;
    [BindProperty] public string NewDisplayName { get; set; } = string.Empty;
    [BindProperty] public string NewPassword    { get; set; } = string.Empty;
    [BindProperty] public bool   NewIsAdmin     { get; set; }

    // profile
    [BindProperty] public string NewFullName    { get; set; } = string.Empty;
    [BindProperty] public string NewDepartment  { get; set; } = string.Empty;
    [BindProperty] public string NewJobTitle    { get; set; } = string.Empty;
    [BindProperty] public string NewEmail       { get; set; } = string.Empty;
    [BindProperty] public string NewPhone       { get; set; } = string.Empty;

    // ── Display data ──────────────────────────────────────────────────────
    public List<UserRecord> Users        { get; private set; } = [];
    public string?          FeedbackMsg  { get; private set; }
    public bool             FeedbackOk   { get; private set; }

    public RegisterModel(UserStore store, ILogger<RegisterModel> log)
    {
        _store = store;
        _log   = log;
    }

    public async Task OnGetAsync() =>
        Users = await _store.GetAllAsync();

    public async Task<IActionResult> OnPostCreateAsync()
    {
        Users = await _store.GetAllAsync();

        if (string.IsNullOrWhiteSpace(NewUsername) || NewUsername.Length < 3)
        {
            FeedbackMsg = "Username must be at least 3 characters.";
            return Page();
        }
        if (NewUsername.Any(c => !char.IsLetterOrDigit(c) && c != '_' && c != '-'))
        {
            FeedbackMsg = "Username may only contain letters, numbers, _ and -.";
            return Page();
        }
        if (string.IsNullOrWhiteSpace(NewPassword) || NewPassword.Length < 6)
        {
            FeedbackMsg = "Password must be at least 6 characters.";
            return Page();
        }
        if (await _store.ExistsAsync(NewUsername))
        {
            FeedbackMsg = $"Username '{NewUsername}' is already taken.";
            return Page();
        }

        var record = new UserRecord
        {
            Username    = NewUsername.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(NewDisplayName)
                            ? NewUsername.Trim() : NewDisplayName.Trim(),
            PasswordHash = _store.HashPassword(NewPassword),
            IsAdmin      = NewIsAdmin,
            CreatedAt    = DateTime.UtcNow,
            FullName     = NewFullName.Trim(),
            Department   = NewDepartment.Trim(),
            JobTitle     = NewJobTitle.Trim(),
            Email        = NewEmail.Trim(),
            PhoneNumber  = NewPhone.Trim(),
        };

        try
        {
            await _store.AddAsync(record);
            _log.LogInformation("Admin created account for '{User}'", record.Username);
        }
        catch (InvalidOperationException ex)
        {
            FeedbackMsg = ex.Message;
            return Page();
        }

        FeedbackMsg = $"Account '{record.Username}' created successfully.";
        FeedbackOk  = true;
        Users = await _store.GetAllAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(string username)
    {
        if (string.IsNullOrWhiteSpace(username) ||
            username.Equals(User.Identity!.Name, StringComparison.OrdinalIgnoreCase))
        {
            FeedbackMsg = "Cannot delete your own account.";
            Users = await _store.GetAllAsync();
            return Page();
        }

        await _store.DeleteAsync(username);
        _log.LogInformation("Admin deleted account '{User}'", username);

        FeedbackMsg = $"Account '{username}' deleted.";
        FeedbackOk  = true;
        Users = await _store.GetAllAsync();
        return Page();
    }
}

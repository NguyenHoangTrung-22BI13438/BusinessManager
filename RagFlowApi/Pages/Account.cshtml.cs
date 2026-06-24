using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RagFlowApi.Models;
using RagFlowApi.Services;

namespace RagFlowApi.Pages;

/// <summary>
/// Razor Page at /Account.
/// GET  — returns the user info + change-password modal fragment.
///         The JS in _Layout fetches this URL and injects the rendered
///         #account-modal-body element into the backdrop modal.
/// POST (handler=ChangePassword) — validates the old password,
///         hashes the new one, and writes it back to users.json.
///         Returns a partial page; the JS reads #modal-feedback for
///         the inline success / error message.
/// </summary>
[Authorize]
public class AccountModel : PageModel
{
    private readonly UserStore _store;

    // ── Bound form fields ─────────────────────────────────────────────────
    [BindProperty] public string CurrentPassword { get; set; } = string.Empty;
    [BindProperty] public string NewPassword     { get; set; } = string.Empty;
    [BindProperty] public string ConfirmPassword { get; set; } = string.Empty;

    // ── Display data ──────────────────────────────────────────────────────
    public UserRecord? Record       { get; private set; }
    public string?     FeedbackMsg  { get; private set; }
    public bool        FeedbackOk   { get; private set; }

    public AccountModel(UserStore store) => _store = store;

    // ── GET ───────────────────────────────────────────────────────────────
    public async Task OnGetAsync()
    {
        Record = await _store.GetByUsernameAsync(User.Identity!.Name!);
    }

    // ── POST: change password ─────────────────────────────────────────────
    public async Task<IActionResult> OnPostChangePasswordAsync()
    {
        Record = await _store.GetByUsernameAsync(User.Identity!.Name!);

        if (Record is null)
        {
            FeedbackMsg = "User record not found.";
            FeedbackOk  = false;
            return Page();
        }

        // Validate current password
        if (!_store.VerifyPassword(Record, CurrentPassword))
        {
            FeedbackMsg = "Current password is incorrect.";
            FeedbackOk  = false;
            return Page();
        }

        // Validate new password
        if (string.IsNullOrWhiteSpace(NewPassword) || NewPassword.Length < 6)
        {
            FeedbackMsg = "New password must be at least 6 characters.";
            FeedbackOk  = false;
            return Page();
        }

        if (NewPassword != ConfirmPassword)
        {
            FeedbackMsg = "New passwords do not match.";
            FeedbackOk  = false;
            return Page();
        }

        // Persist
        Record.PasswordHash = _store.HashPassword(NewPassword);
        await _store.UpdateAsync(Record);

        FeedbackMsg = "Password changed successfully.";
        FeedbackOk  = true;
        return Page();
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RagFlowApi.Models;
using RagFlowApi.Services;

namespace RagFlowApi.Pages;

/// <summary>
/// Razor Page at /Account.
/// GET  — returns the user info + change-password + profile-edit modal fragment.
///         The JS in _Layout fetches this URL and injects the rendered
///         #account-modal-body element into the backdrop modal.
/// POST (handler=ChangePassword) — validates the old password, hashes the new one.
/// POST (handler=UpdateProfile)  — saves personal-info fields to users.json.
/// </summary>
[Authorize]
public class AccountModel : PageModel
{
    private readonly UserStore _store;

    // ── Change-password fields ────────────────────────────────────────────
    [BindProperty] public string CurrentPassword { get; set; } = string.Empty;
    [BindProperty] public string NewPassword     { get; set; } = string.Empty;
    [BindProperty] public string ConfirmPassword { get; set; } = string.Empty;

    // ── Profile fields ────────────────────────────────────────────────────
    [BindProperty] public string ProfileFullName      { get; set; } = string.Empty;
    [BindProperty] public string ProfileDateOfBirth   { get; set; } = string.Empty;
    [BindProperty] public string ProfilePlaceOfBirth  { get; set; } = string.Empty;
    [BindProperty] public string ProfileNationality   { get; set; } = string.Empty;
    [BindProperty] public string ProfileIdNumber      { get; set; } = string.Empty;
    [BindProperty] public string ProfileIdIssuedDate  { get; set; } = string.Empty;
    [BindProperty] public string ProfileIdIssuedPlace { get; set; } = string.Empty;
    [BindProperty] public string ProfileJobTitle      { get; set; } = string.Empty;
    [BindProperty] public string ProfileDepartment    { get; set; } = string.Empty;
    [BindProperty] public string ProfilePhoneNumber   { get; set; } = string.Empty;
    [BindProperty] public string ProfileEmail         { get; set; } = string.Empty;
    [BindProperty] public string ProfileAddress       { get; set; } = string.Empty;

    // ── Display data ──────────────────────────────────────────────────────
    public UserRecord? Record         { get; private set; }
    public string?     FeedbackMsg    { get; private set; }
    public bool        FeedbackOk     { get; private set; }
    public string      ActiveSection  { get; private set; } = "password";

    public AccountModel(UserStore store) => _store = store;

    // ── GET ───────────────────────────────────────────────────────────────
    public async Task OnGetAsync()
    {
        Record = await _store.GetByUsernameAsync(User.Identity!.Name!);
    }

    // ── POST: change password ─────────────────────────────────────────────
    public async Task<IActionResult> OnPostChangePasswordAsync()
    {
        ActiveSection = "password";

        var username = User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(username))
        {
            FeedbackMsg = "Could not identify the current user.";
            FeedbackOk  = false;
            return Page();
        }

        Record = await _store.GetByUsernameAsync(username);

        if (Record is null)
        {
            FeedbackMsg = "User record not found.";
            FeedbackOk  = false;
            return Page();
        }

        if (!_store.VerifyPassword(Record, CurrentPassword))
        {
            FeedbackMsg = "Current password is incorrect.";
            FeedbackOk  = false;
            return Page();
        }

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

        Record.PasswordHash = _store.HashPassword(NewPassword);
        await _store.UpdateAsync(Record);

        FeedbackMsg = "Password changed successfully.";
        FeedbackOk  = true;
        return Page();
    }

    // ── POST: update personal-info profile ───────────────────────────────
    public async Task<IActionResult> OnPostUpdateProfileAsync()
    {
        ActiveSection = "profile";

        var username = User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(username))
        {
            FeedbackMsg = "Could not identify the current user.";
            FeedbackOk  = false;
            return Page();
        }

        Record = await _store.GetByUsernameAsync(username);

        if (Record is null)
        {
            FeedbackMsg = "User record not found.";
            FeedbackOk  = false;
            return Page();
        }

        static string Safe(string? s) => s?.Trim() ?? string.Empty;

        Record.FullName      = Safe(ProfileFullName);
        Record.DateOfBirth   = Safe(ProfileDateOfBirth);
        Record.PlaceOfBirth  = Safe(ProfilePlaceOfBirth);
        Record.Nationality   = Safe(ProfileNationality);
        Record.IdNumber      = Safe(ProfileIdNumber);
        Record.IdIssuedDate  = Safe(ProfileIdIssuedDate);
        Record.IdIssuedPlace = Safe(ProfileIdIssuedPlace);
        Record.JobTitle      = Safe(ProfileJobTitle);
        Record.Department    = Safe(ProfileDepartment);
        Record.PhoneNumber   = Safe(ProfilePhoneNumber);
        Record.Email         = Safe(ProfileEmail);
        Record.Address       = Safe(ProfileAddress);

        await _store.UpdateAsync(Record);

        FeedbackMsg = "Profile updated successfully.";
        FeedbackOk  = true;
        return Page();
    }
}

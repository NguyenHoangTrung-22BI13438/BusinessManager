using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RagFlowApi.Models;
using RagFlowApi.Services;

namespace RagFlowApi.Pages;

/// <summary>
/// Page model for the user registration page.
/// Persists the new user record (credentials, empty RAGFlow IDs) to users.json.
/// Dataset and assistant are created lazily: dataset on first upload,
/// assistant on first question — not at registration time.
/// On success, redirects to the Login page.
/// On failure, surfaces a descriptive error and leaves the form populated.
/// </summary>
public class RegisterModel : PageModel
{
    private readonly UserStore _store;
    private readonly ILogger<RegisterModel> _log;

    /// <summary>Username entered by the user.</summary>
    [BindProperty]
    public string Username { get; set; } = string.Empty;

    /// <summary>Optional friendly display name shown in the nav bar.</summary>
    [BindProperty]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Plain-text password entered by the user. Never persisted.</summary>
    [BindProperty]
    public string Password { get; set; } = string.Empty;

    /// <summary>Confirmation password — must match Password.</summary>
    [BindProperty]
    public string ConfirmPassword { get; set; } = string.Empty;

    /// <summary>
    /// Non-null when registration fails. Displayed as an error alert on the page.
    /// </summary>
    public string? ErrorMessage { get; private set; }

    public RegisterModel(UserStore store, ILogger<RegisterModel> log)
    {
        _store = store;
        _log = log;
    }

    /// <summary>
    /// Handles GET /Register.
    /// Renders the empty registration form.
    /// </summary>
    public void OnGet() { }

    /// <summary>
    /// Handles POST /Register.
    /// Validates input, hashes the password, persists the record, and redirects to Login.
    /// If any step fails the page is re-rendered with an error message.
    /// </summary>
    public async Task<IActionResult> OnPostAsync()
    {
        // ── 1. Basic input validation ─────────────────────────────────────────
        if (!ValidateInput(out var validationError))
        {
            ErrorMessage = validationError;
            return Page();
        }

        // ── 2. Check username availability ───────────────────────────────────
        if (await _store.ExistsAsync(Username))
        {
            ErrorMessage = $"Username '{Username}' is already taken. Please choose another.";
            return Page();
        }

        // ── 5. Build and persist the user record ──────────────────────────────
        var record = new UserRecord
        {
            Username = Username.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(DisplayName)
? Username.Trim()
: DisplayName.Trim(),
            PasswordHash = _store.HashPassword(Password),
            DatasetId = "",          // ← set empty — created on first upload
            AssistantId = "",          // ← set empty — created on first question
            CreatedAt = DateTime.UtcNow
        };

        try
        {
            await _store.AddAsync(record);
        }
        catch (InvalidOperationException ex)
        {
            // Race condition: another request registered the same username
            // between our ExistsAsync check and AddAsync.
            ErrorMessage = ex.Message;
            return Page();
        }

        _log.LogInformation("Registration complete for user '{User}'", Username);

        // ── 6. Redirect to Login ───────────────────────────────────────────────
        return RedirectToPage("/Login", new { registered = true });
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Runs all synchronous validation checks on the bound form properties.
    /// Populates <paramref name="error"/> with the first failure message found.
    /// </summary>
    /// <param name="error">Set to a user-facing error message on failure; null on success.</param>
    /// <returns>True if all checks pass; false if any check fails.</returns>
    private bool ValidateInput(out string? error)
    {
        if (string.IsNullOrWhiteSpace(Username))
        {
            error = "Username is required.";
            return false;
        }

        if (Username.Length < 3 || Username.Length > 32)
        {
            error = "Username must be between 3 and 32 characters.";
            return false;
        }

        if (Username.Any(c => !char.IsLetterOrDigit(c) && c != '_' && c != '-'))
        {
            error = "Username may only contain letters, numbers, underscores, and hyphens.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(Password))
        {
            error = "Password is required.";
            return false;
        }

        if (Password.Length < 6)
        {
            error = "Password must be at least 6 characters.";
            return false;
        }

        if (Password != ConfirmPassword)
        {
            error = "Passwords do not match.";
            return false;
        }

        error = null;
        return true;
    }
}

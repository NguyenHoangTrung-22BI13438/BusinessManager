using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RagFlowApi.Services;
using System.Security.Claims;

namespace RagFlowApi.Pages;

/// <summary>
/// Page model for the login page.
/// Verifies credentials against users.json and issues an auth cookie
/// on success, then redirects the user to the Ask page.
/// </summary>
public class LoginModel : PageModel
{
    private readonly UserStore _store;
    private readonly ILogger<LoginModel> _log;

    /// <summary>Username entered by the user.</summary>
    [BindProperty]
    public string Username { get; set; } = string.Empty;

    /// <summary>Plain-text password entered by the user. Never persisted.</summary>
    [BindProperty]
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Non-null when login fails. Displayed as an error alert on the page.
    /// </summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>
    /// Non-null when the user just registered successfully.
    /// Displayed as a success alert on the page.
    /// </summary>
    public string? SuccessMessage { get; private set; }

    public LoginModel(UserStore store, ILogger<LoginModel> log)
    {
        _store = store;
        _log = log;
    }

    /// <summary>
    /// Handles GET /Login.
    /// Renders the login form. If the user was just redirected from
    /// Register, shows a success message prompting them to sign in.
    /// </summary>
    /// <param name="registered">
    /// Optional flag passed as a query parameter from the Register page
    /// to indicate successful registration.
    /// </param>
    //public void OnGet(bool registered = false)
    public IActionResult OnGet(bool registered = false)
    {
        // Already authenticated — go straight to the app
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToPage("/Ask");

        if (registered)
            SuccessMessage = "Account created successfully. Please sign in.";

        return Page();
    }

    /// <summary>
    /// Handles POST /Login.
    /// Looks up the username, verifies the password hash, issues an auth
    /// cookie containing the username as a claim, and redirects to /Ask.
    /// On failure, re-renders the form with a generic error message.
    /// The error message is intentionally vague to avoid username enumeration.
    /// </summary>
    public async Task<IActionResult> OnPostAsync()
    {
        // ── 1. Basic presence check ───────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "Please enter your username and password.";
            return Page();
        }

        // ── 2. Look up user record ────────────────────────────────────────────
        var record = await _store.GetByUsernameAsync(Username);

        if (record is null)
        {
            _log.LogWarning("Failed login attempt: username '{User}' does not exist", Username);
            ErrorMessage = "Username does not exist.";
            return Page();
        }

        if (!_store.VerifyPassword(record, Password))
        {
            _log.LogWarning("Failed login attempt: wrong password for username '{User}'", Username);
            ErrorMessage = "Wrong password.";
            return Page();
        }

        // ── 4. Build claims identity ──────────────────────────────────────────
        // Name claim is used by UserContext to identify the logged-in user.
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name,    record.Username),
            new("DisplayName",      record.DisplayName),
            new(ClaimTypes.Role,    record.IsAdmin ? "admin" : "user")  // ← add this
        };

        var identity = new ClaimsIdentity(claims, "Cookies");
        var principal = new ClaimsPrincipal(identity);

        // ── 5. Issue the auth cookie ──────────────────────────────────────────
        await HttpContext.SignInAsync("Cookies", principal,
            new AuthenticationProperties
            {
                IsPersistent = true,    // survives browser close
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(7)
                //ExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(10)
            });

        _log.LogInformation("User '{User}' signed in.", record.Username);

        // ── 6. Redirect to Ask ────────────────────────────────────────────────
        return RedirectToPage("/Ask");
    }
}

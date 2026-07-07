using System.Text.Json;
using RagFlowApi.Models;
using Microsoft.AspNetCore.Identity;

namespace RagFlowApi.Services;

/// <summary>
/// Singleton service that manages persistent user account storage.
/// Reads and writes a JSON file (users.json) located in the application
/// content root alongside appsettings.json.
/// Thread-safe: all file operations are protected by a SemaphoreSlim.
/// </summary>
public class UserStore
{
    private readonly string                       _filePath;
    private readonly ILogger<UserStore>           _log;
    private readonly PasswordHasher<UserRecord>   _hasher = new();
    private readonly SemaphoreSlim                _lock   = new(1, 1);
    private static readonly JsonSerializerOptions _writeOptions =
    new()
    { WriteIndented = true };

    public UserStore(IWebHostEnvironment env, ILogger<UserStore> log)
    {
        _filePath = Path.Combine(env.ContentRootPath, "users.json");
        _log      = log;

        // Create an empty store file if it does not exist yet
        if (!File.Exists(_filePath))
            File.WriteAllText(_filePath, "[]");
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads all user records from the JSON file.
    /// Returns an empty list if the file is missing or malformed.
    /// </summary>
    public async Task<List<UserRecord>> GetAllAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var json = await File.ReadAllTextAsync(_filePath);
            return JsonSerializer.Deserialize<List<UserRecord>>(json)
                   ?? [];
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to read users.json");
            return [];
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Finds a single user by their username (case-insensitive).
    /// Returns null if no matching record exists.
    /// </summary>
    /// <param name="username">The username to search for.</param>
    public async Task<UserRecord?> GetByUsernameAsync(string username)
    {
        var all = await GetAllAsync();
        return all.FirstOrDefault(u =>
            u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
    }
    /// <summary>
    /// Returns the first admin user who already has a dataset created.
    /// Used by normal users to resolve the shared corpus dataset ID.
    /// </summary>
    public async Task<UserRecord?> GetAdminWithDatasetAsync()
    {
        var all = await GetAllAsync();
        return all.FirstOrDefault(u =>
            u.IsAdmin && !string.IsNullOrWhiteSpace(u.DatasetId));
    }

    /// <summary>
    /// Checks whether a username is already registered.
    /// </summary>
    /// <param name="username">The username to check.</param>
    /// <returns>True if the username is taken; false otherwise.</returns>
    public async Task<bool> ExistsAsync(string username)
        => await GetByUsernameAsync(username) is not null;

    // ── Write ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Appends a new user record to the JSON file.
    /// Throws InvalidOperationException if the username is already taken.
    /// </summary>
    /// <param name="record">The fully populated UserRecord to persist.</param>
    public async Task AddAsync(UserRecord record)
    {
        await _lock.WaitAsync();
        try
        {
            var json = await File.ReadAllTextAsync(_filePath);
            var all  = JsonSerializer.Deserialize<List<UserRecord>>(json)
                       ?? [];

            // Guard against race conditions where two registrations arrive simultaneously
            if (all.Any(u => u.Username.Equals(
                    record.Username, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    $"Username '{record.Username}' is already taken.");

            all.Add(record);

            await File.WriteAllTextAsync(_filePath, JsonSerializer.Serialize(all, _writeOptions));

            _log.LogInformation("Registered new user: {Username}", record.Username);
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Overwrites an existing user record in users.json.
    /// Called by UserContext after creating a dataset or assistant
    /// to persist the new ID back to disk.
    /// Throws InvalidOperationException if the username is not found.
    /// </summary>
    /// <param name="updated">The updated UserRecord to save.</param>
    public async Task UpdateAsync(UserRecord updated)
    {
        await _lock.WaitAsync();
        try
        {
            var json = await File.ReadAllTextAsync(_filePath);
            var all = JsonSerializer.Deserialize<List<UserRecord>>(json)
                       ?? [];

            var idx = all.FindIndex(u =>
                u.Username?.Equals(updated.Username, StringComparison.OrdinalIgnoreCase) == true);

            if (idx < 0)
                throw new InvalidOperationException(
                    $"Cannot update: user '{updated.Username}' not found.");

            all[idx] = updated;

            await File.WriteAllTextAsync(_filePath, JsonSerializer.Serialize(all, _writeOptions));

            _log.LogInformation("Updated record for user: {Username}", updated.Username);
        }
        finally { _lock.Release(); }
    }

    // ── Password helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Hashes a plain-text password using ASP.NET Core's PasswordHasher.
    /// The result is safe to store in users.json.
    /// </summary>
    /// <param name="plainPassword">The raw password entered by the user.</param>
    /// <returns>A salted, hashed password string.</returns>
    public string HashPassword(string plainPassword)
    {
        // PasswordHasher requires a user object as context; we pass a blank one
        var dummy = new UserRecord();
        return _hasher.HashPassword(dummy, plainPassword);
    }

    /// <summary>
    /// Verifies a plain-text password against a stored hash.
    /// </summary>
    /// <param name="record">The user record containing the stored hash.</param>
    /// <param name="plainPassword">The raw password to verify.</param>
    /// <returns>True if the password matches; false otherwise.</returns>
    public bool VerifyPassword(UserRecord record, string plainPassword)
    {
        var result = _hasher.VerifyHashedPassword(
            record, record.PasswordHash, plainPassword);

        return result != PasswordVerificationResult.Failed;
    }
}

using Dapper;
using Microsoft.AspNetCore.Identity;
using RagFlowApi.Models;
using System.Text.Json;

namespace RagFlowApi.Services;

public class UserStore
{
    private readonly AppDbContext _db;
    private readonly ILogger<UserStore> _log;
    private readonly PasswordHasher<UserRecord> _hasher = new();

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public UserStore(AppDbContext db, ILogger<UserStore> log)
    {
        _db = db;
        _log = log;
    }

    public async Task<List<UserRecord>> GetAllAsync()
    {
        try
        {
            await using var conn = _db.CreateConnection();
            var rows = await conn.QueryAsync<UserRow>("SELECT * FROM users ORDER BY created_at");
            return rows.Select(ToRecord).ToList();
        }
        catch (Exception ex) { _log.LogError(ex, "GetAllAsync failed"); return []; }
    }

    public async Task<UserRecord?> GetByUsernameAsync(string username)
    {
        try
        {
            await using var conn = _db.CreateConnection();
            var row = await conn.QueryFirstOrDefaultAsync<UserRow>(
                "SELECT * FROM users WHERE username = @u", new { u = username });
            return row is null ? null : ToRecord(row);
        }
        catch (Exception ex) { _log.LogError(ex, "GetByUsernameAsync failed"); return null; }
    }

    public async Task<UserRecord?> GetAdminWithDatasetAsync()
    {
        try
        {
            await using var conn = _db.CreateConnection();
            var row = await conn.QueryFirstOrDefaultAsync<UserRow>(
                "SELECT * FROM users WHERE is_admin = 1 AND dataset_id IS NOT NULL AND dataset_id != '' LIMIT 1");
            return row is null ? null : ToRecord(row);
        }
        catch (Exception ex) { _log.LogError(ex, "GetAdminWithDatasetAsync failed"); return null; }
    }

    public async Task<bool> ExistsAsync(string username)
        => await GetByUsernameAsync(username) is not null;

    public async Task AddAsync(UserRecord record)
    {
        await using var conn = _db.CreateConnection();
        var existing = await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT username FROM users WHERE username = @u", new { u = record.Username });
        if (existing is not null)
            throw new InvalidOperationException($"Username '{record.Username}' is already taken.");

        await conn.ExecuteAsync(@"
            INSERT INTO users
                (username, display_name, password_hash, is_admin,
                 dataset_id, assistant_id, dataset_bound, profile_json, created_at)
            VALUES
                (@Username, @DisplayName, @PasswordHash, @IsAdmin,
                 @DatasetId, @AssistantId, @DatasetBound, @ProfileJson, @CreatedAt)",
            new
            {
                record.Username,
                record.DisplayName,
                record.PasswordHash,
                record.IsAdmin,
                DatasetId   = string.IsNullOrEmpty(record.DatasetId)   ? null : record.DatasetId,
                AssistantId = string.IsNullOrEmpty(record.AssistantId) ? null : record.AssistantId,
                record.DatasetBound,
                ProfileJson = SerializeProfile(record),
                CreatedAt   = record.CreatedAt
            });

        _log.LogInformation("Registered new user: {Username}", record.Username);
    }

    public async Task UpdateAsync(UserRecord updated)
    {
        await using var conn = _db.CreateConnection();
        var affected = await conn.ExecuteAsync(@"
            UPDATE users SET
                display_name  = @DisplayName,
                password_hash = @PasswordHash,
                is_admin      = @IsAdmin,
                dataset_id    = @DatasetId,
                assistant_id  = @AssistantId,
                dataset_bound = @DatasetBound,
                profile_json  = @ProfileJson
            WHERE username = @Username",
            new
            {
                updated.Username,
                updated.DisplayName,
                updated.PasswordHash,
                updated.IsAdmin,
                DatasetId   = string.IsNullOrEmpty(updated.DatasetId)   ? null : updated.DatasetId,
                AssistantId = string.IsNullOrEmpty(updated.AssistantId) ? null : updated.AssistantId,
                updated.DatasetBound,
                ProfileJson = SerializeProfile(updated)
            });

        if (affected == 0)
            throw new InvalidOperationException($"Cannot update: user '{updated.Username}' not found.");

        _log.LogInformation("Updated record for user: {Username}", updated.Username);
    }

    public string HashPassword(string plainPassword)
        => _hasher.HashPassword(new UserRecord(), plainPassword);

    public bool VerifyPassword(UserRecord record, string plainPassword)
        => _hasher.VerifyHashedPassword(record, record.PasswordHash, plainPassword)
           != PasswordVerificationResult.Failed;

    // ── Mapping helpers ───────────────────────────────────────────────────────

    private sealed class UserRow
    {
        public string   username      { get; set; } = "";
        public string   display_name  { get; set; } = "";
        public string   password_hash { get; set; } = "";
        public bool     is_admin      { get; set; }
        public string?  dataset_id    { get; set; }
        public string?  assistant_id  { get; set; }
        public bool     dataset_bound { get; set; }
        public string?  profile_json  { get; set; }
        public DateTime created_at    { get; set; }
    }

    private UserRecord ToRecord(UserRow r)
    {
        var rec = new UserRecord
        {
            Username     = r.username,
            DisplayName  = r.display_name,
            PasswordHash = r.password_hash,
            IsAdmin      = r.is_admin,
            DatasetId    = r.dataset_id    ?? "",
            AssistantId  = r.assistant_id  ?? "",
            DatasetBound = r.dataset_bound,
            CreatedAt    = r.created_at
        };

        if (!string.IsNullOrEmpty(r.profile_json))
        {
            try
            {
                var p = JsonSerializer.Deserialize<ProfileJson>(r.profile_json, _jsonOpts);
                if (p is not null)
                {
                    rec.FullName      = p.FullName;
                    rec.DateOfBirth   = p.DateOfBirth;
                    rec.PlaceOfBirth  = p.PlaceOfBirth;
                    rec.Nationality   = p.Nationality;
                    rec.IdNumber      = p.IdNumber;
                    rec.IdIssuedDate  = p.IdIssuedDate;
                    rec.IdIssuedPlace = p.IdIssuedPlace;
                    rec.JobTitle      = p.JobTitle;
                    rec.Department    = p.Department;
                    rec.PhoneNumber   = p.PhoneNumber;
                    rec.Email         = p.Email;
                    rec.Address       = p.Address;
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not deserialize profile_json for user {User}", r.username);
            }
        }

        return rec;
    }

    private static string SerializeProfile(UserRecord r) =>
        JsonSerializer.Serialize(new ProfileJson(
            r.FullName, r.DateOfBirth, r.PlaceOfBirth, r.Nationality,
            r.IdNumber, r.IdIssuedDate, r.IdIssuedPlace, r.JobTitle,
            r.Department, r.PhoneNumber, r.Email, r.Address));

    private record ProfileJson(
        string FullName, string DateOfBirth, string PlaceOfBirth, string Nationality,
        string IdNumber, string IdIssuedDate, string IdIssuedPlace, string JobTitle,
        string Department, string PhoneNumber, string Email, string Address);
}

namespace RagFlowApi.Models;

/// <summary>
/// Represents a registered user account.
/// Persisted as a JSON object inside users.json.
/// Each user owns exactly one RAGFlow dataset and one RAGFlow assistant,
/// created automatically at registration time.
/// </summary>
public class UserRecord
{
    /// <summary>
    /// Unique login name chosen at registration.
    /// Used as the primary key in users.json and stored in the auth cookie.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// BCrypt hash of the user's password.
    /// The plain-text password is never stored.
    /// </summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>
    /// RAGFlow dataset ID created for this user at registration.
    /// All documents uploaded by this user are stored in this dataset.
    /// </summary>
    public string DatasetId { get; set; } = string.Empty;

    /// <summary>
    /// RAGFlow assistant (chat) ID created for this user at registration.
    /// Bound to the user's personal dataset — all queries from this user
    /// run through this assistant and search only their own data.
    /// </summary>
    public string AssistantId { get; set; } = string.Empty;

    /// <summary>
    /// Friendly name shown in the navigation bar.
    /// Defaults to Username if not provided at registration.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// UTC timestamp of when this account was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>
    /// Whether this user has admin privileges.
    /// Admins see the API (Swagger) link in the navigation bar.
    /// Set manually in users.json after registration.
    /// </summary>
    public bool IsAdmin { get; set; } = false;

    /// <summary>
    /// Whether the assistant has been successfully bound to the dataset.
    /// False for accounts created before automatic binding was added (including
    /// manually-created admin accounts). The next EnsureAssistantAsync call
    /// will perform the binding and set this to true.
    /// </summary>
    public bool DatasetBound { get; set; } = false;
}

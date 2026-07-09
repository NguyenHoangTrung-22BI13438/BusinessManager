using MySqlConnector;

namespace RagFlowApi.Services;

public sealed class AppDbContext(string connectionString)
{
    public MySqlConnection CreateConnection() => new(connectionString);
}

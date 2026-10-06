using System.IO.Compression;
using Microsoft.Data.SqlClient;

namespace DatabaseBackup.Services;

public class DatabaseBackupService
{
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<DatabaseBackupService> _logger;

    public DatabaseBackupService(IConfiguration configuration, IWebHostEnvironment environment, ILogger<DatabaseBackupService> logger)
    {
        _configuration = configuration;
        _environment = environment;
        _logger = logger;
    }

    /// <summary>
    /// Backs up every user database on the server into {root}/{yyyy-MM-dd}/,
    /// zips that folder and returns the zip file path.
    /// </summary>
    public async Task<string> BackupAllAsync(CancellationToken cancellationToken)
    {
        var connectionString = _configuration.GetConnectionString("BackupServer")
            ?? throw new InvalidOperationException("Connection string 'BackupServer' is missing from appsettings.");

        var rootPath = _configuration["Backup:RootPath"];
        if (string.IsNullOrWhiteSpace(rootPath))
            rootPath = Path.Combine(_environment.ContentRootPath, "Backups");

        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var backupFolder = Path.Combine(rootPath, today);
        Directory.CreateDirectory(backupFolder);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var databases = await GetUserDatabasesAsync(connection, cancellationToken);
        _logger.LogInformation("Found {Count} databases to back up", databases.Count);

        foreach (var database in databases)
        {
            var backupFile = Path.Combine(backupFolder, $"{today}_{database}.bak");
            _logger.LogInformation("Backing up {Database} to {File}", database, backupFile);

            await using var command = connection.CreateCommand();
            command.CommandText = $"BACKUP DATABASE {QuoteName(database)} TO DISK = @path WITH INIT, FORMAT";
            command.Parameters.AddWithValue("@path", backupFile);
            command.CommandTimeout = 0;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var zipPath = Path.Combine(rootPath, $"{today}.zip");
        if (File.Exists(zipPath))
            File.Delete(zipPath);

        ZipFile.CreateFromDirectory(backupFolder, zipPath, CompressionLevel.Optimal, includeBaseDirectory: true);
        return zipPath;
    }

    private static async Task<List<string>> GetUserDatabasesAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        // database_id > 4 skips master, tempdb, model and msdb
        const string sql = "SELECT name FROM sys.databases WHERE database_id > 4 AND state_desc = 'ONLINE' ORDER BY name";

        var databases = new List<string>();
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            databases.Add(reader.GetString(0));

        return databases;
    }

    private static string QuoteName(string name) => "[" + name.Replace("]", "]]") + "]";
}

using System.Globalization;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Upload;
using Google.Apis.Util.Store;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace DatabaseBackup.Services;

public record DriveUploadResult(string Id, string Name, long? Size, string WebViewLink);

public class GoogleDriveService
{
    private const string FolderMimeType = "application/vnd.google-apps.folder";

    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<GoogleDriveService> _logger;

    public GoogleDriveService(IConfiguration configuration, IWebHostEnvironment environment, ILogger<GoogleDriveService> logger)
    {
        _configuration = configuration;
        _environment = environment;
        _logger = logger;
    }

    /// <summary>
    /// Uploads a local file into the configured Drive folder (created on first use).
    /// The first call opens a browser for Google sign-in; the refresh token is cached after that.
    /// </summary>
    public async Task<DriveUploadResult> UploadAsync(string filePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"File not found: {filePath}", filePath);

        using var drive = await CreateDriveServiceAsync(cancellationToken);

        var folderName = _configuration["GoogleDrive:FolderName"] ?? "DatabaseBackups";
        var folderId = await GetOrCreateFolderAsync(drive, folderName, cancellationToken);

        var metadata = new DriveFile
        {
            Name = Path.GetFileName(filePath),
            Parents = new[] { folderId }
        };

        await using var stream = File.OpenRead(filePath);
        var request = drive.Files.Create(metadata, stream, "application/octet-stream");
        request.Fields = "id, name, size, webViewLink";
        // Smaller chunks so each HTTP request finishes well within the timeout on slow connections
        request.ChunkSize = ResumableUpload.MinimumChunkSize * 8; // 2 MB
        request.ProgressChanged += progress =>
        {
            if (progress.Status == UploadStatus.Uploading)
                _logger.LogInformation("Uploading {File}: {Bytes:N0} / {Total:N0} bytes", metadata.Name, progress.BytesSent, stream.Length);
        };

        _logger.LogInformation("Uploading {File} to Drive folder {Folder}", filePath, folderName);
        var result = await request.UploadAsync(cancellationToken);
        if (result.Status != UploadStatus.Completed)
            throw new InvalidOperationException($"Upload to Google Drive failed: {result.Exception?.Message}", result.Exception);

        // Only clean up after a successful upload, so Drive never ends up with no backups

        try
        {
        await DeleteOldBackupsAsync(drive, folderId, cancellationToken);
        }
        catch
        {

        }

        var file = request.ResponseBody;
        return new DriveUploadResult(file.Id, file.Name, file.Size, file.WebViewLink);
    }

    /// <summary>
    /// Permanently deletes zips in the backup folder whose name date is older than RetentionDays.
    /// Permanent delete (not trash) so the space is freed right away.
    /// </summary>
    private async Task DeleteOldBackupsAsync(DriveService drive, string folderId, CancellationToken cancellationToken)
    {
        var cutoff = DateTime.Now.AddDays(-DatabaseBackupService.RetentionDays);
        string? pageToken = null;

        do
        {
            var list = drive.Files.List();
            list.Q = $"'{folderId}' in parents and trashed = false";
            list.Fields = "nextPageToken, files(id, name)";
            list.PageSize = 100;
            list.PageToken = pageToken;

            var page = await list.ExecuteAsync(cancellationToken);
            foreach (var file in page.Files)
            {
                if (!file.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    || !DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file.Name), DatabaseBackupService.BackupNameFormat,
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var createdAt)
                    || createdAt >= cutoff)
                    continue;

                try
                {
                    await drive.Files.Delete(file.Id).ExecuteAsync(cancellationToken);
                    _logger.LogInformation("Deleted old Drive backup {File}", file.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not delete old Drive backup {File}", file.Name);
                }
            }

            pageToken = page.NextPageToken;
        } while (pageToken != null);
    }

    private async Task<DriveService> CreateDriveServiceAsync(CancellationToken cancellationToken)
    {
        var credentialsPath = ResolvePath(_configuration["GoogleDrive:CredentialsPath"] ?? "google-credentials.json");
        var tokenStorePath = ResolvePath(_configuration["GoogleDrive:TokenStorePath"] ?? "GoogleToken");

        if (!File.Exists(credentialsPath))
            throw new FileNotFoundException($"Google OAuth client file not found: {credentialsPath}", credentialsPath);

        GoogleClientSecrets secrets;
        await using (var credentialsStream = File.OpenRead(credentialsPath))
            secrets = await GoogleClientSecrets.FromStreamAsync(credentialsStream, cancellationToken);

        // drive.file only lets the app see files and folders it created itself
        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            secrets.Secrets,
            new[] { DriveService.Scope.DriveFile },
            "user",
            cancellationToken,
            new FileDataStore(tokenStorePath, fullPath: true));

        var drive = new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "DatabaseBackup"
        });
        drive.HttpClient.Timeout = TimeSpan.FromHours(2);
        return drive;
    }

    private static async Task<string> GetOrCreateFolderAsync(DriveService drive, string folderName, CancellationToken cancellationToken)
    {
        var escapedName = folderName.Replace("\\", "\\\\").Replace("'", "\\'");
        var list = drive.Files.List();
        list.Q = $"mimeType = '{FolderMimeType}' and name = '{escapedName}' and trashed = false";
        list.Fields = "files(id)";
        list.PageSize = 1;

        var existing = await list.ExecuteAsync(cancellationToken);
        if (existing.Files.Count > 0)
            return existing.Files[0].Id;

        var create = drive.Files.Create(new DriveFile { Name = folderName, MimeType = FolderMimeType });
        create.Fields = "id";
        var folder = await create.ExecuteAsync(cancellationToken);
        return folder.Id;
    }

    private string ResolvePath(string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(_environment.ContentRootPath, path);
}

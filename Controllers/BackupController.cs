using DatabaseBackup.Services;
using Microsoft.AspNetCore.Mvc;

namespace DatabaseBackup.Controllers;

[ApiController]
[Route("api/[controller]")]
public class BackupController : ControllerBase
{
    private readonly DatabaseBackupService _backupService;
    private readonly GoogleDriveService _driveService;

    public BackupController(DatabaseBackupService backupService, GoogleDriveService driveService)
    {
        _backupService = backupService;
        _driveService = driveService;
    }

    [HttpGet("BackupAndSendToGoogleDrive")]
    public async Task<IActionResult> BackupAll(CancellationToken cancellationToken)
    {
        try
        {
            string? zipPath = "";
            try
            {

                zipPath = await _backupService.BackupAllAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                return Ok($"Error during backup: {ex.Message} : Inner Exception {ex?.InnerException?.Message}");
            }


            if (!System.IO.File.Exists(zipPath))
                return Ok($"File NOt Fount After BackUp");

            try
            {

                var result = await _driveService.UploadAsync(zipPath, cancellationToken);
                return Ok(result);
            }
            catch (Exception ex)
            {
                return Ok($"Error during upload: {ex.Message} : Inner Exception {ex?.InnerException?.Message}");


            }
        }
        catch (Exception ex)
        {
            return Ok($"Error uploading to Google Drive: {ex.Message} : Inner Exception {ex?.InnerException?.Message}");
        }
         
      //  return PhysicalFile(zipPath, "application/zip", Path.GetFileName(zipPath));
    }
}

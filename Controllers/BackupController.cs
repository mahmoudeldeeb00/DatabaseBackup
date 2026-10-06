using DatabaseBackup.Services;
using Microsoft.AspNetCore.Mvc;

namespace DatabaseBackup.Controllers;

[ApiController]
[Route("api/[controller]")]
public class BackupController : ControllerBase
{
    private readonly DatabaseBackupService _backupService;

    public BackupController(DatabaseBackupService backupService)
    {
        _backupService = backupService;
    }

    [HttpGet]
    public async Task<IActionResult> BackupAll(CancellationToken cancellationToken)
    {
        var zipPath = await _backupService.BackupAllAsync(cancellationToken);
        return PhysicalFile(zipPath, "application/zip", Path.GetFileName(zipPath));
    }
}

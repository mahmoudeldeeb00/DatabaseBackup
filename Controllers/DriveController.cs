using DatabaseBackup.Services;
using Microsoft.AspNetCore.Mvc;

namespace DatabaseBackup.Controllers;

public record DriveUploadRequest(string FilePath);

[ApiController]
[Route("api/[controller]")]
public class DriveController : ControllerBase
{
    private readonly GoogleDriveService _driveService;

    public DriveController(GoogleDriveService driveService)
    {
        _driveService = driveService;
    }

    [HttpPost("upload")]
    public async Task<ActionResult<DriveUploadResult>> Upload(DriveUploadRequest request, CancellationToken cancellationToken)
    {
        if (!System.IO.File.Exists(request.FilePath))
            return NotFound($"File not found: {request.FilePath}");

        return await _driveService.UploadAsync(request.FilePath, cancellationToken);
    }
}

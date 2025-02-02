using Damselfly.Core.Constants;
using Damselfly.Core.DbModels.Models;
using Damselfly.Core.DbModels.Models.APIModels;
using Damselfly.Core.ScopedServices.Interfaces;
using Damselfly.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Damselfly.Web.Server.Controllers;

//[Authorize(Policy = PolicyDefinitions.s_IsDownloader)]
[ApiController]
[Route("/api/download")]
public class DownloadController(IDownloadService service, ILogger<DownloadController> logger, ImageService imageService) : ControllerBase
{
    private readonly IDownloadService _downloadService = service;
    private readonly ImageService _imageService = imageService;

    private readonly ILogger<DownloadController> _logger = logger;

    [HttpGet("/api/download/desktopapppaths")]
    public async Task<DesktopAppPaths> DesktopAppPaths()
    {
        return await _downloadService.GetDesktopAppInfo();
    }

    [HttpPost("/api/download/images")]
    public async Task<DownloadResponse> GetImagesDownload(DownloadRequest req)
    {
        var authorizedImages = new List<Guid>();
        foreach( var id in req.ImageIds )
        {
            if( await _imageService.CanDownload(id, req.Password) )
            {
                authorizedImages.Add(id);
            }
        }
        _ = Task.Run(() =>_downloadService.CreateDownloadZipAsync(authorizedImages, req.Config, req.ConnectionId));
        return new DownloadResponse { StartedSuccessfully = true };
    }
}
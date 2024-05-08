using Damselfly.Core.Constants;
using Damselfly.Core.Database;
using Damselfly.Core.DbModels.Authentication;
using Damselfly.Core.DbModels.Models.API_Models;
using Damselfly.Core.Interfaces;
using Damselfly.Core.Models;
using Damselfly.Core.Services;
using Damselfly.Core.Utils;
using Damselfly.Shared.Utils;
using Damselfly.Web.Server.CustomAttributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Damselfly.Web.Controllers;

//[Authorize(Policy = PolicyDefinitions.s_IsLoggedIn)]
[Route("images")]
[ApiController]
public class ImageController(ImageService imageService, ILogger<ImageController> logger) : Controller
{
    private ILogger<ImageController> _logger = logger;
    private readonly ImageService _imageService = imageService;

    
    [HttpPost]
    [ProducesResponseType(typeof(List<ImageModel>), 200)]
    [Route("upload")]
    [Authorize(Policy = PolicyDefinitions.s_FireBaseAdmin)]
    public async Task<IActionResult> UploadImage([FromForm] UploadImageRequest uploadImageRequest)
    {
        _logger.LogInformation($"Uploading {uploadImageRequest.ImageFiles.Count} image(s)");
        var watch = new Stopwatch("ControllerPostImage");

        var result = await _imageService.CreateImages(uploadImageRequest);

        watch.Stop();

        return Ok(result);
    }

    [HttpGet]
    [Route("imageData/{id}")]
    [ProducesResponseType(typeof(ImageModel), 200)]
    public async Task<IActionResult> GetImageData(Guid id, [FromQuery] string? password)
    {
        var image = await _imageService.GetImageData(id, password);
        if ( image == null )
            return NotFound();
        return Ok(image);
    }

    [HttpDelete]
    [Route("{id}")]
    [ProducesResponseType(typeof(BooleanResultModel), 200)]
    [Authorize(Policy = PolicyDefinitions.s_FireBaseAdmin)]
    public async Task<IActionResult> DeleteImage(Guid id)
    {
        var result = await _imageService.DeleteImage(id);
        if ( result )
            return Ok(new BooleanResultModel{ Result = true });
        return NotFound();
    }

    [Produces("image/jpeg")]
    [HttpGet("/dlimage/{imageId}")]
    public async Task<IActionResult> Image(string imageId, CancellationToken cancel,
        [FromServices] ImageCache imageCache)
    {
        return await Image(imageId, cancel, imageCache, true);
    }

    [Produces("image/jpeg")]
    [HttpGet("/rawimage/{imageId}")]
    public async Task<IActionResult> Image(string imageId, CancellationToken cancel,
        [FromServices] ImageCache imageCache, bool isDownload = false)
    {
        var watch = new Stopwatch("ControllerGetImage");

        IActionResult result = Redirect("/no-image.png");

        if ( Guid.TryParse(imageId, out var id) )
            try
            {
                var image = await imageCache.GetCachedImage(id);

                if ( cancel.IsCancellationRequested )
                    return result;

                if ( image != null )
                {
                    string? downloadFilename = null;

                    if ( isDownload )
                        downloadFilename = image.FileName;

                    if ( cancel.IsCancellationRequested )
                        return result;

                    result = PhysicalFile(image.FullPath, "image/jpeg", downloadFilename);
                }
            }
            catch ( Exception ex )
            {
                Logging.LogError($"No thumb available for /rawmage/{imageId}: ", ex.Message);
            }

        watch.Stop();

        return result;
    }

    [Produces("image/jpeg")]
    [HttpGet("/thumb/{thumbSize}/{imageId}")]
    public async Task<IActionResult> Thumb(string thumbSize, string imageId, CancellationToken cancel,
        [FromServices] ImageCache imageCache, [FromServices] ThumbnailService thumbService, [FromQuery] string? password)
    {
        var watch = new Stopwatch("ControllerGetThumb");

        IActionResult result = Redirect("/no-image.png");

        if ( Enum.TryParse<ThumbSize>(thumbSize, true, out var size) && Guid.TryParse(imageId, out var id) )
            try
            {
                Logging.LogTrace($"Controller - Getting Thumb for {imageId}");
                var hasPermission = await _imageService.CheckPassword(id, password);
                if (!hasPermission)
                {
                    return Unauthorized();
                }
                var image = await imageCache.GetCachedImage(id);

                if ( cancel.IsCancellationRequested )
                    return result;

                if ( image != null )
                {
                    if ( cancel.IsCancellationRequested )
                        return result;

                    Logging.LogTrace($" - Getting thumb path for {imageId}");

                    var file = new FileInfo(image.FullPath);
                    var imagePath = thumbService.GetThumbPath(file, size);
                    var gotThumb = true;


                    if ( !System.IO.File.Exists(imagePath) )
                    {
                        gotThumb = false;
                        Logging.LogTrace($" - Generating thumbnail on-demand for {image.FileName}...");

                        if ( cancel.IsCancellationRequested )
                            return result;

                        var conversionResult = await thumbService.ConvertFile(image, false, size);

                        if ( conversionResult.ThumbsGenerated )
                            gotThumb = true;
                    }

                    if ( cancel.IsCancellationRequested )
                        return result;

                    if ( gotThumb )
                    {
                        Logging.LogTrace($" - Loading file for {imageId}");

                        result = PhysicalFile(imagePath, "image/jpeg");
                    }

                    Logging.LogTrace($"Controller - served thumb for {imageId}");
                }
            }
            catch ( Exception ex )
            {
                Logging.LogError($"Unable to process /thumb/{thumbSize}/{imageId}: {ex.Message}");
            }

        watch.Stop();

        return result;
    }

    private async Task UpdateThumbStatus(Image image, IImageProcessResult conversionResult, ImageContext db)
    {
        Logging.LogTrace($" - Updating metadata for {image.ImageId}");
        try
        {
            if ( image.MetaData != null )
            {
                db.Attach(image.MetaData);
                image.MetaData.ThumbLastUpdated = DateTime.UtcNow;
                db.ImageMetaData.Update(image.MetaData);
            }
            else
            {
                var metadata = new ImageMetaData
                {
                    ImageId = image.ImageId,
                    ThumbLastUpdated = DateTime.UtcNow
                };
                db.ImageMetaData.Add(metadata);
                image.MetaData = metadata;
            }

            await db.SaveChangesAsync("ThumbUpdate");
        }
        catch ( Exception ex )
        {
            Logging.LogWarning($"Unable to update DB thumb for ID {image.ImageId}: {ex.Message}");
        }
    }

    //[Produces("image/jpeg")]
    //[HttpGet("/face/{faceId}")]
    //public async Task<IActionResult> Face(string faceId, CancellationToken cancel,
    //    [FromServices] ImageProcessService imageProcessor,
    //    [FromServices] ThumbnailService thumbService,
    //    [FromServices] ImageCache imageCache,
    //    [FromServices] ImageContext db)
    //{
    //    IActionResult result = Redirect("/no-image.png");

    //    try
    //    {
    //        var query = db.ImageObjects.AsQueryable();

    //        // TODO Massively optimise this - if the file already exists we don't need the DB
    //        if ( int.TryParse(faceId, out var personId) )
    //            query = query.Where(x => x.Person.PersonId == personId);
    //        else
    //            query = query.Where(x => x.Person.PersonGuid == faceId);

    //        // Sort by largest face picture, then by most recent date taken
    //        var face = await query
    //            .OrderByDescending(x => x.RectWidth)
    //            .ThenByDescending(x => x.RectHeight)
    //            .ThenByDescending(x => x.Image.SortDate)
    //            .FirstOrDefaultAsync();

    //        if ( cancel.IsCancellationRequested )
    //            return result;

    //        if ( face != null )
    //        {
    //            var thumbPath = await thumbService.GenerateFaceThumb(face);

    //            if ( thumbPath != null && thumbPath.Exists ) result = PhysicalFile(thumbPath.FullName, "image/jpeg");
    //        }
    //    }
    //    catch ( Exception ex )
    //    {
    //        Logging.LogError($"Unable to load face thumbnail for {faceId}: {ex.Message}");
    //    }

    //    return result;
    //}
}
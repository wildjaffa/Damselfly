using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Damselfly.Core.Constants;
using Damselfly.Core.Database;
using Damselfly.Core.Interfaces;
using Damselfly.Core.Models;
using Damselfly.Core.ScopedServices.Interfaces;
using Damselfly.Core.Utils;
using Damselfly.Shared.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using static Damselfly.Core.Models.ExifOperation;
using Stopwatch = Damselfly.Shared.Utils.Stopwatch;

namespace Damselfly.Core.Services;

/// <summary>
///     Service to add/remove/update tags on image files on disk. This uses
///     ExifTool to do the actual EXIF tag manipulation on the disk files,
///     since there aren't currently any native C# libraries which can
///     efficiently write tags to images without re-encoding the JPEG data
///     (which would be lossy, and therefore destructive). Plus ExifTool
///     is FAST!
/// </summary>
public class ExifService : IProcessJobFactory, ITagService
{
    private readonly ImageCache _imageCache;
    private readonly IndexingService _indexingService;
    private readonly ServerNotifierService _notifier;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IStatusService _statusService;
    private readonly WorkService _workService;
    private readonly string _exifToolPath;

    private readonly List<Tag> faveTags = new();

    public ExifService(IStatusService statusService, WorkService workService,
        IndexingService indexingService, ImageCache imageCache,
        ServerNotifierService notifier, IServiceScopeFactory scopeFactory,
        IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _statusService = statusService;
        _imageCache = imageCache;
        _indexingService = indexingService;
        _workService = workService;
        _notifier = notifier;
        _exifToolPath = configuration["ExifTool:ExePath"]!;

        GetExifToolVersion();
        _ = LoadFavouriteTagsAsync();

        // _workService.AddJobSource(this);
    }

    public static string ExifToolVer { get; private set; }

    public JobPriorities Priority => JobPriorities.ExifService;

    public async Task<ICollection<IProcessJob>> GetPendingJobs(int maxCount)
    {
        using var scope = _scopeFactory.CreateScope();
        using var db = scope.ServiceProvider.GetService<ImageContext>();

        // We skip any operations where the timestamp is more recent than 30s
        var timeThreshold = DateTime.UtcNow.AddSeconds(-1 * 30);

        // Find all the operations that are pending, and the timestamp is older than the threshold.
        var opsToProcess = await db.KeywordOperations.AsQueryable()
            .Where(x => x.State == ExifOperation.FileWriteState.Pending && x.TimeStamp < timeThreshold)
            .OrderByDescending(x => x.TimeStamp)
            .Take(maxCount)
            .Include(x => x.Image)
            .ToListAsync();

        var conflatedOps = await ConflateOperations(opsToProcess);

        var jobs = conflatedOps.Select(x => new ExifProcess
        {
            ImageId = x.Key,
            ExifOps = x.Value,
            Service = this
        }).ToArray();

        return jobs;
    }

    public event Action OnFavouritesChanged;
    public event Action<ICollection<string>> OnUserTagsAdded;

    public Task<ICollection<Tag>> GetFavouriteTags()
    {
        ICollection<Tag> result = faveTags;
        return Task.FromResult(result);
    }


    /// <summary>
    ///     Takes an image and a set of keywords, and writes them to the DB queue for
    ///     keywords to be added. These will then be processed asynchronously.
    /// </summary>
    /// <param name="images"></param>
    /// <param name="tagsToAdd"></param>
    /// <param name="tagsToRemove"></param>
    /// <returns></returns>
    public async Task UpdateTagsAsync(ICollection<Guid> imageIds, ICollection<string>? addTags,
        ICollection<string>? removeTags = null, int? userId = null)
    {
        // TODO: Split tags with commas here?
        var timestamp = DateTime.UtcNow;
        var changeDesc = string.Empty;

        using var scope = _scopeFactory.CreateScope();
        using var db = scope.ServiceProvider.GetService<ImageContext>();

        var keywordOps = new List<ExifOperation>();

        if ( addTags != null )
        {
            var tagsToAdd = addTags.Where(x => !string.IsNullOrEmpty(x.Trim())).ToList();

            foreach ( var imageId in imageIds )
                keywordOps.AddRange(tagsToAdd.Select(keyword => new ExifOperation
                {
                    ImageId = imageId,
                    Text = keyword.Sanitise(),
                    Type = ExifOperation.ExifType.Keyword,
                    Operation = ExifOperation.OperationType.Add,
                    TimeStamp = timestamp,
                    UserId = userId
                }));

            var tagsAdded = string.Join( ',', tagsToAdd );
            changeDesc += $"added: {tagsAdded}";
        }

        if ( removeTags != null )
        {
            var tagsToRemove = removeTags.Where(x => !string.IsNullOrEmpty(x.Trim())).ToList();

            foreach ( var imageId in imageIds )
                keywordOps.AddRange(tagsToRemove.Select(keyword =>
                    new ExifOperation
                    {
                        ImageId = imageId,
                        Text = keyword.Sanitise(),
                        Type = ExifOperation.ExifType.Keyword,
                        Operation = ExifOperation.OperationType.Remove,
                        TimeStamp = timestamp
                    }));

            if ( !string.IsNullOrEmpty(changeDesc) )
                changeDesc += ", ";
            changeDesc += $"removed: {string.Join(',', tagsToRemove)}";
        }

        Logging.LogVerbose(
            $"Bulk inserting {keywordOps.Count()} keyword operations (for {imageIds.Count()}) into queue. ");

        try
        {
            await db.BulkInsert(db.KeywordOperations, keywordOps);

            _statusService.UpdateStatus($"Saved tags ({changeDesc}) for {imageIds.Count()} images.");
        }
        catch ( Exception ex )
        {
            Logging.LogError($"Exception inserting keyword operations: {ex.Message}");
        }

        if ( userId != -1 )
            NotifyUserTagsAdded(addTags);

        // Trigger the work service to look for new jobs
        _workService.FlagNewJobs(this);
    }

    /// <summary>
    ///     Takes an image and a set of keywords, and writes them to the DB queue for
    ///     keywords to be added. These will then be processed asynchronously.
    /// </summary>
    /// <param name="images"></param>
    /// <param name="tagsToAdd"></param>
    /// <param name="tagsToRemove"></param>
    /// <returns></returns>
    public async Task SetExifFieldAsync(ICollection<Guid> imageIds, ExifOperation.ExifType exifType, string newValue,
        int? userId = null)
    {
        var timestamp = DateTime.UtcNow;
        var changeDesc = string.Empty;

        using var scope = _scopeFactory.CreateScope();
        using var db = scope.ServiceProvider.GetService<ImageContext>();

        var keywordOps = new List<ExifOperation>();

        keywordOps.AddRange(imageIds.Select(imageId => new ExifOperation
        {
            ImageId = imageId,
            Text = newValue,
            Type = exifType,
            Operation = ExifOperation.OperationType.Add,
            TimeStamp = timestamp,
            UserId = userId
        }));

        changeDesc += $"set {exifType}";

        Logging.LogVerbose(
            $"Inserting {keywordOps.Count()} {exifType} operations (for {imageIds.Count()}) into queue. ");

        try
        {
            await db.BulkInsert(db.KeywordOperations, keywordOps);

            _statusService.UpdateStatus($"Saved {exifType} ({changeDesc}) for {imageIds.Count()} images.");
        }
        catch ( Exception ex )
        {
            Logging.LogError($"Exception inserting {exifType} operations: {ex.Message}");
        }

        // Trigger the work service to look for new jobs
        _workService.FlagNewJobs(this);
    }

    /// <summary>
    ///     Switches a tag from favourite, to not favourite, or back
    /// </summary>
    /// <param name="tag"></param>
    /// <returns></returns>
    public async Task<bool> ToggleFavourite(Tag tag)
    {
        using var scope = _scopeFactory.CreateScope();
        using var db = scope.ServiceProvider.GetService<ImageContext>();

        // TODO: Async - use BulkUpdateAsync?
        tag.Favourite = !tag.Favourite;

        db.Tags.Update(tag);
        await db.SaveChangesAsync("Tag favourite");

        await LoadFavouriteTagsAsync();

        return tag.Favourite;
    }

    private void NotifyFavouritesChanged()
    {
        OnFavouritesChanged?.Invoke();

        _ = _notifier.NotifyClients(NotificationType.FavouritesAndRecentsChanged);
    }

    private void NotifyUserTagsAdded(ICollection<string> tagsAdded)
    {
        OnUserTagsAdded?.Invoke(tagsAdded);

        _ = _notifier.NotifyClients(NotificationType.FavouritesAndRecentsChanged, tagsAdded);
    }

    /// <summary>
    ///     Check the ExifTool at startup
    /// </summary>
    private void GetExifToolVersion()
    {
        if( ExifToolVer != null ) return;
        var process = new ProcessStarter();
        if ( process.StartProcess(_exifToolPath, "-ver") ) ExifToolVer = $"v{process.OutputText}";

        if ( string.IsNullOrEmpty(ExifToolVer) ) ExifToolVer = "Unavailable - ExifTool Not found";

        Logging.Log($"ExifTool Version: {ExifToolVer}");
    }

    /// <summary>
    ///     Add a list of IPTC tags to an image on the disk.
    /// </summary>
    /// <param name="images"></param>
    /// <param name="tagToAdd"></param>
    /// <returns></returns>
    public async Task AddTagAsync(Image[] images, string tagToAdd)
    {
        var imageIds = images.Select(x => x.ImageId).ToList();
        var tagList = new List<string> { tagToAdd };
        await UpdateTagsAsync(imageIds, tagList);
    }

    /// <summary>
    ///     Takes an image and a set of keywords, and writes them to the DB queue for
    ///     keywords to be added. These will then be processed asynchronously.
    /// </summary>
    /// <param name="images"></param>
    /// <param name="tagsToAdd"></param>
    /// <param name="tagsToRemove"></param>
    /// <returns></returns>
    public async Task UpdateTagsAsync(Image image, List<string>? addTags, List<string>? removeTags = null,
        int? userId = null)
    {
        await UpdateTagsAsync(new[] { image.ImageId }, addTags, removeTags, userId);
    }

    /// <summary>
    ///     Takes an image and a set of keywords, and writes them to the DB queue for
    ///     keywords to be added. These will then be processed asynchronously.
    /// </summary>
    /// <param name="images"></param>
    /// <param name="tagsToAdd"></param>
    /// <param name="tagsToRemove"></param>
    /// <returns></returns>
    public async Task UpdateFaceDataAsync(Image[] images, List<ImageObject> faces, int? userId = null)
    {
#if ! DEBUG
        // Not supported yet....
        return;
# endif

        var timestamp = DateTime.UtcNow;
        var changeDesc = string.Empty;

        using var scope = _scopeFactory.CreateScope();
        using var db = scope.ServiceProvider.GetService<ImageContext>();

        var ops = new List<ExifOperation>();

        if ( faces != null )
        {
            foreach ( var image in images )
                ops.AddRange(faces.Select(face => new ExifOperation
                {
                    ImageId = image.ImageId,
                    Text = JsonSerializer.Serialize(face),
                    Type = ExifOperation.ExifType.Face,
                    Operation = ExifOperation.OperationType.Add,
                    TimeStamp = timestamp,
                    UserId = userId
                }));
            changeDesc = $"added {faces.Count} face tags";
        }

        Logging.LogVerbose($"Bulk inserting {ops.Count()} face exif operations (for {images.Count()}) into queue. ");

        try
        {
            await db.BulkInsert(db.KeywordOperations, ops);

            _statusService.UpdateStatus($"Saved tags ({changeDesc}) for {images.Count()} images.");
        }
        catch ( Exception ex )
        {
            Logging.LogError($"Exception inserting keyword operations: {ex.Message}");
        }

        // Trigger the work service to look for new jobs
        _workService.FlagNewJobs(this);
    }

    /// <summary>
    ///     Helper method to actually run ExifTool and update the tags on disk.
    /// </summary>
    /// <param name="imagePath"></param>
    /// <param name="tagsToAdd"></param>
    /// <param name="tagsToRemove"></param>
    /// <returns></returns>
    private async Task<bool> ProcessExifOperations(Guid imageId, List<ExifOperation> exifOperations)
    {
        var success = false;

        var image = await _imageCache.GetCachedImage(imageId);

        Logging.LogVerbose("Updating tags for file {0}", image.FullPath);
        var args = string.Empty;
        var opsToProcess = new List<ExifOperation>();

        foreach ( var op in exifOperations )
        {
            var operationText = op.Text.RemoveSmartQuotes();

            // We need to escape double-quotes.
            operationText = operationText.Replace("\"", "\\\"");

            if ( string.IsNullOrEmpty(operationText) )
            {
                Logging.LogWarning($"Exif Operation with empty text: {op.Image.FileName}.");
                continue;
            }

            if ( op.Type == ExifOperation.ExifType.Keyword )
            {
                // Weird but important: we *alwaya* add a -= for the keyword,
                // whether we're removing or adding it. Removing is self-evident,
                // but adding is less intuitive. The reason is to avoid duplicate
                // keywords. So if we do
                //      "-keywords-=Banana -keywords+=Banana",
                // this will remove the tag and re-add it if it already exists
                // (creating a no-op) but the remove will do nothing if it doesn't
                // exist. Thus, we ensure we don't add keywords twice.
                // See: https://stackoverflow.com/questions/67282388/adding-multiple-keywords-with-exiftool-but-only-if-theyre-not-already-present
                args += $" -keywords-=\"{operationText}\" ";

                if ( op.Operation == ExifOperation.OperationType.Remove )
                {
                    Logging.LogVerbose($" Removing keyword {operationText} from {op.Image.FileName}");
                    opsToProcess.Add(op);
                }
                else if ( op.Operation == ExifOperation.OperationType.Add )
                {
                    Logging.LogVerbose($" Adding keyword '{operationText}' to {op.Image.FileName}");
                    args += $" -keywords+=\"{operationText}\" ";
                    opsToProcess.Add(op);
                }
            }
            else if ( op.Type == ExifOperation.ExifType.Caption )
            {
                args += $" -iptc:Caption-Abstract=\"{op.Text}\"";
                opsToProcess.Add(op);
            }
            else if ( op.Type == ExifOperation.ExifType.Description )
            {
                args += $" -Exif:ImageDescription=\"{op.Text}\"";
                opsToProcess.Add(op);
            }
            else if ( op.Type == ExifOperation.ExifType.Copyright )
            {
                args += $" -Copyright=\"{op.Text}\"";
                args += $" -iptc:CopyrightNotice=\"{op.Text}\"";
                opsToProcess.Add(op);
            }
            else if ( op.Type == ExifOperation.ExifType.Rating )
            {
                args += $" -exif:Rating=\"{op.Text}\"";
                opsToProcess.Add(op);
            }
            else if( op.Type == ExifOperation.ExifType.Rotate )
            {
                int rotationDegrees = Convert.ToUInt16( op.Text );
                args += $" -orientation=\"Rotate {rotationDegrees} CW\"";
                opsToProcess.Add( op );
            }
            else if ( op.Type == ExifOperation.ExifType.Face )
            {
                var imageObject = JsonSerializer.Deserialize<ImageObject>(op.Text);

                // Face tags using MGW standard
                // exiftool -xmp-mwg-rs:RegionAppliedToDimensionsH=4000 -xmp-mwg-rs:RegionAppliedToDimensionsUnit="pixel" -xmp-mwg-rs:RegionAppliedToDimensionsW=6000
                // -xmp-mwg-rs:RegionAreaX=0.319270833 -xmp-mwg-rs:RegionAreaY=0.21015625 -xmp-mwg-rs:RegionAreaW=0.165104167 -xmp-mwg-rs:RegionAreaH=0.30390625
                // -xmp-mwg-rs:RegionName=John -xmp-mwg-rs:RegionRotation=0 -xmp-mwg-rs:RegionType="Face" myfile.xmp

                if ( Debugger.IsAttached )
                {
                    // TODO: How to add multiple faces?
                    args += " -xmp-mwg-rs:RegionType=\"Face\"";
                    args += " -xmp-mwg-rs:RegionAppliedToDimensionsUnit=\"pixel\"";
                    args += " -xmp-mwg-rs:RegionAppliedToDimensionsH=4000";
                    args += " -xmp-mwg-rs:RegionAppliedToDimensionsW=6000";
                    args += " -xmp-mwg-rs:RegionAreaX=0.319270833 -xmp-mwg-rs:RegionAreaY=0.21015625";
                    args += " -xmp-mwg-rs:RegionAreaW=0.165104167 -xmp-mwg-rs:RegionAreaH=0.30390625";
                    args += " -xmp-mwg-rs:RegionRotation=0";

                    if ( imageObject.Person != null ) args += $" -xmp-mwg-rs:RegionName={imageObject.Person.Name}";

                    opsToProcess.Add(op);
                }
            }
        }

        // Assume they've all failed unless we succeed below.
        exifOperations.ForEach(x => x.State = ExifOperation.FileWriteState.Failed);

        if ( opsToProcess.Any() )
        {
            // Note: we could do this to preserve the last-mod-time:
            //   args += " -P -overwrite_original_in_place";
            // However, we rely on the last-mod-time changing to pick up
            // changes to keywords and to subsequently re-index images.
            args += " -overwrite_original ";
            // We enable the 'ignore minor warnings' flag which will allow
            // us to do things like write tags that are too long for the
            // IPTC specification.
            args += " -m ";
            args += " \"" + image.FullPath + "\"";

            var process = new ProcessStarter();

            // Fix perl local/env issues for exiftool
            var env = new Dictionary<string, string>();
            env["LANGUAGE"] = "en_US.UTF-8";
            env["LANG"] = "en_US.UTF-8";
            env["LC_ALL"] = "en_US.UTF-8";

            var watch = new Stopwatch("RunExifTool");
            success = process.StartProcess(_exifToolPath, args, env);
            watch.Stop();

            if ( success )
            {
                opsToProcess.ForEach(x => x.State = ExifOperation.FileWriteState.Written);

                // Updating the timestamp on the image to newer than its metadata will
                // trigger its metadata and tags to be refreshed during the next scan
                await _indexingService.MarkImagesForScan(new List<Guid> { image.ImageId });
            }
            else
            {
                Logging.LogError("ExifTool Tag update failed for image: {0}", image.FullPath);
                RestoreTempExifImage(image.FullPath);
            }
        }

        using var scope = _scopeFactory.CreateScope();
        using var db = scope.ServiceProvider.GetService<ImageContext>();

        // Now write the updates
        await db.BulkUpdate(db.KeywordOperations, exifOperations);

        // Now write a summary of how many succeeded and failed.
        var totals = string.Join(", ", exifOperations.GroupBy(x => x.State)
            .Select(x => $"{x.Key}: {x.Count()}")
            .ToList());

        _statusService.UpdateStatus($"EXIF data written for {image.FileName} (ID: {image.ImageId}). {totals}");

        NotifyFavouritesChanged();

        return success;
    }

    /// <summary>
    ///     If ExifTool fails, sometimes it can leave the temp file in place, meaning our image
    ///     will go missing. So try and put it back.
    ///     See: https://stackoverflow.com/questions/65870251/make-exiftool-overwrite-original-is-transactional-and-atomic
    /// </summary>
    /// <param name="imagePath"></param>
    private void RestoreTempExifImage(string imagePath)
    {
        var path = new FileInfo(imagePath);
        var newExtension = path.Extension + "_exiftool_tmp";
        var tempPath = new FileInfo(Path.ChangeExtension(imagePath, newExtension));

        if ( !path.Exists && tempPath.Exists )
        {
            Logging.Log($"Moving {tempPath.FullName} to {path.Name}...");
            try
            {
                File.Move(tempPath.FullName, path.FullName);
            }
            catch ( Exception ex )
            {
                Logging.LogWarning($"Unable to move file: {ex.Message}");
            }
        }
    }

    /// <summary>
    ///     Clean up processed keyword operations
    /// </summary>
    /// <param name="cleanupFreq"></param>
    public async Task CleanUpKeywordOperations(TimeSpan cleanupFreq)
    {
        using var scope = _scopeFactory.CreateScope();
        using var db = scope.ServiceProvider.GetService<ImageContext>();

        // Clean up completed operations older than 6 months
        var cutOff = DateTime.UtcNow.AddMonths(-6);

        try
        {
            var cleanedUp = await db.BatchDelete(db.KeywordOperations.Where(op =>
                op.State == ExifOperation.FileWriteState.Written
                && op.TimeStamp < cutOff));

            Logging.LogVerbose($"Cleaned up {cleanedUp} completed Keyword Operations.");
        }
        catch ( Exception ex )
        {
            Logging.LogError($"Exception whilst cleaning up keyword operations: {ex.Message}");
        }
    }


    /// <summary>
    ///     Takes a list of time-ordered keyword operations (add/remove) and for
    ///     each keyword conflates down to a single distinct operation. So if there
    ///     was:
    ///     Image1 Add 'cat'
    ///     Image2 Add 'cat'
    ///     Image2 Remove 'cat'
    ///     Image2 Add 'dog'
    ///     Image2 Add 'cat'
    ///     Image1 Remove 'cat'
    ///     THen this would conflate down to:
    ///     Image2 Add 'cat'
    ///     Image2 Add 'dog'
    ///     Image1 Remove 'cat'
    /// </summary>
    /// <param name="tagsToProcess"></param>
    /// <returns></returns>
    private async Task<IDictionary<Guid, List<ExifOperation>>> ConflateOperations(List<ExifOperation> opsToProcess)
    {
        // The result is the image ID, and a list of conflated ops.
        var result = new Dictionary<Guid, List<ExifOperation>>();
        var discardedOps = new List<ExifOperation>();

        // First, conflate the keywords.
        var imageKeywords = opsToProcess.Where(x => x.Type == ExifOperation.ExifType.Keyword)
            .GroupBy(x => x.Image.ImageId);

        foreach ( var imageOpList in imageKeywords )
        {
            var theImage = imageOpList.Key;
            var keywordOps = imageOpList.GroupBy(x => x.Text);

            // Now, for each exif change for this image, collect up the most
            // recent operation and store it in a dictionary. Note, text in
            // operations are case-sensitive (so ops for tag 'Cat' do not
            // conflate with ops for tag 'cat'
            var exifOpDict = new Dictionary<string, ExifOperation>();

            foreach ( var op in keywordOps )
            {
                var orderedOps = op.OrderBy(x => x.TimeStamp).ToList();

                foreach ( var imageKeywordOp in orderedOps )
                {
                    if ( exifOpDict.TryGetValue(imageKeywordOp.Text, out var existing) )
                        // Update the state before it's replaced in the dict.
                        discardedOps.Add(existing);

                    // Store the most recent op for each operation,
                    // over-writing the previous
                    exifOpDict[imageKeywordOp.Text] = imageKeywordOp;
                }
            }

            // By now we've got a dictionary of keywords/operations. Now we just
            // add them into the final result.
            result[theImage] = exifOpDict.Values.ToList();
        }

        // Now the Faces. Group by image + list of ops
        var imageFaces = opsToProcess.Where(x => x.Type == ExifOperation.ExifType.Face)
            .GroupBy(x => x.Image)
            .Select(x => new { x.Key.ImageId, Ops = x.ToList() })
            .ToList();

        // Now collect up the face updates. We don't discard any of these
        foreach ( var pair in imageFaces )
            if ( result.ContainsKey(pair.ImageId) )
                result[pair.ImageId].AddRange(pair.Ops);
            else
                // Add the most recent to the result
                result[pair.ImageId] = pair.Ops;

        if ( opsToProcess.Any() )
        {
            // These items we just want the most recent in the list
            ConflateSingleObjects(opsToProcess, result, discardedOps, ExifOperation.ExifType.Caption);
            ConflateSingleObjects(opsToProcess, result, discardedOps, ExifOperation.ExifType.Description);
            ConflateSingleObjects(opsToProcess, result, discardedOps, ExifOperation.ExifType.Copyright);
            ConflateSingleObjects(opsToProcess, result, discardedOps, ExifOperation.ExifType.Rating);
            ConflateRotation( opsToProcess, result, discardedOps );
        }

        if ( discardedOps.Any() )
        {
            using var scope = _scopeFactory.CreateScope();
            using var db = scope.ServiceProvider.GetService<ImageContext>();

            // Mark the ops as discarded, and save them.
            discardedOps.ForEach(x => x.State = ExifOperation.FileWriteState.Discarded);

            Logging.Log($"Discarding {discardedOps.Count} duplicate EXIF operations.");
            var watch = new Stopwatch("WriteDiscardedExifOps");
            await db.BulkUpdate(db.KeywordOperations, discardedOps);
            watch.Stop();
        }

        return result;
    }

    /// <summary>
    /// If we have multiple rotations, we need to net them out. For example:
    ///    90, 180, -90, 90, -270, 90 == 90
    /// </summary>
    /// <param name="opsToProcess"></param>
    /// <param name="result"></param>
    private void ConflateRotation(List<ExifOperation> opsToProcess, Dictionary<Guid, List<ExifOperation>> result, List<ExifOperation> discardedOps )
    {
        var rotateOps = opsToProcess.Where( x => x.Type == ExifOperation.ExifType.Rotate );

        var opsByImage = rotateOps.GroupBy( x => x.Image );

        var netRotations = opsByImage.Select( x => new { 
                Image = x.Key, 
                Rotation = x.Sum( op => Convert.ToInt16( op.Text ) ) % 360 } );

        foreach( var netRotate in netRotations )
        {
            if( !result.TryGetValue( netRotate.Image.ImageId, out var existingOps ) )
            {
                existingOps = new List<ExifOperation>();
                result[netRotate.Image.ImageId] = existingOps;
            }

            // Save a new Operation, representing the conflated net rotation
            existingOps.Add( new ExifOperation { 
                    ImageId = netRotate.Image.ImageId,
                    Operation = OperationType.Add,
                    Type = ExifType.Rotate,
                    Text = netRotate.Rotation.ToString(),
            } ); 
        }

        // We always discard, since we created a new op to represent the net rotation
        discardedOps.AddRange( rotateOps );
    }

    private static void ConflateSingleObjects(List<ExifOperation> opsToProcess,
        Dictionary<Guid, List<ExifOperation>> result, List<ExifOperation> discardedOps, ExifOperation.ExifType exifType)
    {
        // Now the captions. Group by image + list of ops, sorted newest first, and then the
        // one we want is the most recent.
        var imageCaptions = opsToProcess.Where(x => x.Type == exifType)
            .GroupBy(x => x.Image)
            .Select(x => new { Image = x.Key, NewestFirst = x.OrderByDescending(d => d.TimeStamp) })
            .Select(x => new
            {
                x.Image,
                Newest = x.NewestFirst.Take(1).ToList(),
                Discarded = x.NewestFirst.Skip(1).ToList()
            })
            .ToList();

        // Now collect up the caption updates, and mark the rest as discarded.
        foreach ( var pair in imageCaptions )
        {
            // Add the most recent to the result if it's in the dict, otherwise create a new entry
            if ( result.ContainsKey(pair.Image.ImageId) )
                result[pair.Image.ImageId].AddRange(pair.Newest);
            else
                result[pair.Image.ImageId] = pair.Newest;

            discardedOps.AddRange(pair.Discarded);
        }
    }

    /// <summary>
    ///     Loads the favourite tags from the DB.
    /// </summary>
    /// <returns></returns>
    private async Task LoadFavouriteTagsAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        using var db = scope.ServiceProvider.GetService<ImageContext>();

        // TODO: Clear the tag cache and reload, and get this from the cache
        var faves = await Task.FromResult(db.Tags
            .Where(x => x.Favourite)
            .Distinct()
            .OrderBy(x => x.Keyword)
            .ToList());

        if ( !faves.SequenceEqual(faveTags) )
        {
            faveTags.Clear();
            faveTags.AddRange(faves);

            NotifyFavouritesChanged();
        }
    }

    public class ExifProcess : IProcessJob
    {
        public Guid ImageId { get; set; }
        public List<ExifOperation> ExifOps { get; set; }
        public ExifService Service { get; set; }
        public DateTime ProcessSchedule { get; set; }
        public bool CanProcess => true;
        public string Name => "Writing Metadata";
        public string Description => $"Writing {ExifOps.Count()} Metadata for ID: {ImageId}";
        public JobPriorities Priority => JobPriorities.ExifService;

        public async Task Process()
        {
            await Service.ProcessExifOperations(ImageId, ExifOps);
        }

        public override string ToString()
        {
            return Description;
        }
    }
}
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

namespace Damselfly.Core.Services;

/// <summary>
///     Core indexing service, which is responsible for scanning the folders on
///     disk for images, and to ingest them into the DB with all their extracted
///     metadata, such as size, last modified date, etc., etc.
/// </summary>
public class IndexingService : IProcessJobFactory, IRescanProvider
{
    private readonly ConfigService _configService;
    private readonly ImageCache _imageCache;
    private readonly ImageProcessService _imageProcessService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IStatusService _statusService;
    private readonly FolderWatcherService _watcherService;
    private readonly WorkService _workService;
    private readonly ImageContext db;
    private bool _fullIndexComplete;
    public static string RootFolder { get; set; }
    public static bool EnableIndexing { get; set; } = true;

    public IndexingService( IServiceScopeFactory scopeFactory, IStatusService statusService,
        ImageProcessService imageService, ConfigService config, ImageCache imageCache, 
        FolderWatcherService watcherService, WorkService workService, IConfiguration configuration,
        ImageContext imageContext)
    {
        _scopeFactory = scopeFactory;
        _statusService = statusService;
        _configService = config;
        _imageProcessService = imageService;
        _imageCache = imageCache;
        _workService = workService;
        _watcherService = watcherService;
        RootFolder = configuration["DamselflyConfiguration:SourceDirectory"]!;
        EnableIndexing = configuration["DamselflyConfiguration:EnableIndexing"]! != "true";
        // Slight hack to work around circular dependencies
        _watcherService.LinkIndexingServiceInstance(this);
        db = imageContext;
    }

    

    public JobPriorities Priority => JobPriorities.Indexing;

    public async Task<ICollection<IProcessJob>> GetPendingJobs(int maxCount)
    {
        if ( _fullIndexComplete )
        {
            using var scope = _scopeFactory.CreateScope();

            // Now, see if there's any folders that have a null scan date.
            var folders = await db.Folders.Where(x => x.FolderScanDate == null)
                .OrderBy(x => x.Path)
                .Take(maxCount)
                .ToArrayAsync();

            var jobs = folders.Select(x => new IndexProcess
                {
                    Path = new DirectoryInfo(x.Path),
                    Service = this,
                    Name = "Indexing"
                })
                .ToArray();

            return jobs;
        }
        // We always perform a full index at startup. This checks the
        // state of the folders/images, and also creates the filewatchers

        _fullIndexComplete = true;
        Logging.Log("Performing full index.");

        return new[]
        {
            new IndexProcess
            {
                Path = new DirectoryInfo(RootFolder),
                Service = this,
                Name = "Full Index",
                IsFullIndex = true
            }
        };
    }

    public async Task MarkFolderForScan(Guid folderId)
    {
        await MarkFoldersForScan(new List<Guid> { folderId });
    }

    public async Task MarkAllForScan()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();

            var updated = await db.BatchUpdate(db.Folders, p => p.SetProperty(x => x.FolderScanDate, x => null));

            _statusService.UpdateStatus($"All {updated} folders flagged for re-indexing.");

            _workService.FlagNewJobs(this);
        }
        catch ( Exception ex )
        {
            Logging.LogError($"Exception when marking folder for reindexing: {ex}");
        }
    }

    /// <summary>
    ///     Flags a set of images for reindexing by marking their containing
    ///     folders for rescan.
    /// </summary>
    /// <param name="images"></param>
    /// <returns></returns>
    public async Task MarkImagesForScan(ICollection<Guid> images)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();

            var folders = await db.Images.Where(x => images.Contains(x.ImageId))
                .Select(x => x.Folder.FolderId)
                .ToListAsync();

            await MarkFoldersForScan(folders);
        }
        catch ( Exception ex )
        {
            Logging.LogError($"Exception when marking images for reindexing: {ex}");
        }
    }

    public event Action OnFoldersChanged;

    private bool IsIndexedFolder( DirectoryInfo dir )
    {
        var trashFolder = _configService.Get( ConfigSettings.TrashcanFolderName );

        if( !string.IsNullOrEmpty( trashFolder ) )
        {
            if( dir.Name == trashFolder )
                return false;
        }

        return dir.IsMonitoredFolder();
    }

    private void NotifyFolderChanged()
    {
        Logging.LogVerbose("Folders changed.");

        // TODO - invoke back on dispatcher thread....
        OnFoldersChanged?.Invoke();
    }

    /// <summary>
    ///     Indexes all of the images in a folder, optionally filtering for a last-mod
    ///     threshold and only indexing those images which have changed since that date.
    /// </summary>
    /// <param name="folder"></param>
    /// <param name="parent"></param>
    public async Task IndexFolder(DirectoryInfo folder, Folder parent)
    {
        Folder folderToScan = null;
        var foldersChanged = false;

        Logging.LogVerbose($"Indexing {folder.FullName}...");

        // Get all the sub-folders on the disk, but filter out
        // ones we're not interested in.
        var subFolders = folder.SafeGetSubDirectories()
            .Where(IsIndexedFolder )
            .ToList();

        try
        {
            using var scope = _scopeFactory.CreateScope();

            // Load the existing folder and its images from the DB
            folderToScan = await db.Folders
                .Where(x => x.Path.Equals(folder.FullName))
                .Include(x => x.Images)
                .FirstOrDefaultAsync();

            if ( folderToScan == null )
            {
                Logging.LogVerbose("Scanning new folder: {0}\\{1}", folder.Parent.Name, folder.Name);
                folderToScan = new Folder { Path = folder.FullName };
            }
            else
            {
                Logging.LogVerbose("Scanning existing folder: {0}\\{1} ({2} images in DB)", folder.Parent.Name,
                    folder.Name, folderToScan.Images.Count());
            }

            if ( folderToScan.FolderId == Guid.Empty )
            {
                Logging.Log($"Adding new folder: {folderToScan.Path}");

                if ( parent != null )
                    folderToScan.ParentId = parent.FolderId;
                // New folder, add it. 
                db.Folders.Add(folderToScan);
                await db.SaveChangesAsync("AddFolders");
                foldersChanged = true;
            }

            // Now, check for missing folders, and clean up if appropriate.
            foldersChanged = await RemoveMissingChildDirs(db, folderToScan) || foldersChanged;

            _watcherService.CreateFileWatcher(folder);

            // Now scan the images. If there's changes it could mean the folder
            // should now be included in the folderlist, so flag it.
            await ScanFolderImages(folderToScan.FolderId);
        }
        catch ( Exception ex )
        {
            Logging.LogError($"Unexpected exception scanning folder {folderToScan.Name}: {ex.Message}");
            if ( ex.InnerException != null )
                Logging.LogError($" Inner exception: {ex.InnerException.Message}");
        }

        // Scan subdirs recursively.
        foreach ( var sub in subFolders ) 
            await IndexFolder(sub, folderToScan);
    }

    /// <summary>
    ///     Checks the folder, and any recursive children, to ensure it still exists
    ///     on the disk. If it doesn't, removes the child folders from the databas.
    /// </summary>
    /// <param name="db"></param>
    /// <param name="folderToScan"></param>
    /// <returns>True if any folders were updated/changed</returns>
    private async Task<bool> RemoveMissingChildDirs(ImageContext db, Folder folderToScan)
    {
        var foldersChanged = false;

        try
        {
            // Now query the DB for child folders of our current folder
            var dbChildDirs = db.Folders.Where(x => x.ParentId == folderToScan.FolderId).ToList();

            foreach ( var childFolder in dbChildDirs )
                // Depth-first removal of child folders
                foldersChanged = await RemoveMissingChildDirs(db, childFolder);

            // ...and then look for any DB folders that aren't included in the list of sub-folders.
            // That means they've been removed from the disk, and should be removed from the DB.
            var missingDirs = dbChildDirs.Where(f => !IsIndexedFolder( new DirectoryInfo(f.Path))).ToList();

            if ( missingDirs.Any() )
            {
                missingDirs.ForEach(x =>
                {
                    Logging.LogVerbose("Deleting folder {0}", x.Path);
                    _watcherService.RemoveFileWatcher(x.Path);
                });

                db.RemoveRange(missingDirs);

                Logging.Log("Removing {0} deleted folders...", missingDirs.Count());
                // Don't use bulk delete; we want EFCore to remove the linked images
                await db.SaveChangesAsync("DeleteFolders");
                foldersChanged = true;
            }
        }
        catch ( Exception ex )
        {
            Logging.LogError($"Unexpected exception scanning for removed folders {folderToScan.Name}: {ex.Message}");
            if ( ex.InnerException != null )
                Logging.LogError($" Inner exception: {ex.InnerException.Message}");
        }

        return foldersChanged;
    }

    /// <summary>
    ///     For a given folder, scans the disk to find all the images in that folder,
    ///     and then indexes all of those images for metadata etc. Optionally takes
    ///     a last-mod threshold which, if set, will mean that only images changed
    ///     since that date will be processed.
    /// </summary>
    /// <param name="folderToScan"></param>
    /// <param name="force">Force the folder to be scanned</param>
    /// <returns></returns>
    private async Task<bool> ScanFolderImages(Guid folderIdToScan)
    {
        var imagesWereAddedOrRemoved = false;
        var folderImageCount = 0;

        using var scope = _scopeFactory.CreateScope();

        // Get the folder with the image list from the DB. 
        var dbFolder = await db.Folders.Where(x => x.FolderId == folderIdToScan)
            .Include(x => x.Images)
            .FirstOrDefaultAsync();

        // Get the list of files from disk
        var folder = new DirectoryInfo(dbFolder.Path);
        var allImageFiles = SafeGetImageFiles(folder);

        if ( allImageFiles == null )
            // Null here means we weren't able to read the contents of the directory.
            // So bail, and give up on this folder altogether.
            return false;

        // First, see if images have been added or removed since we last indexed,
        // by comparing the list of known image filenames with what's on disk.
        // If they're different, we disregard the last scan date of the folder and
        // force the update. 
        var fileListIsEqual =
            allImageFiles.Select(x => x.Name).ArePermutations(dbFolder.Images.Select(y => y.FileName));

        if ( fileListIsEqual && dbFolder.FolderScanDate != null )
            // Number of images is the same, and the folder has a scan date
            // which implies it's been scanned previously, so nothing to do.
            return true;

        Logging.LogVerbose($"New or removed images found in folder {dbFolder.Name}.");

        var watch = new Stopwatch("ScanFolderFiles");

        // Select just imagefiles, and most-recent first
        folderImageCount = allImageFiles.Count();

        int newImages = 0, updatedImages = 0;
        foreach ( var file in allImageFiles )
            try
            {
                var dbImage = dbFolder.Images.FirstOrDefault(x =>
                    x.FileName.Equals(file.Name, StringComparison.OrdinalIgnoreCase));

                if ( dbImage != null )
                {
                    // See if the image has changed since we last indexed it
                    var fileChanged = file.FileIsMoreRecentThan(dbImage.LastUpdated);

                    if ( !fileChanged )
                    {
                        // File hasn't changed. Look for a sidecar to see if it's been modified.
                        var sidecar = dbImage.GetSideCar();

                        if ( sidecar != null )
                            // If there's a sidecar, see if that's changed.
                            fileChanged = sidecar.Filename.FileIsMoreRecentThan(dbImage.LastUpdated);
                    }

                    if ( !fileChanged )
                    {
                        Logging.LogTrace($"Indexed image {dbImage.FileName} unchanged - skipping.");
                        continue;
                    }
                }

                var image = dbImage;

                if ( image == null ) image = new Image { FileName = file.Name };

                // Store some info about the disk file
                image.FileSizeBytes = (int)file.Length;
                image.FileCreationDate = file.CreationTimeUtc;
                image.FileLastModDate = file.LastWriteTimeUtc;

                image.Folder = dbFolder;
                image.FlagForMetadataUpdate();

                if ( dbImage == null )
                {
                    // Default the sort date to file creation date. It'll get updated
                    // later during indexing to set it to the date-taken date, if one
                    // exists.
                    image.SortDate = image.FileCreationDate.ToUniversalTime();

                    Logging.LogTrace("Adding new image {0}", image.FileName);
                    dbFolder.Images.Add(image);
                    newImages++;
                    imagesWereAddedOrRemoved = true;
                }
                else
                {
                    db.Images.Update(image);
                    updatedImages++;

                    // Changed, so throw it out of the cache
                    _imageCache.Evict(image.ImageId);
                }
            }
            catch ( Exception ex )
            {
                Logging.LogError($"Exception while scanning for new image {file}: {ex.Message}");
            }

        // Now look for files to remove.
        // TODO - Sanity check that these don't hit the DB
        var filesToRemove = dbFolder.Images.Select(x => x.FileName).Except(allImageFiles.Select(x => x.Name));
        var dbImages = dbFolder.Images.Select(x => x.FileName);
        var imagesToDelete = dbFolder.Images
            .Where(x => filesToRemove.Contains(x.FileName))
            .ToList();

        if ( imagesToDelete.Any() )
        {
            imagesToDelete.ForEach(x => Logging.LogVerbose("Deleting image {0} (ID: {1})", x.FileName, x.ImageId));

            // Removing these will remove the associated ImageTag and selection references.
            db.Images.RemoveRange(imagesToDelete);
            imagesToDelete.ForEach(x => _imageCache.Evict(x.ImageId));
            imagesWereAddedOrRemoved = true;
        }

        // Flag/update the folder to say we've processed it
        dbFolder.FolderScanDate = DateTime.UtcNow;
        dbFolder.Images.Clear();
        db.Folders.Update(dbFolder);

        await db.SaveChangesAsync("FolderScan");

        watch.Stop();

        _statusService.UpdateStatus(
            $"Indexed folder {dbFolder.Name}: processed {dbFolder.Images.Count()} images ({newImages} new, {updatedImages} updated, {imagesToDelete.Count} removed) in {watch.HumanElapsedTime}.");

        // Do this after we scan for images, because we only load folders if they have images.
        if ( imagesWereAddedOrRemoved )
            NotifyFolderChanged();

        if ( imagesWereAddedOrRemoved || updatedImages > 0 )
        {
            // Should flag the metadata service here...
        }


        return imagesWereAddedOrRemoved;
    }

    /// <summary>
    ///     Marks the FolderScanDate as null, which will cause the
    ///     indexing service to pick it up and scan it for any changes.
    /// </summary>
    /// <param name="folders"></param>
    /// <returns></returns>
    public async Task MarkFoldersForScan(List<Guid> folderIds)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();

            var queryable = db.Folders.Where(f => folderIds.Contains(f.FolderId));
            await db.BatchUpdate(queryable, p => p.SetProperty(x => x.FolderScanDate, x => null));

            if ( folderIds.Count() == 1 )
                _statusService.UpdateStatus("Folder flagged for re-indexing.");
            else
                _statusService.UpdateStatus($"{folderIds.Count()} folders flagged for re-indexing.");

            _workService.FlagNewJobs(this);
        }
        catch ( Exception ex )
        {
            Logging.LogError($"Exception when marking folder for reindexing: {ex}");
        }
    }

    /// <summary>
    ///     Get all image files in a subfolder, and return them, ordered by
    ///     the most recently updated first.
    /// </summary>
    /// <param name="folder"></param>
    /// <returns></returns>
    public List<FileInfo> SafeGetImageFiles(DirectoryInfo folder)
    {
        var watch = new Stopwatch("GetFiles");

        try
        {
            var files = folder.GetFiles()
                .Where(x => _imageProcessService.IsImageFileType(x))
                .OrderByDescending(x => x.LastWriteTimeUtc)
                .ThenByDescending(x => x.CreationTimeUtc)
                .ToList();

            return files;
        }
        catch ( Exception ex )
        {
            Logging.LogWarning("Unable to read files from {0}: {1}", folder.FullName, ex.Message);
            return new List<FileInfo>();
        }
        finally
        {
            watch.Stop();
        }
    }

    public void StartService()
    {
        if ( EnableIndexing )
            _workService.AddJobSource(this);
        else
            Logging.Log("Indexing has been disabled.");
    }

    public class IndexProcess : IProcessJob
    {
        public bool IsFullIndex { get; set; }
        public DirectoryInfo Path { get; set; }
        public IndexingService Service { get; set; }
        public bool CanProcess => true;
        public string Name { get; set; }
        public string Description => $"{Name} {Path}";
        public JobPriorities Priority => IsFullIndex ? JobPriorities.FullIndexing : JobPriorities.Indexing;

        public async Task Process()
        {
            await Service.IndexFolder(Path, null);

            if ( IsFullIndex )
                Logging.Log("Full index compelete.");
        }

        public override string ToString()
        {
            return Description;
        }
    }
}
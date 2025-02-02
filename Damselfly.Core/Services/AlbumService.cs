using AutoMapper;
using Damselfly.Core.Database;
using Damselfly.Core.DbModels.Models.API_Models;
using Damselfly.Core.DbModels.Models.APIModels;
using Damselfly.Core.DbModels.Models.Entities;
using Damselfly.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Damselfly.Core.DbModels.Authentication;
using Damselfly.Core.ScopedServices.Interfaces;
using Damselfly.Core.DbModels.Models.Enums;
using Microsoft.Extensions.Logging;
using System.Security.AccessControl;

namespace Damselfly.Core.Services
{
    public class AlbumService(IMapper mapper, 
        ImageContext imageContext, 
        FileService fileService, 
        IConfiguration configuration, 
        IndexingService indexingService, 
        IAuthService authService,
        ImageService imageService,
        EmailMailGunService emailService,
        NotificationService notificationService,
        ILogger<AlbumService> logger)
    {
        private readonly IMapper _mapper = mapper;
        private readonly ImageContext _context = imageContext;
        private readonly FileService _fileService = fileService;
        private readonly IConfiguration _configuration = configuration;
        private readonly IndexingService _indexingService = indexingService;
        private readonly IAuthService _authService = authService;
        private readonly ImageService _imageService = imageService;
        private readonly EmailMailGunService _emailService = emailService;
        private readonly NotificationService _notificationService = notificationService;
        private readonly ILogger<AlbumService> _logger = logger;

        public async Task<AlbumModel> CreateAlbum(AlbumModel albumModel)
        {
            var album = _mapper.Map<Album>(albumModel);
            var root = _configuration["DamselflyConfiguration:SourceDirectory"];
            var folderPath = Path.Combine(root, album.UrlName);
            var parentFolder = await _context.Folders.FirstOrDefaultAsync(f => f.ParentId == null);
            var folder = new Folder { Path = folderPath, ParentId = parentFolder!.FolderId };
            var newDirectory = Directory.CreateDirectory(folderPath);
            await UpdateDirectoryPermissions(newDirectory);
            album.Folder = folder;
            _context.Folders.Add(folder);
            _context.Albums.Add(album);
            await _context.SaveChangesAsync();
            await _indexingService.IndexFolder(newDirectory, parentFolder);
            return _mapper.Map<AlbumModel>(album);
        }

        public async Task<BooleanResultModel> AddImagesToAlbum(AddExistingImagesToAlbumRequest request)
        {
            var album = await _context.Albums.FirstOrDefaultAsync(a => a.AlbumId == request.AlbumId);
            if (album == null) return new BooleanResultModel { Result = false };
            var images = await _context.Images.Where(i => request.ImageIds.Contains(i.ImageId) && !i.AlbumImages.Any(a => a.AlbumId == album.AlbumId))
                .ToListAsync();
            var albumImages = images.Select(i => new AlbumImage { AlbumId = album.AlbumId, ImageId = i.ImageId }).ToList();
            album.AlbumImages.AddRange(albumImages);
            _context.Update(album);
            await _context.SaveChangesAsync();
            return new BooleanResultModel { Result = true };
        }

        public async Task<AlbumModel> UpdateAlbum(AlbumModel albumModel )
        {
            albumModel.Images.Clear();
            var album = await _context.Albums.Where(a => a.AlbumId == albumModel.AlbumId).FirstOrDefaultAsync();
            if(album == null) return null;
            _mapper.Map(albumModel, album);
            _context.Update(album);
            await _context.SaveChangesAsync();
            return _mapper.Map<AlbumModel>(album);
        }

        public async Task<bool> DeleteAlbum(Guid id) 
        {
            var affectedImages = _context.Images.Include(i => i.AlbumImages).Where(i => i.AlbumImages.Any(a => a.AlbumId == id));
            var deleteImages = affectedImages.Where(i => i.AlbumImages.Count == 1).ToList(); 
            var updateImages = affectedImages.Where(i => i.AlbumImages.Count > 1).ToList();
            foreach(var image in updateImages)
            {                 
                image.AlbumImages.Remove(image.AlbumImages.First(a => a.AlbumId == id));
            }
            var deleteResult = await _fileService.DeleteImages(new MultiImageRequest { ImageIDs = deleteImages.Select(i => i.ImageId).ToList() });
            if(deleteResult || deleteImages.Count == 0)
            {
                _context.Albums.Remove(_context.Albums.First(a => a.AlbumId == id));
                await _context.SaveChangesAsync();
                return true;
            }
            return false;
        }

        public async Task<AlbumModel?> GetAlbum(Guid id, string? password)
        {
            var album = await AlbumWithImagesQuery().FirstOrDefaultAsync(a => a.AlbumId == id);
            if (album == null) return null;
            album = await CheckPassword(album, password);
            return _mapper.Map<AlbumModel>(album);
        }

        public async Task<AlbumModel?> GetByName(string urlName, string? password)
        {
            var album = await AlbumWithImagesQuery().FirstOrDefaultAsync(a => a.UrlName == urlName);
            if( album == null ) return null;
            album = await CheckPassword(album, password);
            return _mapper.Map<AlbumModel>(album);
        }

        public async Task<List<AlbumModel>> GetAlbums()
        {
            var albums = await _context.Albums.ToListAsync();
            return _mapper.Map<List<AlbumModel>>(albums);
        }

        public async Task<AlbumModel> UnlockAlbum(Guid id)
        {
            var album = await _context.Albums.FirstOrDefaultAsync(a => a.AlbumId == id);
            if( album == null ) throw new Exception("Album not found");
            album.InvalidPasswordAttempts = 0;
            _context.Update(album);
            await _context.SaveChangesAsync();
            return _mapper.Map<AlbumModel>(album);
        }


        public async Task<bool> ScanAlbum(Guid albumId)
        {
            try
            {
                var album = await _context.Albums.Include(a => a.Folder).FirstOrDefaultAsync(a => a.AlbumId == albumId);
                if( album == null ) return false;
                var images = Directory.GetFiles(album.Folder.Path, "*.*", SearchOption.AllDirectories)
                    .Where(s => s.EndsWith(".jpg") || s.EndsWith(".jpeg") || s.EndsWith(".png") || s.EndsWith(".gif") || s.EndsWith(".bmp"))
                    .Select(s => new FileInfo(s)).ToList();
                var results = new List<ImageScanResultEnum>();
                foreach( var image in images )
                {
                    var result = await _imageService.ProcessAlbumImageFromFolder(image, album);
                    results.Add(result);
                }
                var adminEmail = _configuration["ContactForm:ToAddress"];
                var stringBuilder = new StringBuilder();
                stringBuilder.AppendLine($"<p>Results for scan of {album.Name}</p>");
                var failedCount = results.Count(r => r == ImageScanResultEnum.Error);
                var successCount = results.Count(r => r == ImageScanResultEnum.New);
                var existingCount = results.Count(r => r == ImageScanResultEnum.Preexisting);
                stringBuilder.AppendLine($"<p>Of the {images.Count} found in the folder</p>");
                stringBuilder.AppendLine($"<p>Failed: {failedCount}</p>");
                stringBuilder.AppendLine($"<p>Success: {successCount}</p>");
                stringBuilder.AppendLine($"<p>Existing: {existingCount}</p>");

                await _emailService.SendEmailAsync(adminEmail, $"Results of {album.Name} scan", stringBuilder.ToString());
                await _notificationService.SendNotification("Album scan complete", $"Check your email for full scan results of {album.Name}");
                return true;
            }
            catch(Exception ex)
            {
                _logger.LogError(ex, $"Error scanning album {albumId}");
                await _notificationService.SendNotification("Album scan error", $"Error scanning album {albumId}");
                return false;
            }
        }   

        public static bool QueueAlbumScan(Guid albumId)
        {
            Hangfire.BackgroundJob.Enqueue<AlbumService>(x => x.ScanAlbum(albumId));
            return true;
        }

        private async Task<Album> CheckPassword(Album album, string? password)
        {
           

            if (album.IsPublic || album.Password == null) return album;
            if (album.InvalidPasswordAttempts > Album.MaxInvalidPasswordAttempts ) return null;
            if (album.Password == password)
            {
                await _context.Albums
                    .Where(x => x.AlbumId == album.AlbumId)
                    .ExecuteUpdateAsync(a => a.SetProperty(x => x.InvalidPasswordAttempts, 0));
                return album;
            }
            var isAdmin = await _authService.CheckCurrentFirebaseUserIsInRole([RoleDefinitions.s_AdminRole]);
            if( isAdmin )
            {
                return album;
            }
            album.InvalidPasswordAttempts++;
            await _context.Albums
                .Where(x => x.AlbumId == album.AlbumId)
                .ExecuteUpdateAsync(a => a.SetProperty(x => x.InvalidPasswordAttempts, album.InvalidPasswordAttempts));
            return null;
        }

        private IQueryable<Album> AlbumWithImagesQuery()
        {
            return _context.Albums.Include(a => a.AlbumImages).ThenInclude(ai => ai.Image).ThenInclude(i => i.MetaData);
        }

        private static async Task UpdateDirectoryPermissions(DirectoryInfo directory)
        {
            if( OperatingSystem.IsLinux() )
            {
                var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "chmod",
                        Arguments = "766 " + directory.FullName,
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    }
                };
                process.Start();
                await process.WaitForExitAsync();
                return;
            }
            if( OperatingSystem.IsWindows() )
            {
                directory.SetAccessControl(new DirectorySecurity(directory.FullName, AccessControlSections.All));
                return;
            }
            throw new NotImplementedException("Unable to detect operating system");
        }
    }
}

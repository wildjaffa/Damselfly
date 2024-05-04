using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Damselfly.Core.Constants;
using Damselfly.Core.Database;
using Damselfly.Core.DbModels;
using Damselfly.Core.Models;
using Damselfly.Core.ScopedServices.Interfaces;
using Damselfly.Core.Utils;
using Damselfly.Shared.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Damselfly.Core.Services;

public class SearchQueryService
{
    private readonly IConfigService _configService;
    private readonly ImageCache _imageCache;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ImageContext db;

    public SearchQueryService(IServiceScopeFactory scopeFactory, ImageCache cache,
        IConfigService configService, ImageContext imageContext)
    {
        _scopeFactory = scopeFactory;
        _configService = configService;
        _imageCache = cache;
        db = imageContext;
    }

    /// <summary>
    ///     Escape out characters like apostrophes
    /// </summary>
    /// <param name="searchText"></param>
    /// <returns></returns>
    private static string EscapeChars(string searchText)
    {
        return searchText.Replace("'", "''");
    }

    /// <summary>
    ///     The actual search query. Given a page (first+count) we run the search query on the DB
    ///     and return back a set of data into the SearchResults collection. Since search parameters
    ///     are all AND based, and additive, we build up the query depending on whether the user
    ///     has specified a folder, a search text, a date range, etc, etc.
    ///     TODO: Add support for searching by Lens ID, Camera ID, etc.
    /// </summary>
    /// <param name="first"></param>
    /// <param name="count"></param>
    /// <returns>True if there's more data available for the requested range</returns>
    private async Task<SearchResponse> LoadMoreData(SearchQuery query, int first, int count)
    {
        // Assume there is more data available - unless the search
        // returns less than we asked for (see below).
        var response = new SearchResponse { MoreDataAvailable = true, SearchResults = new Guid[0] };

        using var scope = _scopeFactory.CreateScope();

        var watch = new Stopwatch("ImagesLoadData");
        var results = new List<Guid>();
        
        Image similarImage = null;
        //int similarId = 0;

        //if (query.SimilarToId != null)
        //{
        //    similarId = query.SimilarToId.Value;
        //    similarImage = await _imageCache.GetCachedImage(similarId);
        //}

        try
        {
            Logging.LogTrace("Loading images from {0} to {1} - Query: {2}", first, first + count, query);

            var hasTextSearch = !string.IsNullOrEmpty(query.SearchText);

            // Default is everything.
            var images = db.Images.AsQueryable();

            if ( hasTextSearch )
            {
                var searchText = EscapeChars(query.SearchText);
                // If we have search text, then hit the fulltext Search.
                images = await db.ImageSearch(searchText, query.IncludeAITags);
            }

            // TODO: UI should make these two mutually exclusive
            if (query.UntaggedImages)
            {
                images = images.Where(x => !x.ImageTags.Any() && ! x.ImageObjects.Any());
            }
            else if ( query.Tag != null )
            {
                var tagImages = images.Where(x => x.ImageTags.Any(y => y.TagId == query.Tag.TagId));
                var objImages = images.Where(x => x.ImageObjects.Any(y => y.TagId == query.Tag.TagId));

                images = tagImages.Union(objImages);
            }

            if (similarImage != null && similarImage.Hash != null)
            {
                var similarHash = similarImage.Hash;

                if (similarHash.HasPerceptualHash())
                {
                    var hash1A = $"{similarHash.PerceptualHex1.Substring(0, 2)}%";
                    var hash1B = $"%{similarHash.PerceptualHex1.Substring(2, 2)}";
                    var hash2A = $"{similarHash.PerceptualHex2.Substring(0, 2)}%";
                    var hash2B = $"%{similarHash.PerceptualHex2.Substring(2, 2)}";
                    var hash3A = $"{similarHash.PerceptualHex3.Substring(0, 2)}%";
                    var hash3B = $"%{similarHash.PerceptualHex3.Substring(2, 2)}";
                    var hash4A = $"{similarHash.PerceptualHex4.Substring(0, 2)}%";
                    var hash4B = $"%{similarHash.PerceptualHex4.Substring(2, 2)}";

                    images = images.Where(x =>
                                            (
                                                EF.Functions.Like(x.Hash.PerceptualHex1, hash1A) ||
                                                EF.Functions.Like(x.Hash.PerceptualHex1, hash1B) ||
                                                EF.Functions.Like(x.Hash.PerceptualHex2, hash2A) ||
                                                EF.Functions.Like(x.Hash.PerceptualHex2, hash2B) ||
                                                EF.Functions.Like(x.Hash.PerceptualHex3, hash3A) ||
                                                EF.Functions.Like(x.Hash.PerceptualHex3, hash3B) ||
                                                EF.Functions.Like(x.Hash.PerceptualHex4, hash4A) ||
                                                EF.Functions.Like(x.Hash.PerceptualHex4, hash4B)
                                            ));
                }
            }

            // If selected, filter by the image filename/foldername
            if ( hasTextSearch && !query.TagsOnly )
            {
                // TODO: Make this like more efficient. Toggle filename/path search? Or just add filename into FTS?
                var likeTerm = $"%{query.SearchText}%";

                // Now, search folder/filenames
                var fileImages = db.Images.Where(x => EF.Functions.Like(x.Folder.Path, likeTerm)
                                                      || EF.Functions.Like(x.FileName, likeTerm));
                images = images.Union(fileImages);
            }

            if ( query.Person?.PersonId != null )
                // Filter by personID
                images = images.Where(x => x.ImageObjects.Any(p => p.PersonId == query.Person.PersonId));

            if ( query.Folder?.FolderId != null )
            {
                IEnumerable<Folder> descendants;

                if( query.IncludeChildFolders )
                {
                    descendants = await db.GetChildFolderIds( db.Folders, query.Folder.FolderId );
                }
                else
                {
                    descendants = query.Folder.Subfolders.ToList();
                }

                // Filter by folderID
                images = images.Where(x => descendants.Select(x => x.FolderId).Contains(x.FolderId));
            }

            if ( query.MinDate.HasValue || query.MaxDate.HasValue )
            {
                var minDate = query.MinDate.HasValue ? query.MinDate.Value : DateTime.MinValue;
                // Ensure the end date is always inclusive, so set the time to 23:59:59
                var maxDate = query.MaxDate.HasValue ? query.MaxDate.Value.AddDays(1).AddSeconds(-1) : DateTime.MaxValue;
                
                // Always filter by date - because if there's no filter
                // set then they'll be set to min/max date.
                images = images.Where(x => x.SortDate >= minDate &&
                                           x.SortDate <= maxDate);
            }

            if ( query.MinRating.HasValue )
                // Filter by Minimum rating
                images = images.Where(x => x.MetaData.Rating >= query.MinRating);

            if ( query.Month.HasValue )
                // Filter by month
                images = images.Where(x => x.SortDate.Month == query.Month);

            if ( query.MinSizeKB.HasValue )
            {
                var minSizeBytes = query.MinSizeKB.Value * 1024;
                images = images.Where(x => x.FileSizeBytes > minSizeBytes);
            }

            if ( query.MaxSizeKB.HasValue )
            {
                var maxSizeBytes = query.MaxSizeKB.Value * 1024;
                images = images.Where(x => x.FileSizeBytes < maxSizeBytes);
            }

            if ( query.Orientation.HasValue )
            {
                if ( query.Orientation == OrientationType.Panorama )
                    images = images.Where(x => x.MetaData.AspectRatio > 2);
                else if ( query.Orientation == OrientationType.Landscape )
                    images = images.Where(x => x.MetaData.AspectRatio > 1);
                else if ( query.Orientation == OrientationType.Portrait )
                    images = images.Where(x => x.MetaData.AspectRatio < 1);
                else if ( query.Orientation == OrientationType.Square )
                    images = images.Where(x => x.MetaData.AspectRatio == 1);
            }

            if ( query.CameraId.HasValue )
                images = images.Where(x => x.MetaData.CameraId == query.CameraId);

            if ( query.LensId.HasValue )
                images = images.Where(x => x.MetaData.LensId == query.LensId);

            if ( query.FaceSearch.HasValue )
                images = query.FaceSearch switch
                {
                    FaceSearchType.Faces => images.Where(x =>
                        x.ImageObjects.Any(x => x.Type == ImageObject.ObjectTypes.Face.ToString())),
                    FaceSearchType.NoFaces => images.Where(x =>
                        !x.ImageObjects.Any(x => x.Type == ImageObject.ObjectTypes.Face.ToString())),
                    FaceSearchType.UnidentifiedFaces => images.Where(x =>
                        x.ImageObjects.Any(x => x.Person.State == Person.PersonState.Unknown)),
                    FaceSearchType.IdentifiedFaces => images.Where(x =>
                        x.ImageObjects.Any(x => x.Person.State == Person.PersonState.Identified)),
                    _ => images
                };

            // Add in the ordering for the group by
            switch ( query.Grouping )
            {
                case GroupingType.None:
                case GroupingType.Date:
                    images = query.SortOrder == SortOrderType.Descending
                        ? images.OrderByDescending(x => x.SortDate)
                        : images.OrderBy(x => x.SortDate);
                    break;
                case GroupingType.Folder:
                    images = query.SortOrder == SortOrderType.Descending
                        ? images.OrderBy(x => x.Folder.Path).ThenByDescending(x => x.SortDate)
                        : images.OrderByDescending(x => x.Folder.Path).ThenBy(x => x.SortDate);
                    break;
                default:
                    throw new ArgumentException("Unexpected grouping type.");
            }

            results = await images
                .Select(x => x.ImageId)
                .Skip(first)
                .Take(count)
                .ToListAsync();

            watch.Stop();

            Logging.Log($"Search: {results.Count()} images found in search query within {watch.ElapsedTime}ms");
        }
        catch ( Exception ex )
        {
            Logging.LogError("Search query failed: {0}", ex.Message);
        }
        finally
        {
            watch.Stop();
        }

        if ( results.Count < count )
            // The number of returned IDs is less than we asked for
            // so we must have reached the end of the results.
            response.MoreDataAvailable = false;

        // Now load the tags....
        var enrichedImages = await _imageCache.GetCachedImages(results);

        try
        {
            // If it's a 'similar to' query, filter out the ones that don't pass the threshold.
            if ( query.SimilarToId != null && enrichedImages.Any() )
            {
                var threshold = _configService.GetInt(ConfigSettings.SimilarityThreshold, 75) / 100.0;

                // Complete the hamming distance calculation here:
                var searchHash = similarImage.Hash;

                var similarImages = enrichedImages
                    .Where(x => x.Hash != null && x.Hash.SimilarityTo(searchHash) > threshold).ToList();

                Logging.Log(
                    $"Found {similarImages.Count} of {enrichedImages.Count} prefiltered images that match image ID {query.SimilarToId} with a threshold of {threshold:P1} or more.");

                enrichedImages = similarImages;
            }
        }
        catch ( Exception ex )
        {
            Logging.LogError($"Similarity threshold calculation failed: {ex}");
        }

        // Set the results on the service property
        response.SearchResults = enrichedImages.Select(x => x.ImageId).ToArray();

        return response;
    }

    public async Task<SearchResponse> GetQueryImagesAsync(SearchRequest request)
    {
        var query = new SearchQuery();
        request.Query.CopyPropertiesTo(query);

        using var scope = _scopeFactory.CreateScope();

        // WASM TODO Should make this better
        if ( request.Query.FolderId.HasValue )
            query.Folder = await db.Folders.FirstOrDefaultAsync(x => x.FolderId == request.Query.FolderId.Value);
        if ( request.Query.TagId.HasValue )
            query.Tag = await db.Tags.FirstOrDefaultAsync(x => x.TagId == request.Query.TagId.Value);
        if ( request.Query.PersonId.HasValue )
            query.Person = await db.People.FirstOrDefaultAsync(x => x.PersonId == request.Query.PersonId.Value);

        // Load more data if we need it.
        return await LoadMoreData(query, request.First, request.Count);
    }
}
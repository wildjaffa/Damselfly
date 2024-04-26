using System.Collections.Generic;
using Damselfly.Core.Models;

namespace Damselfly.Core.DbModels.Models.APIModels;

public class DownloadRequest
{
    public ICollection<int> ImageIds { get; set; }
    public ExportConfig Config { get; set; }
    public string? Password { get; set; }
}

public class DownloadResponse
{
    public string DownloadUrl { get; set; }
}
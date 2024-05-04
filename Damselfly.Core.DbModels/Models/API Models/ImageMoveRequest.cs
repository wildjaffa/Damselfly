using System;
using System.Collections.Generic;
using Damselfly.Core.Models;

namespace Damselfly.Core.DbModels.Models.APIModels;

public class MultiImageRequest
{
    public ICollection<Guid> ImageIDs { get; set; }
}

public class ImageMoveRequest : MultiImageRequest
{
    public Folder Destination { get; set; }
    public bool Move { get; set; }
}


using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Humanizer;

namespace Damselfly.Core.Models;

/// <summary>
///     One image can have a number of objects each with a name.
/// </summary>
public class ImageObject
{
    public enum ObjectTypes
    {
        Object = 0,
        Face = 1
    }

    public enum RecognitionType
    {
        Manual = 0,
        Emgu = 1, // Deprecated
        Accord = 2, // Deprecated
        Azure = 3, // Deprecated
        MLNetObject = 4,
        ExternalApp = 5,
        FaceONNX = 6
    }

    [Key]
    
    public Guid ImageObjectId { get; set; } = new Guid();

    [Required] public Guid ImageId { get; set; }

    public virtual Image Image { get; set; }

    [Required] public Guid TagId { get; set; }

    public virtual Tag Tag { get; set; }

    public string? Type { get; set; } = ObjectTypes.Object.ToString();
    public RecognitionType RecogntionSource { get; set; }
    public double Score { get; set; }
    public int RectX { get; set; }
    public int RectY { get; set; }
    public int RectWidth { get; set; }
    public int RectHeight { get; set; }

    public Guid? PersonId { get; set; }
    public virtual Person Person { get; set; }

    public bool IsFace => Type == ObjectTypes.Face.ToString();

    public override string ToString()
    {
        return GetTagName();
    }

    public string GetTagName(bool includeScore = false)
    {
        var ret = "Unidentified Object";

        if ( IsFace )
        {
            if ( Person != null && Person.Name != "Unknown" )
                return $"{Person.Name.Transform(To.TitleCase)}";
            ret = "Unidentified face";
        }
        else if ( Type == ObjectTypes.Object.ToString() && Tag != null )
        {
            ret = $"{Tag.Keyword.Transform(To.SentenceCase)}";
        }

        if ( includeScore && Score > 0 ) ret += $" ({Score:P0})";

        return ret;
    }
}
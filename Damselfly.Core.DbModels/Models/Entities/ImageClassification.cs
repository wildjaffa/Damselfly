using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Damselfly.Core.Models;

/// <summary>
///     An image classification detected via ML
/// </summary>
public class ImageClassification
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)] 
    public int ClassificationId { get; set; }

    public string? Label { get; set; }

    public override string ToString()
    {
        return $"{Label} [{ClassificationId}]";
    }
}
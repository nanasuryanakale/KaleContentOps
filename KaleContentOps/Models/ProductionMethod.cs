using System.ComponentModel.DataAnnotations;

namespace KaleContentOps.Models;

public class ProductionMethod
{
    public int Id { get; set; }

    [Required]
    [MaxLength(50)]
    public string Code { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public ICollection<ContentLog> ContentLogs { get; set; }
        = new List<ContentLog>();
}
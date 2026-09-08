using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace KaleContentOps.Models
{
    public class MasterPic
    {
        public int Id { get; set; }

        [Required]
        [MaxLength(100)]
        public string Name { get; set; } = string.Empty;

        public bool IsActive { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public ICollection<ContentLog> ContentLogs { get; set; }
            = new List<ContentLog>();
    }
}

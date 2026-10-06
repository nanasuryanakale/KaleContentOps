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

        // ------------------------------------------------------------------
        // Phase 2A - Employment period (business calendar dates, Asia/Jakarta).
        // ------------------------------------------------------------------
        // JoinDate: REQUIRED. The first date the PIC officially joined the team.
        // Maps to SQL Server `date`. Legacy rows are backfilled from CreatedAt
        // (the earliest known system record of the PIC) as an explicit
        // approximation - it is NOT asserted to be the authoritative HR join date.
        public DateOnly JoinDate { get; set; }

        // ResignDate: OPTIONAL. The last date the PIC is still considered a
        // team member; NULL means "no known resignation/end date".
        // Maps to SQL Server `date` NULL.
        // It is NEVER derived from UpdatedAt/CreatedAt/current date: those
        // columns describe record maintenance, not business resignation.
        // Lifecycle rules (setting/interpreting ResignDate) belong to Phase 2B.
        public DateOnly? ResignDate { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public ICollection<ContentLog> ContentLogs { get; set; }
            = new List<ContentLog>();
    }
}

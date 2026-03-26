using System.ComponentModel.DataAnnotations;

namespace OmegleCloneMVC.Models
{
    public class AdminActionLog
    {
        public int Id { get; set; }

        public int ActorUserId { get; set; }
        [MaxLength(128)]
        public string ActorEmail { get; set; } = "";

        public int? TargetUserId { get; set; }
        [MaxLength(128)]
        public string? TargetEmail { get; set; }

        [MaxLength(64)]
        public string Action { get; set; } = "";

        [MaxLength(1000)]
        public string? Details { get; set; }

        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    }
}

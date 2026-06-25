using System.ComponentModel.DataAnnotations;

namespace OmegleCloneMVC.Models
{
    public class Report
    {
        public int Id { get; set; }

        [MaxLength(64)]
        public string ReporterIp { get; set; } = "";

        [MaxLength(64)]
        public string ReportedIp { get; set; } = "";

        [MaxLength(64)]
        public string Reason { get; set; } = "";

        [MaxLength(16)]
        public string ChatType { get; set; } = ""; // "video" or "text"

        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    }
}

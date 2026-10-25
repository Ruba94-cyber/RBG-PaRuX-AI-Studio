using System;

namespace PaRuX.Models
{
    public class ChatAttachment
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string FileName { get; set; } = string.Empty;
        public string MediaType { get; set; } = "image/png";
        public string LocalPath { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        /// <summary>Server media job that produced this file; lets reconciliation avoid attaching it twice.</summary>
        public string? MediaJobId { get; set; }
    }
}

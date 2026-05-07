using System.ComponentModel.DataAnnotations;

namespace TorServices.Models;

public class TorrentRecord
{
    [Key]
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? OutputDir { get; set; }
    public string? MagnetUri { get; set; }
    public string? TorrentPath { get; set; }
    public long TotalSize { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? ClientId { get; set; }
    public virtual UserClient? User { get; set; }
    public virtual TorrentProgress? ProgressInfo { get; set; }
}

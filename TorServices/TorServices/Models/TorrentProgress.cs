using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TorServices.Models;

public class TorrentProgress
{
    [Key, ForeignKey("Torrent")]
    public string TorrentId { get; set; } = string.Empty;
    
    public string Status { get; set; } = "Queued";
    public double Progress { get; set; }
    public int TotalPieces { get; set; }
    public int CompletedPieces { get; set; }
    public byte[]? Bitfield { get; set; }
    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;
    public virtual TorrentRecord? Torrent { get; set; }
}

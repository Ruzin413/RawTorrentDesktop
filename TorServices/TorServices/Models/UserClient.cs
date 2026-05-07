using System.ComponentModel.DataAnnotations;

namespace TorServices.Models;

public class UserClient
{
    [Key]
    public string ClientId { get; set; } = string.Empty;
    public DateTime RegisteredAt { get; set; } = DateTime.UtcNow;
    public virtual ICollection<TorrentRecord> Downloads { get; set; } = new List<TorrentRecord>();
}

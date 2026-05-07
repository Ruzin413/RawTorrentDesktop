namespace TorServices.DTOs;

public class TorrentStatus
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public double Progress { get; set; }
    public int ActivePeers { get; set; }
    public string Status { get; set; } = "Initializing";
    public long TotalSize { get; set; }
    public int CompletedPieces { get; set; }
    public int TotalPieces { get; set; }
    public string? ClientId { get; set; }
    public string? OutputDir { get; set; }
}

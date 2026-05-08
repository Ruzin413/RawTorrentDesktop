using System.Collections.Concurrent;
using System.IO;
using TorServices.Core;
using TorServices.Models;
using TorServices.DTOs;
using TorServices.Data;
using TorServices.Network;
using TorServices.DHT;
using TorServices.Parser;


namespace TorServices.Services;

public class TorrentService
{
    private readonly ConcurrentDictionary<string, TorrentController> _controllers = new();
    private readonly List<string> _queueOrder = new();
    private int _maxActiveDownloads = 2;
    public int MaxActiveDownloads 
    { 
        get => _maxActiveDownloads; 
        set 
        { 
            _maxActiveDownloads = value; 
            ProcessQueue(); // Re-process queue when limit increases
        } 
    }
    private readonly object _lock = new();
    private readonly CsvDataStore _store;
    private readonly string _metadataDir;

    public TorrentService(CsvDataStore store)
    {
        _store = store;
        _metadataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RawTorrent", "Metadata");
        Directory.CreateDirectory(_metadataDir);

        LoadFromStore();
        StartBackgroundMonitor();
        UpnpService.ForwardPort(6881, "RawTorrent").FireAndForget("UPnP Port Forwarding");
        
        var listener = new PeerListener(6881, OnIncomingConnection);
        listener.Start();

        var lsd = new LsdService(OnLsdPeerFound);
        lsd.Start();
    }

    private void OnLsdPeerFound(byte[] infoHash, string address)
    {
        var controller = _controllers.Values.FirstOrDefault(c => 
            c.InfoHash != null && c.InfoHash.SequenceEqual(infoHash));
        
        if (controller != null)
        {
            controller.AddDiscoveredPeer(address);
        }
    }

    private async Task OnIncomingConnection(byte[] infoHash, PeerSession session)
    {
        var controller = _controllers.Values.FirstOrDefault(c => 
            c.InfoHash != null && c.InfoHash.SequenceEqual(infoHash));
        
        if (controller != null)
        {
            await controller.HandleIncomingConnection(session);
        }
        else
        {
            session.Dispose();
        }
    }

    private void StartBackgroundMonitor()
    {
        Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(2000);
                var activeIds = _controllers.Keys.ToList();
                foreach (var id in activeIds)
                {
                    if (_controllers.TryGetValue(id, out var controller))
                    {
                        SaveToStore(controller);
                        
                        // If it's completed, remove from active controllers
                        if (controller.Status == "Completed")
                        {
                            _controllers.TryRemove(id, out _);
                            lock (_lock) _queueOrder.Remove(id);
                        }
                    }
                }
            }
        }).FireAndForget("Background Monitor");
    }

    private void LoadFromStore()
    {
        var records = _store.GetAllTorrents();
        var allProgress = _store.GetAllProgress();
        
        foreach (var record in records)
        {
            var progress = allProgress.FirstOrDefault(p => p.TorrentId == record.Id);
            var status = progress?.Status ?? "Stopped";
            if (status != "Completed")
            {
                var controller = new TorrentController 
                { 
                    Name = record.Name,
                    OutputDir = record.OutputDir,
                    MagnetUri = record.MagnetUri,
                    TorrentPath = record.TorrentPath,
                    MetadataCacheDir = _metadataDir,
                    InitialBitfield = progress?.Bitfield,
                    TotalPieces = progress?.TotalPieces ?? 0,
                    Status = "Stopped",
                    ClientId = record.ClientId
                };

                // If it's a magnet link, check if we have metadata cached
                if (string.IsNullOrEmpty(controller.TorrentPath) && !string.IsNullOrEmpty(controller.MagnetUri))
                {
                    string cachePath = Path.Combine(_metadataDir, record.Id.ToLower() + ".torrent");
                    if (File.Exists(cachePath))
                    {
                        controller.TorrentPath = cachePath;
                    }
                }

                controller.SetId(record.Id);
                _controllers[record.Id] = controller;
            }
        }
    }

    public IEnumerable<TorrentStatus> GetAllTorrents(string? clientId = null)
    {
        var controllers = _controllers.Values.AsEnumerable();
        if (!string.IsNullOrEmpty(clientId))
        {
            controllers = controllers.Where(c => c.ClientId == clientId);
        }

        return controllers.Select(c => new TorrentStatus
        {
            Id = c.Id,
            Name = c.Name,
            Progress = c.TotalPieces > 0 ? (double)c.CompletedPieces / c.TotalPieces * 100 : 0,
            ActivePeers = c.ActivePeersCount,
            Status = c.Status,
            TotalSize = c.TotalSize,
            CompletedPieces = c.CompletedPieces,
            TotalPieces = c.TotalPieces,
            ClientId = c.ClientId,
            OutputDir = c.OutputDir
        });
    }

    public List<TorrentStatus> GetHistory(string? clientId = null)
    {
        var records = _store.GetAllTorrents();
        var allProgress = _store.GetAllProgress();

        var query = records
            .Select(r => new { Record = r, Progress = allProgress.FirstOrDefault(p => p.TorrentId == r.Id) })
            .Where(x => x.Progress != null && x.Progress.Status == "Completed");

        if (!string.IsNullOrEmpty(clientId))
        {
            query = query.Where(x => x.Record.ClientId == clientId);
        }

        return query
            .OrderByDescending(x => x.Progress!.LastUpdatedAt)
            .Select(x => new TorrentStatus
            {
                Id = x.Record.Id,
                Name = x.Record.Name,
                Status = x.Progress!.Status,
                Progress = x.Progress.Progress,
                TotalSize = x.Record.TotalSize,
                TotalPieces = x.Progress.TotalPieces,
                CompletedPieces = x.Progress.CompletedPieces,
                ClientId = x.Record.ClientId,
                OutputDir = x.Record.OutputDir
            })
            .ToList();
    }

    public Task<string> StartTorrent(string path, string? outputDir = null, string? clientId = null)
    {
        var controller = new TorrentController { TorrentPath = path, OutputDir = outputDir, ClientId = clientId };
        _controllers[controller.Id] = controller;
        
        SaveToStore(controller);

        lock (_lock)
        {
            _queueOrder.Add(controller.Id);
        }
        
        ProcessQueue();
        Console.WriteLine($"[Service] Added new torrent to queue: {path} (Client: {clientId})");
        return Task.FromResult(controller.Id);
    }

    public Task<string> StartMagnet(string uri, string? outputDir = null, string? clientId = null)
    {
        var controller = new TorrentController { MagnetUri = uri, OutputDir = outputDir, ClientId = clientId, MetadataCacheDir = _metadataDir };
        var magnet = MagnetParser.Parse(uri);
        controller.SetId(BitConverter.ToString(magnet.InfoHash).Replace("-", "").ToLower());

        // Check if metadata already cached
        string cachePath = Path.Combine(_metadataDir, controller.Id.ToLower() + ".torrent");
        if (File.Exists(cachePath))
        {
            controller.TorrentPath = cachePath;
        }

        _controllers[controller.Id] = controller;
        SaveToStore(controller);

        lock (_lock)
        {
            _queueOrder.Add(controller.Id);
        }

        ProcessQueue();
        Console.WriteLine($"[Service] Added new magnet to queue: {uri} (Client: {clientId})");
        return Task.FromResult(controller.Id);
    }

    private void SaveToStore(TorrentController controller)
    {
        var record = _store.FindTorrent(controller.Id);
        if (record == null)
        {
            record = new TorrentRecord
            {
                Id = controller.Id,
                Name = controller.Name,
                OutputDir = controller.OutputDir,
                MagnetUri = controller.MagnetUri,
                TorrentPath = controller.TorrentPath,
                ClientId = controller.ClientId
            };
        }
        else if (controller.Name != "Initializing..." && record.Name == "Initializing...")
        {
            record.Name = controller.Name;
        }

        // Always update TotalSize if the controller has it
        if (controller.TotalSize > 0) record.TotalSize = controller.TotalSize;

        _store.SaveTorrent(record);

        var progress = _store.FindProgress(controller.Id);
        if (progress == null)
        {
            progress = new TorrentProgress
            {
                TorrentId = controller.Id,
            };
        }

        progress.Status = controller.Status;
        progress.TotalPieces = controller.TotalPieces;
        progress.CompletedPieces = controller.CompletedPieces;
        progress.Progress = controller.TotalPieces > 0 ? (double)controller.CompletedPieces / controller.TotalPieces * 100 : 0;
        progress.LastUpdatedAt = DateTime.UtcNow;

        var currentBitfield = controller.GetBitfield();
        if (currentBitfield != null) progress.Bitfield = currentBitfield;
        else if (progress.Bitfield == null) progress.Bitfield = controller.InitialBitfield;

        _store.SaveProgress(progress);
    }


    public async Task<bool> ResumeTorrent(string id)
    {
        if (_controllers.TryGetValue(id, out var controller))
        {
            if (controller.Status == "Stopped" || controller.Status == "Error")
            {
                controller.Status = "Queued";
                lock (_lock)
                {
                    if (!_queueOrder.Contains(id)) _queueOrder.Add(id);
                }
                ProcessQueue();
                return true;
            }
        }
        return false;
    }

    private void ProcessQueue()
    {
        lock (_lock)
        {
            int activeCount = _controllers.Values.Count(c => c.Status == "Downloading" || c.Status == "Starting...");
            
            if (activeCount < MaxActiveDownloads)
            {
                var nextId = _queueOrder.FirstOrDefault(id => 
                    _controllers.TryGetValue(id, out var c) && c.Status == "Queued");

                if (nextId != null && _controllers.TryGetValue(nextId, out var controller))
                {
                    controller.Status = "Starting..."; // Temporary status to avoid double-pick
                    
                    Task.Run(async () => {
                        try {
                            if (!string.IsNullOrEmpty(controller.TorrentPath))
                                await controller.StartDownload(controller.TorrentPath, controller.OutputDir);
                            else if (!string.IsNullOrEmpty(controller.MagnetUri))
                                await controller.StartMagnetDownload(controller.MagnetUri, controller.OutputDir);
                        } catch (Exception ex) {
                            controller.Status = $"Error: {ex.Message}";
                            AppLogger.LogError("DOWNLOAD ERROR", ex, $"Torrent='{controller.Name}'");
                        } finally {
                            ProcessQueue(); // Check for next when this one ends
                        }
                    }).FireAndForget("Torrent Download Loop");

                    // Check if we can start another one immediately
                    Task.Run(() => ProcessQueue());
                }
            }
        }
    }

    public async Task<bool> StopTorrent(string id)
    {
        if (_controllers.TryGetValue(id, out var controller))
        {
            await controller.Stop();
            controller.Status = "Stopped";
            SaveToStore(controller);
            ProcessQueue();
            return true;
        }
        return false;
    }

    public async Task<bool> RemoveTorrent(string id, bool deleteData = false)
    {
        bool found = false;
        string? outputDir = null;
        string? name = null;
        TorrentController? controller = null;

        if (_controllers.TryGetValue(id, out controller))
        {
            await controller.Stop();
            outputDir = controller.OutputDir;
            name = controller.Name;
            found = true;
        }

        var record = _store.FindTorrent(id);
        if (record != null)
        {
            if (outputDir == null) outputDir = record.OutputDir;
            if (name == null) name = record.Name;
            found = true;
        }

        if (deleteData && !string.IsNullOrEmpty(outputDir) && !string.IsNullOrEmpty(name))
        {
            await Task.Run(async () => {
                var targetPath = Path.Combine(outputDir, name);
                Console.WriteLine($"[Service] Attempting to delete data at: {targetPath}");

                for (int i = 0; i < 15; i++) // Increased to 15 retries
                {
                    try
                    {
                        // Help OS release file handles from Memory Mapped Files
                        if (i > 0) 
                        {
                            GC.Collect();
                            GC.WaitForPendingFinalizers();
                            await Task.Delay(1000); // Wait 1s between retries after the first one
                        }

                        if (Directory.Exists(targetPath)) {
                            Directory.Delete(targetPath, true);
                            Console.WriteLine($"[Service] Successfully deleted directory: {targetPath}");
                            break; 
                        }
                        else if (File.Exists(targetPath)) {
                            File.Delete(targetPath);
                            Console.WriteLine($"[Service] Successfully deleted file: {targetPath}");
                            break;
                        }
                        else {
                            break; // Already gone
                        }
                    }
                    catch (Exception ex) { 
                        Console.WriteLine($"[Service] Delete attempt {i+1} failed: {ex.Message}");
                    } 
                }
            });
        }

        if (controller != null)
        {
            _controllers.TryRemove(id, out _);
            lock (_lock)
            {
                _queueOrder.Remove(id);
            }
            ProcessQueue();
        }

        if (record != null)
        {
            _store.RemoveTorrent(id);
            _store.RemoveProgress(id);
        }

        return found;
    }

    public async Task ClearAllData(string? clientId = null)
    {
        var idsToRemove = _controllers
            .Where(kvp => string.IsNullOrEmpty(clientId) || kvp.Value.ClientId == clientId)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var id in idsToRemove)
        {
            if (_controllers.TryRemove(id, out var controller))
            {
                await controller.Stop();
                lock (_lock) _queueOrder.Remove(id);
            }
        }

        if (string.IsNullOrEmpty(clientId))
        {
            _store.ClearAll();
        }
        else
        {
            var records = _store.GetAllTorrents().Where(r => r.ClientId == clientId).ToList();
            foreach (var r in records)
            {
                _store.RemoveTorrent(r.Id);
                _store.RemoveProgress(r.Id);
            }
        }


    }

    public async Task<List<string>> DiscoverPeersForMetadata(byte[] infoHash, List<string> trackers, string peerId)
    {
        var discoveredPeers = new ConcurrentBag<string>();
        var trackerClient = new TrackerClient();
        var dhtClient = new DhtClient();
        
        var tasks = new List<Task>();
        
        foreach (var t in trackers)
        {
            tasks.Add(Task.Run(async () => {
                try {
                    var ps = await trackerClient.GetPeers(t, infoHash, 0, peerId);
                    foreach (var p in ps) discoveredPeers.Add(p);
                } catch { }
            }));
        }
        
        tasks.Add(Task.Run(async () => {
            try {
                var ps = await dhtClient.GetPeersAsync(infoHash);
                foreach (var p in ps) discoveredPeers.Add(p);
            } catch { }
        }));
        
        await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(5000));
        
        return discoveredPeers.Distinct().Take(10).ToList();
    }
}

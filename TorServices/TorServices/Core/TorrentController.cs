using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using TorServices.Core;
using TorServices.Network;
using TorServices.Parser;
using TorServices.DHT;

namespace TorServices.Core;

public class TorrentController
{
    private readonly TrackerClient _tracker = new();
    private readonly DhtClient _dht = new();
    private readonly ConcurrentQueue<string> _peerDiscoveryQueue = new();
    private readonly ConcurrentDictionary<string, bool> _triedPeers = new();
    private readonly List<PeerSession> _activeSessions = new();
    private readonly List<PeerSession> _unchokedPeers = new();
    private readonly List<Task> _backgroundTasks = new();
    private PieceManager? _pieceManager;
    private CancellationTokenSource? _cts;
    private readonly ConcurrentDictionary<string, int> _peerStrikes = new();
    private readonly ConcurrentDictionary<string, long> _downloadedFromPeer = new();
    private readonly ConcurrentDictionary<string, long> _uploadedToPeer = new();
    public bool SequentialMode { get; set; } = false;

    private readonly SemaphoreSlim _downloadSemaphore = new(1, 1);
    private const int MaxActiveSessions = 100;
    public byte[]? InfoHash { get; private set; }
    public string Id { get; private set; } = Guid.NewGuid().ToString("N");
    public void SetId(string id) => Id = id;
    public string Name { get; set; } = "Initializing...";
    public string Status { get; set; } = "Queued";
    public int TotalPieces { get; set; }

    private int _lastCompletedPieces;
    public int CompletedPieces => _pieceManager?.CompletedCount ?? _lastCompletedPieces;

    public int ActivePeersCount => _activeSessions.Count;
    public long TotalSize { get; private set; }

    public string? TorrentPath { get; set; }
    public string? MagnetUri { get; set; }
    public string? OutputDir { get; set; }
    public string? ClientId { get; set; }
    public string? MetadataCacheDir { get; set; }
    public byte[]? InitialBitfield { get; set; }


    public async Task Stop()
    {
        _cts?.Cancel();
        
        // Force-dispose all active sessions to close sockets and release any potential locks
        lock (_activeSessions)
        {
            foreach (var session in _activeSessions.ToArray())
            {
                try { session.Dispose(); } catch { }
            }
            _activeSessions.Clear();
        }

        // Wait for all background tasks (Discovery, DHT, Choking, and Peer sessions) to finish
        Task[] tasks;
        lock (_backgroundTasks) tasks = _backgroundTasks.ToArray();

        if (tasks.Length > 0)
        {
            // Use a timeout to avoid hanging forever if a task is stuck
            var timeoutTask = Task.Delay(5000);
            var whenAllTask = Task.WhenAll(tasks);
            await Task.WhenAny(whenAllTask, timeoutTask);
            
            lock (_backgroundTasks) _backgroundTasks.Clear();
        }

        // Save progress before disposing piece manager
        if (_pieceManager != null)
        {
            _lastCompletedPieces = _pieceManager.CompletedCount;
            InitialBitfield = _pieceManager.GetBitfield();
            _pieceManager.Dispose();
            _pieceManager = null;
        }
        
        // One final GC to help Windows release Memory Mapped File handles
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }
    public byte[]? GetBitfield() => _pieceManager?.GetBitfield() ?? InitialBitfield;


    public void AddDiscoveredPeer(string address)
    {
        if (!_triedPeers.ContainsKey(address))
        {
            _peerDiscoveryQueue.Enqueue(address);
        }
    }

    public async Task StartDownload(string torrentPath, string? outputDir = null)
    {
        this.TorrentPath = torrentPath;
        this.OutputDir = outputDir;
        Console.WriteLine($"[+] Starting torrent download from file: {torrentPath}");


        byte[] torrentData = TorrentFileReader.Read(torrentPath);
        var parser = new BencodeParser(torrentData);
        var meta = parser.Parse() as Dictionary<string, object>;

        if (meta == null) 
        {
            Status = "Error: Invalid Torrent";
            return;
        }

        var infoDict = meta["info"] as Dictionary<string, object>;
        var metadata = new TorrentMetadata(infoDict!);

        byte[] infoHash = TorrentCrypto.ComputeInfoHash(parser.RawInfoBytes);
        string peerId = "-TS0001-" + Guid.NewGuid().ToString("N")[..12];

        List<string> trackers = GetTrackers(meta);
        await ExecuteDownload(infoHash, peerId, metadata, trackers, outputDir);
    }

    public async Task StartMagnetDownload(string magnetUri, string? outputDir = null)
    {
        this.MagnetUri = magnetUri;
        this.OutputDir = outputDir;
        var magnet = MagnetParser.Parse(magnetUri);
        string peerId = "-TS0001-" + Guid.NewGuid().ToString("N")[..12];

        Console.WriteLine("[*] Searching DHT and Trackers for magnet peers...");
        
        // 1. Initial concurrent discovery for metadata fetching
        Task.Run(async () => {
            var ps = await _dht.GetPeersAsync(magnet.InfoHash);
            foreach (var p in ps) if (_triedPeers.TryAdd(p, true)) _peerDiscoveryQueue.Enqueue(p);
        }).FireAndForget("DHT Discovery");

        foreach (var url in GetDiscoveryTrackers(magnet.Trackers))
        {
            Task.Run(async () => {
                try {
                    var ps = await _tracker.GetPeers(url, magnet.InfoHash, 0, peerId);
                    foreach (var p in ps) if (_triedPeers.TryAdd(p, true)) _peerDiscoveryQueue.Enqueue(p);
                } catch { }
            }).FireAndForget("Tracker Discovery");
        }

        // Wait for at least some peers to be discovered
        DateTime start = DateTime.Now;
        while (_peerDiscoveryQueue.IsEmpty && (DateTime.Now - start).TotalSeconds < 30) await Task.Delay(500);

        // 2. Fetch metadata from peers concurrently
        var fetcher = new MetadataFetcher();
        byte[]? infoData = null;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var fetchTasks = new List<Task>();
        const int MaxMetadataConcurrent = 20;

        while (infoData == null && !cts.IsCancellationRequested)
        {
            while (fetchTasks.Count < MaxMetadataConcurrent && _peerDiscoveryQueue.TryDequeue(out string? peerAddr))
            {
                var pAddr = peerAddr;
                fetchTasks.Add(Task.Run(async () => {
                    try {
                        var (ip, port) = ParsePeer(pAddr);
                        var data = await fetcher.FetchMetadataAsync(ip, port, magnet.InfoHash, peerId, cts.Token);
                        if (data != null) {
                            Interlocked.CompareExchange(ref infoData, data, null);
                            cts.Cancel(); // Success!
                        }
                    } catch { }
                }));
            }

            if (fetchTasks.Count == 0 && _peerDiscoveryQueue.IsEmpty) break;

            var finished = await Task.WhenAny(fetchTasks.Concat(new[] { Task.Delay(2000, cts.Token) }));
            fetchTasks.RemoveAll(t => t.IsCompleted);

            if (infoData != null) break;
        }

        if (infoData == null) 
        { 
            Status = "Error: Metadata Fetch Failed";
            return; 
        }

        var parser = new BencodeParser(infoData);
        var infoDict = parser.Parse() as Dictionary<string, object>;
        if (infoDict == null)
        {
            Status = "Error: Metadata Parsing Failed";
            return;
        }
        var metadata = new TorrentMetadata(infoDict);

        // Cache metadata so we don't have to fetch it again on resume
        if (!string.IsNullOrEmpty(MetadataCacheDir) && infoDict != null)
        {
            try
            {
                Directory.CreateDirectory(MetadataCacheDir);
                string hexHash = BitConverter.ToString(magnet.InfoHash).Replace("-", "").ToLower();
                string cachePath = Path.Combine(MetadataCacheDir, hexHash + ".torrent");
                
                var rootDict = new Dictionary<string, object> { { "info", infoDict } };
                byte[] torrentFileBytes = BencodeEncoder.EncodeDictionary(rootDict);
                await File.WriteAllBytesAsync(cachePath, torrentFileBytes);
                this.TorrentPath = cachePath;
            }
            catch { /* non-critical */ }
        }

        await ExecuteDownload(magnet.InfoHash, peerId, metadata, magnet.Trackers, outputDir);
    }

    private async Task ExecuteDownload(byte[] infoHash, string peerId, TorrentMetadata metadata, List<string> trackers, string? outputDir, List<string>? initialPeers = null)
    {
        InfoHash = infoHash;
        await _downloadSemaphore.WaitAsync();
        try
        {
            _pieceManager?.Dispose();
            _pieceManager = null;

            _triedPeers.Clear(); // Critical: Reset so we can connect to peers used for metadata

        TotalPieces = metadata.Pieces.Length / 20;
        _lastCompletedPieces = InitialBitfield != null ? CountSetBits(InitialBitfield) : 0;
        TotalSize = metadata.TotalLength;

        bool isResume = Name != "Initializing...";
        
        if (!isResume)
        {
            Name = metadata.Name;
            
            // Handle name conflicts
            string finalName = Name;
            int counter = 1;
            while (Directory.Exists(Path.Combine(outputDir!, finalName)) || 
                   File.Exists(Path.Combine(outputDir!, finalName)))
            {
                finalName = $"{Name} ({counter++})";
            }
            
            if (finalName != Name)
            {
                Name = finalName;
            }
            metadata.Rename(Name);
        }
        else
        {
            if (metadata.Name != Name)
            {
                metadata.Rename(Name);
            }
        }
        
        Status = "Downloading";
        Console.WriteLine($"[!] Torrent '{Name}' metadata parsed. Size: {TotalSize / 1024 / 1024} MB, Pieces: {TotalPieces}");


        _pieceManager = new PieceManager(metadata, outputDir!, InitialBitfield);

        _cts = new CancellationTokenSource();
        var cts = _cts;

        // 1. Start parallel background discovery
        var discoveryTask = Task.Run(async () => {
            try {
                while (!cts.IsCancellationRequested) {
                    var discoveryTasks = GetDiscoveryTrackers(trackers).Select(async t => {
                        try {
                            var ps = await _tracker.GetPeers(t, infoHash, metadata.TotalLength, peerId);
                            foreach (var p in ps) _peerDiscoveryQueue.Enqueue(p);
                        } catch { }
                    });
                    await Task.WhenAll(discoveryTasks);
                    await Task.Delay(TimeSpan.FromMinutes(5), cts.Token);
                }
            } catch (OperationCanceledException) { /* normal shutdown */ }
        }, cts.Token);
        lock (_backgroundTasks) _backgroundTasks.Add(discoveryTask);

        // 1b. Start Choking (Tit-for-Tat) Loop
        var chokingTask = Task.Run(async () => {
            try {
                while (!cts.IsCancellationRequested) {
                    ManageChoking();
                    await Task.Delay(10000, cts.Token);
                }
            } catch (OperationCanceledException) { /* normal shutdown */ }
        }, cts.Token);
        lock (_backgroundTasks) _backgroundTasks.Add(chokingTask);

        // 1c. Aggressive DHT discovery
        var dhtTask = Task.Run(async () => {
            try {
                while (!cts.IsCancellationRequested) {
                    try {
                        var ps = await _dht.GetPeersAsync(infoHash);
                        foreach (var p in ps) _peerDiscoveryQueue.Enqueue(p);
                    } catch (OperationCanceledException) { throw; } catch { }
                    await Task.Delay(TimeSpan.FromMinutes(1), cts.Token);
                }
            } catch (OperationCanceledException) { /* normal shutdown */ }
        }, cts.Token);
        lock (_backgroundTasks) _backgroundTasks.Add(dhtTask);

        if (initialPeers != null) {
            Console.WriteLine($"[i] Using {initialPeers.Count} initial peers.");
            foreach (var p in initialPeers) _peerDiscoveryQueue.Enqueue(p);
        }

        // 2. Main download loop
        for (int i = 0; i < MaxActiveSessions; i++)
        {
            var downloadTask = Task.Run(async () => {
                while (!cts.IsCancellationRequested && _pieceManager != null && _pieceManager.CompletedCount < TotalPieces)
                {
                    if (_peerDiscoveryQueue.TryDequeue(out string? peerAddr))
                    {
                        if (!_triedPeers.TryAdd(peerAddr, true)) continue;

                        try {
                            using var session = new PeerSession(peerAddr, infoHash, peerId, TotalPieces);
                            session.OnPeersDiscovered += (parent, found) => {
                                foreach (var f in found) _peerDiscoveryQueue.Enqueue(f);
                            };

                            if (await session.StartAsync(cts.Token))
                            {
                                lock (_activeSessions) _activeSessions.Add(session);
                                
                                try {
                                    while (session.Connected && _pieceManager.CompletedCount < TotalPieces)
                                    {
                                        int pieceIndex = PickPieceRarestFirst(session, _pieceManager, TotalPieces, out bool isEndgame);
                                        if (pieceIndex == -1) { await Task.Delay(100); continue; }

                                        if (_pieceManager.TryClaimPiece(pieceIndex) || isEndgame)
                                        {
                                            try {
                                                var data = await session.RequestPieceAsync(pieceIndex, GetPieceLength(pieceIndex, TotalPieces, metadata.TotalLength, metadata.PieceLength), cts.Token);
                                                
                                                byte[] expectedHash = new byte[20];
                                                Buffer.BlockCopy(metadata.Pieces, pieceIndex * 20, expectedHash, 0, 20);

                                                if (PieceVerifier.Verify(data, expectedHash)) {
                                                    _pieceManager.Store(pieceIndex, data);
                                                    _downloadedFromPeer.AddOrUpdate(session.Address, data.Length, (k, v) => v + data.Length);
                                                } else {
                                                    _pieceManager.ReleasePiece(pieceIndex);
                                                    // Optimization 5: Corrupt Peer Banning
                                                    int strikes = _peerStrikes.AddOrUpdate(session.Address, 1, (k, v) => v + 1);
                                                    if (strikes >= 3) {
                                                        Console.WriteLine($"[!] Banning peer {session.Address} for sending corrupt data.");
                                                        session.Dispose();
                                                    }
                                                }
                                            } catch {
                                                _pieceManager.ReleasePiece(pieceIndex);
                                                throw; // Drop connection if download fails
                                            }
                                        }
                                    }
                                } finally {
                                    lock (_activeSessions) _activeSessions.Remove(session);
                                }
                            }
                        } catch { }
                    }
                    else
                    {
                        // Wait for more peers
                        await Task.Delay(500);
                    }
                }
            });
            lock (_backgroundTasks) _backgroundTasks.Add(downloadTask);
        }

        try {
            while (_pieceManager.CompletedCount < TotalPieces && !cts.IsCancellationRequested) {
                await Task.Delay(1000, cts.Token);
            }
        } catch (OperationCanceledException) { /* paused/stopped */ }

        if (_pieceManager.CompletedCount >= TotalPieces)
        {
            Status = "Completed";
            Console.WriteLine($"[v] Torrent '{Name}' download completed successfully!");
        }
        else
        {
            Status = "Stopped";
            Console.WriteLine($"[x] Torrent '{Name}' download stopped/cancelled.");
        }


        cts.Cancel();
        Task[] tasks;
        lock (_backgroundTasks) tasks = _backgroundTasks.ToArray();
        try { await Task.WhenAll(tasks); } catch (OperationCanceledException) { /* expected */ }
        lock (_backgroundTasks) _backgroundTasks.Clear();
        
        _lastCompletedPieces = _pieceManager?.CompletedCount ?? _lastCompletedPieces;
        InitialBitfield = _pieceManager?.GetBitfield() ?? InitialBitfield;

        _pieceManager?.Dispose();

        _pieceManager = null;
        }
        finally
        {
            _downloadSemaphore.Release();
            InfoHash = null;
        }
    }

    public async Task HandleIncomingConnection(PeerSession session)
    {
        var task = Task.Run(async () => {
            if (_cts == null || _cts.IsCancellationRequested || _pieceManager == null) 
            {
                session.Dispose();
                return;
            }

            if (await session.StartAsync(_cts.Token))
            {
                lock (_activeSessions) _activeSessions.Add(session);
                try
                {
                    while (session.Connected && _pieceManager != null && _pieceManager.CompletedCount < TotalPieces)
                    {
                        int pieceIndex = PickPieceRarestFirst(session, _pieceManager, TotalPieces, out bool isEndgame);
                        if (pieceIndex == -1) { await Task.Delay(100); continue; }

                        if (_pieceManager.TryClaimPiece(pieceIndex) || isEndgame)
                        {
                            try {
                                int pLen = GetPieceLength(pieceIndex, TotalPieces, TotalSize, (int)(TotalSize / TotalPieces));
                                var data = await session.RequestPieceAsync(pieceIndex, pLen, _cts.Token);
                                _pieceManager.Store(pieceIndex, data);
                            } catch { 
                                _pieceManager.ReleasePiece(pieceIndex); 
                            }
                        }
                    }
                }
                finally 
                { 
                    lock (_activeSessions) _activeSessions.Remove(session); 
                    session.Dispose(); 
                }
            }
        });

        lock (_backgroundTasks) _backgroundTasks.Add(task);
        await task;
        lock (_backgroundTasks) _backgroundTasks.Remove(task);
    }


    private void ManageChoking()
    {
        lock (_activeSessions)
        {
            if (_activeSessions.Count <= 4)
            {
                foreach (var s in _activeSessions) s.Unchoke();
                return;
            }

            // Rank peers by download speed (Tit-for-Tat)
            var topPeers = _activeSessions
                .OrderByDescending(s => _downloadedFromPeer.GetValueOrDefault(s.Address, 0))
                .Take(4)
                .ToList();

            // Optimistic unchoke (pick one random peer to see if they are fast)
            var others = _activeSessions.Except(topPeers).ToList();
            if (others.Count > 0) topPeers.Add(others[Random.Shared.Next(others.Count)]);

            foreach (var s in _activeSessions)
            {
                if (topPeers.Contains(s)) s.Unchoke();
                else s.Choke();
            }

            // Reset speeds for next interval
            _downloadedFromPeer.Clear();
        }
    }

    private int PickPieceRarestFirst(PeerSession session, PieceManager pieceManager, int totalPieces, out bool isEndgame)
    {
        isEndgame = false;
        var candidates = new List<int>();
        var endgameCandidates = new List<int>();

        // Optimization 7: Sequential Mode
        if (SequentialMode)
        {
            for (int i = 0; i < totalPieces; i++)
            {
                if (session.Bitfield.HasPiece(i) && !pieceManager.IsPieceCompleted(i))
                {
                    if (!pieceManager.IsClaimed(i)) return i;
                }
            }
        }

        for (int i = 0; i < totalPieces; i++)
        {
            if (session.Bitfield.HasPiece(i) && !pieceManager.IsPieceCompleted(i))
            {
                if (!pieceManager.IsClaimed(i)) candidates.Add(i);
                else endgameCandidates.Add(i);
            }
        }

        if (candidates.Count > 0)
        {
            var availability = new int[totalPieces];
            lock (_activeSessions)
            {
                foreach (var s in _activeSessions)
                {
                    foreach (var c in candidates)
                        if (s.Bitfield.HasPiece(c)) availability[c]++;
                }
            }

            int minAvailability = candidates.Min(c => availability[c]);
            var rarestCandidates = candidates.Where(c => availability[c] == minAvailability).ToList();
            
            return rarestCandidates[Random.Shared.Next(rarestCandidates.Count)];
        }

        if (endgameCandidates.Count > 0)
        {
            isEndgame = true;
            return endgameCandidates[Random.Shared.Next(endgameCandidates.Count)];
        }

        return -1;
    }

    // [Rest of helpers: GetTrackers, GetDiscoveryTrackers, ParsePeer, GetPieceLength]
    private List<string> GetDiscoveryTrackers(List<string> original)
    {
        var list = original.Distinct().ToList();
        var fallbacks = new[] {
            "udp://tracker.opentrackr.org:1337/announce",
            "udp://tracker.openbittorrent.com:80/announce",
            "udp://9.rarbg.to:2710/announce",
            "udp://exodus.desync.com:6969/announce"
        };
        foreach (var f in fallbacks) if (!list.Contains(f)) list.Add(f);
        return list;
    }

    private (string ip, int port) ParsePeer(string peer)
    {
        var parts = peer.Split(':');
        return (parts[0], int.Parse(parts[1]));
    }

    private int GetPieceLength(int index, int totalPieces, long fileSize, int pieceLength)
    {
        if (index == totalPieces - 1) {
            long last = fileSize % pieceLength;
            return last == 0 ? pieceLength : (int)last;
        }
        return pieceLength;
    }

    private List<string> GetTrackers(Dictionary<string, object> meta)
    {
        var trackers = new List<string>();
        if (meta.ContainsKey("announce")) trackers.Add(Encoding.UTF8.GetString((meta["announce"] as byte[])!));
        if (meta.ContainsKey("announce-list") && meta["announce-list"] is List<object> list)
            foreach (var tier in list) if (tier is List<object> tl) foreach (var t in tl) trackers.Add(Encoding.UTF8.GetString((t as byte[])!));
        return trackers.Distinct().ToList();
    }

    private int CountSetBits(byte[] bitfield)
    {
        int count = 0;
        foreach (byte b in bitfield)
        {
            int v = b;
            for (int i = 0; i < 8; i++)
            {
                if ((v & (1 << i)) != 0) count++;
            }
        }
        return count;
    }
}
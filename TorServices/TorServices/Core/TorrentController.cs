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
    private PieceManager? _pieceManager;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _downloadSemaphore = new(1, 1);
    private const int MaxActiveSessions = 30;
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
    public byte[]? InitialBitfield { get; set; }


    public void Stop() => _cts?.Cancel();
    public byte[]? GetBitfield() => _pieceManager?.GetBitfield();


    public async Task StartDownload(string torrentPath, string? outputDir = null)
    {
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

        var magnet = MagnetParser.Parse(magnetUri);
        string peerId = "-TS0001-" + Guid.NewGuid().ToString("N")[..12];

        Console.WriteLine("[*] Searching DHT and Trackers for magnet peers...");
        
        // 1. Initial concurrent discovery for metadata fetching
        _ = Task.Run(async () => {
            var ps = await _dht.GetPeersAsync(magnet.InfoHash);
            foreach (var p in ps) if (_triedPeers.TryAdd(p, true)) _peerDiscoveryQueue.Enqueue(p);
        });

        foreach (var url in GetDiscoveryTrackers(magnet.Trackers))
        {
            _ = Task.Run(async () => {
                try {
                    var ps = await _tracker.GetPeers(url, magnet.InfoHash, 0, peerId);
                    foreach (var p in ps) if (_triedPeers.TryAdd(p, true)) _peerDiscoveryQueue.Enqueue(p);
                } catch { }
            });
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
        var metadata = new TorrentMetadata(infoDict!);

        await ExecuteDownload(magnet.InfoHash, peerId, metadata, magnet.Trackers, outputDir);
    }

    private async Task ExecuteDownload(byte[] infoHash, string peerId, TorrentMetadata metadata, List<string> trackers, string? outputDir, List<string>? initialPeers = null)
    {
        await _downloadSemaphore.WaitAsync();
        try
        {
            _pieceManager?.Dispose();
            _pieceManager = null;

            _triedPeers.Clear(); // Critical: Reset so we can connect to peers used for metadata

        TotalPieces = metadata.Pieces.Length / 20;
        _lastCompletedPieces = InitialBitfield != null ? CountSetBits(InitialBitfield) : 0;
        TotalSize = metadata.TotalLength;

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
            metadata.Rename(finalName);
        }
        
        Status = "Downloading";
        Console.WriteLine($"[!] Torrent '{Name}' metadata parsed. Size: {TotalSize / 1024 / 1024} MB, Pieces: {TotalPieces}");


        _pieceManager = new PieceManager(metadata, outputDir!, InitialBitfield);

        _cts = new CancellationTokenSource();
        var cts = _cts;

        // 1. Start background discovery
        _ = Task.Run(async () => {
            while (!cts.IsCancellationRequested) {
                foreach (var t in GetDiscoveryTrackers(trackers)) {
                    try {
                        var ps = await _tracker.GetPeers(t, infoHash, metadata.TotalLength, peerId);
                        var newPeers = ps.Where(p => !_triedPeers.ContainsKey(p)).ToList();
                        if (newPeers.Count > 0) {
                            foreach (var p in newPeers) _peerDiscoveryQueue.Enqueue(p);
                        }
                    } catch { }
                }
                await Task.Delay(TimeSpan.FromMinutes(15), cts.Token);
            }
        }, cts.Token);

        _ = Task.Run(async () => {
            while (!cts.IsCancellationRequested) {
                var ps = await _dht.GetPeersAsync(infoHash);
                var newPeers = ps.Where(p => !_triedPeers.ContainsKey(p)).ToList();
                if (newPeers.Count > 0) {
                    foreach (var p in newPeers) _peerDiscoveryQueue.Enqueue(p);
                }
                await Task.Delay(TimeSpan.FromMinutes(5), cts.Token);
            }
        }, cts.Token);

        if (initialPeers != null) {
            Console.WriteLine($"[i] Using {initialPeers.Count} initial peers.");
            foreach (var p in initialPeers) _peerDiscoveryQueue.Enqueue(p);
        }

        // 2. Main download loop
        var downloadTasks = new List<Task>();
        for (int i = 0; i < MaxActiveSessions; i++)
        {
            downloadTasks.Add(Task.Run(async () => {
                while (!cts.IsCancellationRequested && _pieceManager.CompletedCount < TotalPieces)
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
                                        if (pieceIndex == -1) { await Task.Delay(1000); continue; }

                                        if (_pieceManager.TryClaimPiece(pieceIndex) || isEndgame)
                                        {
                                            try {
                                                var data = await session.RequestPieceAsync(pieceIndex, GetPieceLength(pieceIndex, TotalPieces, metadata.TotalLength, metadata.PieceLength), cts.Token);
                                                
                                                byte[] expectedHash = new byte[20];
                                                Buffer.BlockCopy(metadata.Pieces, pieceIndex * 20, expectedHash, 0, 20);

                                                if (PieceVerifier.Verify(data, expectedHash)) {
                                                    _pieceManager.Store(pieceIndex, data);
                                                } else {
                                                    _pieceManager.ReleasePiece(pieceIndex);
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
            }));
        }

        while (_pieceManager.CompletedCount < TotalPieces && !cts.IsCancellationRequested) {
            await Task.Delay(1000);
        }

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
        await Task.WhenAll(downloadTasks);
        
        _lastCompletedPieces = _pieceManager?.CompletedCount ?? _lastCompletedPieces;
        InitialBitfield = _pieceManager?.GetBitfield() ?? InitialBitfield;

        _pieceManager?.Dispose();

        _pieceManager = null;
    }
    finally
    {
        _downloadSemaphore.Release();
    }
}


    private int PickPieceRarestFirst(PeerSession session, PieceManager pieceManager, int totalPieces, out bool isEndgame)
    {
        isEndgame = false;
        var candidates = new List<int>();
        var endgameCandidates = new List<int>();

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
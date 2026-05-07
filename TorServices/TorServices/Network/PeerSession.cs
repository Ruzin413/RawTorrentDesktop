using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using TorServices.Core;

namespace TorServices.Network;

public class PeerSession : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly ExtensionManager _extensions = new();
    private readonly Bitfield _bitfield;
    private readonly byte[] _infoHash;
    private readonly string _peerId;
    private bool _isChoked = true;
    private bool _isInterested = false;
    private bool _running = true;
    
    // Dispatch piece data to waiting Request calls
    private readonly ConcurrentDictionary<(int index, int begin), TaskCompletionSource<byte[]>> _pendingBlocks = new();

    public Bitfield Bitfield => _bitfield;
    public bool IsChoked => _isChoked;
    public string Address { get; }
    public bool Connected => _tcp.Connected && _running;

    public event Action<string, List<string>>? OnPeersDiscovered;

    public PeerSession(string address, byte[] infoHash, string peerId, int pieceCount)
    {
        Address = address;
        _infoHash = infoHash;
        _peerId = peerId;
        _bitfield = new Bitfield(pieceCount);

        var (ip, port) = ParsePeer(address);
        _tcp = new TcpClient();
        _tcp.Connect(ip, port);
        _stream = _tcp.GetStream();
    }

    public async Task<bool> StartAsync(CancellationToken token)
    {
        var peerClient = new PeerClient();
        var (hsOk, hsExt) = await peerClient.HandshakeAsync(_stream, _infoHash, _peerId);
        if (!hsOk) return false;

        if (hsExt)
        {
            await ExtensionManager.SendHandshakeAsync(_stream, new Dictionary<int, string> { { 1, "ut_metadata" }, { 2, "ut_pex" } });
        }

        _ = Task.Run(() => ReceiveLoop(token), token);
        
        // Signal interest immediately to get into unchoke queue
        await SetInterestedAsync(true);

        // Initial bitfield might be sent here or in ReceiveLoop
        await Task.Delay(500, token); 
        return true;
    }

    public async Task SetInterestedAsync(bool interested)
    {
        if (_isInterested == interested) return;
        _isInterested = interested;
        byte id = interested ? PeerMessage.Interested : PeerMessage.NotInterested;
        await PeerClient.SendMessageAsync(_stream, id, Array.Empty<byte>());
    }

    public async Task<byte[]> RequestPieceAsync(int index, int length, CancellationToken token)
    {
        if (_isChoked)
        {
            await SetInterestedAsync(true);
            // Wait for unchoke
            var unchokeCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            unchokeCts.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                while (_isChoked && !unchokeCts.IsCancellationRequested)
                {
                    await Task.Delay(100, unchokeCts.Token);
                }
            } 
            catch (OperationCanceledException) { }

            if (_isChoked) throw new TimeoutException("Timed out waiting for unchoke.");
        }
        
        token.ThrowIfCancellationRequested();
        const int BlockSize = 16384;
        byte[] data = new byte[length];
        int received = 0;
        int requested = 0;
        var blocks = new List<Task<byte[]>>();

        while (received < length)
        {
            token.ThrowIfCancellationRequested();

            while (blocks.Count < 10 && requested < length)
            {
                int bLen = Math.Min(BlockSize, length - requested);
                blocks.Add(RequestBlockAsync(index, requested, bLen, token));
                requested += bLen;
            }

            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(15), token);
            var finishedTask = await Task.WhenAny(blocks.Cast<Task>().Append(timeoutTask));
            
            if (finishedTask == timeoutTask)
            {
                throw new TimeoutException("Timed out waiting for block data.");
            }

            var finished = (Task<byte[]>)finishedTask;
            blocks.Remove(finished);
            
            byte[] blockData = await finished;
            // The block data includes header (index 4 bytes, begin 4 bytes)
            Buffer.BlockCopy(blockData, 8, data, (blockData[4] << 24) | (blockData[5] << 16) | (blockData[6] << 8) | blockData[7], blockData.Length - 8);
            received += (blockData.Length - 8);
        }

        return data;
    }

    private async Task<byte[]> RequestBlockAsync(int index, int begin, int length, CancellationToken token)
    {
        var tcs = new TaskCompletionSource<byte[]>();
        _pendingBlocks[(index, begin)] = tcs;

        byte[] request = new byte[12];
        PeerClient.WriteInt(request, 0, index);
        PeerClient.WriteInt(request, 4, begin);
        PeerClient.WriteInt(request, 8, length);

        await PeerClient.SendMessageAsync(_stream, PeerMessage.Request, request, token);

        using var registration = token.Register(() => tcs.TrySetCanceled());
        return await tcs.Task;
    }

    private async Task ReceiveLoop(CancellationToken token)
    {
        while (_running && _tcp.Connected)
        {
            try
            {
                var (id, payload) = await PeerClient.ReadMessageAsync(_stream, token);

                switch (id)
                {
                    case PeerMessage.Choke: _isChoked = true; break;
                    case PeerMessage.Unchoke: _isChoked = false; break;
                    case PeerMessage.Have:
                        int pieceIndex = (payload[0] << 24) | (payload[1] << 16) | (payload[2] << 8) | payload[3];
                        _bitfield.SetPiece(pieceIndex, true);
                        break;
                    case PeerMessage.Bitfield:
                        var bf = new Bitfield(payload, _bitfield.Length);
                        for (int i = 0; i < bf.Length; i++) _bitfield.SetPiece(i, bf.HasPiece(i));
                        break;
                    case PeerMessage.Piece:
                        int idx = (payload[0] << 24) | (payload[1] << 16) | (payload[2] << 8) | payload[3];
                        int beg = (payload[4] << 24) | (payload[5] << 16) | (payload[6] << 8) | payload[7];
                        if (_pendingBlocks.TryRemove((idx, beg), out var tcs))
                        {
                            tcs.TrySetResult(payload);
                        }
                        break;
                    case PeerMessage.Extended:
                        if (payload.Length > 0)
                        {
                            int extId = payload[0];
                            byte[] extPayload = new byte[payload.Length - 1];
                            Buffer.BlockCopy(payload, 1, extPayload, 0, extPayload.Length);
                            
                            if (extId == 0) _extensions.HandleHandshake(extPayload);
                            else if (_extensions.SupportsPex && extId == _extensions.GetExtensionId("ut_pex"))
                            {
                                var found = _extensions.ParsePexMessage(extPayload);
                                OnPeersDiscovered?.Invoke(Address, found);
                            }
                        }
                        break;
                }
            }
            catch { break; }
        }
    }

    private (string ip, int port) ParsePeer(string peer)
    {
        var parts = peer.Split(':');
        return (parts[0], int.Parse(parts[1]));
    }

    public void Dispose()
    {
        _running = false;
        _stream?.Dispose();
        _tcp?.Dispose();
    }
}

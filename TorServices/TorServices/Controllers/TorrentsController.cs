using Microsoft.AspNetCore.Mvc;
using TorServices.Models;
using TorServices.DTOs;
using TorServices.Services;
using TorServices.Parser;
using TorServices.Core;
using TorServices.Network;
using System.IO;

namespace TorServices.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TorrentsController : ControllerBase
{
    private readonly TorrentService _torrentService;

    public TorrentsController(TorrentService torrentService)
    {
        _torrentService = torrentService;
    }
    [HttpGet]
    public ActionResult<IEnumerable<TorrentStatus>> Get([FromQuery] string? clientId)
    {
        return Ok(_torrentService.GetAllTorrents(clientId));
    }
    [HttpGet("history")]
    public ActionResult<IEnumerable<TorrentStatus>> GetHistory([FromQuery] string? clientId)
    {
        return Ok(_torrentService.GetHistory(clientId));
    }
    [HttpPost("browse-native")]
    public IActionResult BrowseNative()
    {
        try
        {
            var script = "Add-Type -AssemblyName System.Windows.Forms; $f = New-Object System.Windows.Forms.FolderBrowserDialog; $f.ShowNewFolderButton = $true; if($f.ShowDialog() -eq 'OK') { $f.SelectedPath }";
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = $"-Command \"{script}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };
            process.Start();
            string result = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return Ok(new { path = result });
        }
        catch (Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }
    [HttpPost("browse-file-native")]
    public IActionResult BrowseFileNative()
    {
        try
        {
            var script = "Add-Type -AssemblyName System.Windows.Forms; $f = New-Object System.Windows.Forms.OpenFileDialog; $f.Filter = 'Torrent Files (*.torrent)|*.torrent|All Files (*.*)|*.*'; if($f.ShowDialog() -eq 'OK') { $f.FileName }";
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = $"-Command \"{script}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };
            process.Start();
            string result = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return Ok(new { path = result });
        }
        catch (Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }


    [HttpGet("list-drives")]
    public IActionResult ListDrives()
    {
        try
        {
            var drives = DriveInfo.GetDrives()
                .Where(d => d.IsReady)
                .Select(d => new { name = d.Name.Replace("\\", ""), path = d.Name })
                .ToList();
            return Ok(drives);
        }
        catch (Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpGet("list-directories")]
    public IActionResult ListDirectories([FromQuery] string? path)
    {
        try
        {
            string root = string.IsNullOrEmpty(path) ? (OperatingSystem.IsWindows() ? "C:\\" : "/") : path;
            
            if (!Directory.Exists(root)) return BadRequest("Path does not exist");

            var dirs = Directory.GetDirectories(root)
                .Select(d => new { name = Path.GetFileName(d), path = d, isDirectory = true })
                .ToList();

            var files = Directory.GetFiles(root)
                .Select(f => new { name = Path.GetFileName(f), path = f, isDirectory = false })
                .ToList();

            return Ok(new { 
                currentPath = root, 
                parentPath = Path.GetDirectoryName(root),
                entries = dirs.Concat(files).OrderByDescending(e => e.isDirectory).ThenBy(e => e.name)
            });
        }
        catch (Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }
    [HttpPost("metadata")]
    public async Task<IActionResult> GetMetadata([FromBody] MetadataRequest request)
    {
        try
        {
            if (!string.IsNullOrEmpty(request.Path))
            {
                byte[] torrentData = System.IO.File.ReadAllBytes(request.Path);
                var parser = new BencodeParser(torrentData);
                var meta = parser.Parse() as Dictionary<string, object>;
                if (meta == null) return BadRequest("Invalid torrent file");
                
                var infoDict = meta["info"] as Dictionary<string, object>;
                var metadata = new TorrentMetadata(infoDict!);
                
                return Ok(new { name = metadata.Name, size = metadata.TotalLength });
            }
            if (!string.IsNullOrEmpty(request.Magnet))
            {
                var magnet = MagnetParser.Parse(request.Magnet);
                var fetcher = new MetadataFetcher();
                string peerId = "-TS0001-" + Guid.NewGuid().ToString("N")[..12];
                
                // For magnets, we need to discover some peers first
                var ps = await _torrentService.DiscoverPeersForMetadata(magnet.InfoHash, magnet.Trackers, peerId);
                
                byte[]? infoData = null;
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                
                var fetchTasks = ps.Select(peerAddr => Task.Run(async () =>
                {
                    try {
                        var parts = peerAddr.Split(':');
                        var data = await fetcher.FetchMetadataAsync(parts[0], int.Parse(parts[1]), magnet.InfoHash, peerId, cts.Token);
                        if (data != null) {
                            Interlocked.CompareExchange(ref infoData, data, null);
                            cts.Cancel();
                        }
                    } catch { }
                })).ToList();

                if (fetchTasks.Any())
                {
                    await Task.WhenAny(Task.WhenAll(fetchTasks), Task.Delay(TimeSpan.FromSeconds(30)));
                }

                if (infoData == null) return BadRequest("Metadata fetch timed out");

                var parser = new BencodeParser(infoData);
                var infoDict = parser.Parse() as Dictionary<string, object>;
                var metadata = new TorrentMetadata(infoDict!);
                
                return Ok(new { name = metadata.Name, size = metadata.TotalLength });
            }

            return BadRequest("Path or Magnet URI required.");
        }
        catch (Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPost("download")]
    public async Task<IActionResult> Download([FromBody] DownloadRequest request)
    {
        try
        {
            if (!string.IsNullOrEmpty(request.Path))
            {
                var id = await _torrentService.StartTorrent(request.Path, request.OutputDir, request.ClientId);
                return Ok(new { id });
            }
            
            if (!string.IsNullOrEmpty(request.Magnet))
            {
                var id = await _torrentService.StartMagnet(request.Magnet, request.OutputDir, request.ClientId);
                return Ok(new { id });
            }

            return BadRequest("Path or Magnet URI required.");
        }
        catch (Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPost("{id}/resume")]
    public IActionResult Resume(string id)
    {
        if (_torrentService.ResumeTorrent(id)) return Ok();
        return NotFound();
    }

    [HttpPost("{id}/stop")]
    public IActionResult Stop(string id)
    {
        if (_torrentService.StopTorrent(id)) return Ok();
        return NotFound();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Remove(string id)
    {
        if (await _torrentService.RemoveTorrent(id)) return Ok();
        return NotFound();
    }

    [HttpPost("open-folder")]
    public IActionResult OpenFolder([FromQuery] string path)
    {
        if (string.IsNullOrEmpty(path)) return BadRequest("Path is required");
        if (!Directory.Exists(path)) return NotFound("Directory not found");
        
        try
        {
            System.Diagnostics.Process.Start("explorer.exe", path);
            return Ok();
        }
        catch (Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpDelete("clear-all")]
    public async Task<IActionResult> ClearAll([FromQuery] string? clientId)
    {
        await _torrentService.ClearAllData(clientId);
        return Ok();
    }

}

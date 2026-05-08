using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace TorServices.Network;

public class UpnpService
{
    private static readonly string SSDP_QUERY = 
        "M-SEARCH * HTTP/1.1\r\n" +
        "HOST: 239.255.255.250:1900\r\n" +
        "ST: upnp:rootdevice\r\n" +
        "MAN: \"ssdp:discover\"\r\n" +
        "MX: 3\r\n\r\n";

    public static async Task<bool> ForwardPort(int port, string description)
    {
        try
        {
            Console.WriteLine($"[UPnP] Attempting to forward port {port}...");
            
            // 1. Discovery
            string? controlUrl = await DiscoverControlUrl();
            if (string.IsNullOrEmpty(controlUrl))
            {
                Console.WriteLine("[UPnP] Could not discover a UPnP-capable router on this network.");
                return false;
            }

            // 2. Get Local IP
            string? localIp = GetLocalIpAddress();
            if (localIp == null) return false;

            // 3. Send AddPortMapping Request
            bool success = await SendPortMappingRequest(controlUrl, port, localIp, description);
            
            if (success) Console.WriteLine($"[UPnP] Success! Port {port} is now forwarded to {localIp}.");
            else Console.WriteLine("[UPnP] Router rejected the port mapping request.");

            return success;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[UPnP] Error during port forwarding: {ex.Message}");
            return false;
        }
    }

    private static async Task<string?> DiscoverControlUrl()
    {
        using var udp = new UdpClient();
        udp.Client.ReceiveTimeout = 3000;
        byte[] data = Encoding.UTF8.GetBytes(SSDP_QUERY);
        
        await udp.SendAsync(data, data.Length, "239.255.255.250", 1900);

        using var cts = new CancellationTokenSource(3000);
        try
        {
            var result = await udp.ReceiveAsync(cts.Token);
            string response = Encoding.UTF8.GetString(result.Buffer);
            
            string? location = response.Split("\r\n")
                .FirstOrDefault(x => x.StartsWith("LOCATION", StringComparison.OrdinalIgnoreCase))
                ?.Split(" ", 2)[1].Trim();

            if (string.IsNullOrEmpty(location)) return null;

            // Fetch the XML and find the control URL
            return await ParseControlUrlFromLocation(location);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[UPnP] Discovery error: {ex.Message}");
            return null;
        }
    }

    private static async Task<string?> ParseControlUrlFromLocation(string location)
    {
        using var client = new HttpClient();
        string xml = await client.GetStringAsync(location);
        var doc = XDocument.Parse(xml);
        XNamespace ns = doc.Root!.GetDefaultNamespace();

        // We look for WANIPConnection or WANPPPConnection
        var services = doc.Descendants(ns + "service");
        var targetService = services.FirstOrDefault(s => 
            s.Element(ns + "serviceType")?.Value.Contains("WANIPConnection") == true ||
            s.Element(ns + "serviceType")?.Value.Contains("WANPPPConnection") == true);

        string? relativeControlUrl = targetService?.Element(ns + "controlURL")?.Value;
        if (string.IsNullOrEmpty(relativeControlUrl)) return null;

        // Combine with base URL if relative
        if (relativeControlUrl.StartsWith("http")) return relativeControlUrl;
        
        var uri = new Uri(location);
        return $"{uri.Scheme}://{uri.Host}:{uri.Port}{ (relativeControlUrl.StartsWith("/") ? "" : "/") }{relativeControlUrl}";
    }

    private static async Task<bool> SendPortMappingRequest(string controlUrl, int port, string localIp, string description)
    {
        string soapBody = $@"<?xml version=""1.0""?>
<s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/"" s:encodingStyle=""http://schemas.xmlsoap.org/soap/encoding/"">
  <s:Body>
    <u:AddPortMapping xmlns:u=""urn:schemas-upnp-org:service:WANIPConnection:1"">
      <NewRemoteHost></NewRemoteHost>
      <NewExternalPort>{port}</NewExternalPort>
      <NewProtocol>TCP</NewProtocol>
      <NewInternalPort>{port}</NewInternalPort>
      <NewInternalClient>{localIp}</NewInternalClient>
      <NewEnabled>1</NewEnabled>
      <NewPortMappingDescription>{description}</NewPortMappingDescription>
      <NewLeaseDuration>0</NewLeaseDuration>
    </u:AddPortMapping>
  </s:Body>
</s:Envelope>";

        using var client = new HttpClient();
        var content = new StringContent(soapBody, Encoding.UTF8, "text/xml");
        client.DefaultRequestHeaders.Add("SOAPACTION", "\"urn:schemas-upnp-org:service:WANIPConnection:1#AddPortMapping\"");

        var response = await client.PostAsync(controlUrl, content);
        return response.IsSuccessStatusCode;
    }

    private static string? GetLocalIpAddress()
    {
        var host = Dns.GetHostEntry(Dns.GetHostName());
        return host.AddressList.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork)?.ToString();
    }
}

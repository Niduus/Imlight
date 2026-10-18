/*
 * Imlight
 * Copyright (C) 2025 Revive101
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Imlight.Setup;

/// <summary>What "Let friends connect" did.</summary>
internal sealed record FriendAccessResult(string? PublicIp, string? HomeIp, bool Firewall, string FirewallNote,
    bool Router, string RouterNote, bool SharedAddress, IReadOnlyList<int> Ports);

/// <summary>
/// Lets people on other computers join this server: allows its ports through Windows Firewall and asks the
/// router to send them to this computer (UPnP), and finds the address to give friends.
/// </summary>
internal static class FriendAccess {

    private const string FIREWALL_RULE = "Imlight server";
    private const string MARK_FILE = "friends-can-connect.txt";

    private static readonly string[] PUBLIC_IP_SERVICES = [
        "https://api.ipify.org/", "https://icanhazip.com/", "https://checkip.amazonaws.com/",
    ];

    private static readonly string[] ROUTER_SEARCHES = [
        "urn:schemas-upnp-org:service:WANIPConnection:1", "urn:schemas-upnp-org:service:WANIPConnection:2",
        "urn:schemas-upnp-org:service:WANPPPConnection:1", "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
    ];

    /// <summary>
    /// The ports players connect to: the login port and one game port per realm.
    /// </summary>
    internal static List<int> Ports(string installFolder) {
        var config = InstalledCommands.ConfigFile(installFolder);
        int Number(string section, string key, int fallback)
            => int.TryParse(IniFile.Get(config, section, key), out var value) ? value : fallback;

        var login = Number("Login Server", "LoginServerPort", 12000);
        var game = Number("Game Server", "GameServerPort", 12333);
        var realms = Math.Max(1, Math.Max(Number("Game Server", "GameServerCount", 3), Number("Game Server", "MaxGameServersAllowed", 3)));

        return new[] { login }.Concat(Enumerable.Range(game, realms)).Distinct().ToList();
    }

    /// <summary>True once "Let friends connect" has worked, so the server start asks the router again.</summary>
    internal static bool WasTurnedOn(string installFolder) => File.Exists(Path.Combine(installFolder, MARK_FILE));

    internal static async Task<string?> PublicIpAsync() {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        foreach (var service in PUBLIC_IP_SERVICES) {
            try {
                var text = (await http.GetStringAsync(service)).Trim();
                if (IPAddress.TryParse(text, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork) {
                    return text;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) {
                // Try the next one.
            }
        }

        return null;
    }

    /// <summary>
    /// Does everything friends need on this computer's side. Windows asks once to allow the firewall change.
    /// </summary>
    internal static async Task<FriendAccessResult> TurnOnAsync(string installFolder) {
        var ports = Ports(installFolder);
        var (firewall, firewallNote) = await AllowThroughFirewallAsync(ports);
        var router = await OpenRouterAsync(ports);
        var publicIp = await PublicIpAsync();
        var shared = router.ExternalIp is { } external && (IsShared(external) || (publicIp is not null && external != publicIp));
        if (router.Ok) {
            await File.WriteAllTextAsync(Path.Combine(installFolder, MARK_FILE),
                "The app asks the router to open the server's ports each time the server starts. Delete this file to stop that.\n");
        }

        return new FriendAccessResult(publicIp, router.HomeIp, firewall, firewallNote, router.Ok, router.Note, shared, ports);
    }

    /// <summary>
    /// Asks the router again, quietly. Routers forget when they restart, and this computer's home address can change.
    /// </summary>
    internal static async Task RefreshRouterAsync(string installFolder) {
        try {
            await OpenRouterAsync(Ports(installFolder));
        }
        catch {
            // Best effort only.
        }
    }

    // ---------------------------------------------------------------------
    // Windows Firewall
    // ---------------------------------------------------------------------

    private static async Task<(bool Ok, string Note)> AllowThroughFirewallAsync(List<int> ports) {
        if (!OperatingSystem.IsWindows()) {
            return (true, "Not needed on this computer.");
        }

        var list = string.Join(",", ports);
        var script = $"netsh advfirewall firewall delete rule name=\"{FIREWALL_RULE}\" >nul 2>&1 & " +
                     $"netsh advfirewall firewall add rule name=\"{FIREWALL_RULE}\" dir=in action=allow protocol=TCP localport={list} profile=any";
        var startInfo = new ProcessStartInfo("cmd.exe", $"/c {script}") {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try {
            using var process = Process.Start(startInfo);
            if (process is null) {
                return (false, "Windows did not run the firewall change.");
            }

            await process.WaitForExitAsync();

            return process.ExitCode == 0
                ? (true, $"Windows Firewall now lets players in on ports {list}.")
                : (false, "Windows Firewall did not take the change.");
        }
        catch (Win32Exception) {
            return (false, "Windows Firewall was not changed, because Windows asked for permission and it was not given. Click the button again and choose Yes.");
        }
    }

    // ---------------------------------------------------------------------
    // Router (UPnP)
    // ---------------------------------------------------------------------

    private sealed record RouterResult(bool Ok, string Note, string? HomeIp, string? ExternalIp);

    private static async Task<RouterResult> OpenRouterAsync(List<int> ports) {
        var routers = await FindRoutersAsync();
        if (routers.Count == 0) {
            return new RouterResult(false, "The router did not answer. It may have UPnP turned off, so its ports have to be opened by hand.", null, null);
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        var problems = new List<string>();
        foreach (var (control, service) in routers) {
            var homeIp = HomeAddressTowards(control.Host);
            if (homeIp is null) {
                continue;
            }

            string? external = null;
            try {
                var reply = await SoapAsync(http, control, service, "GetExternalIPAddress", "");
                external = reply.Descendants().FirstOrDefault(e => e.Name.LocalName == "NewExternalIPAddress")?.Value;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Xml.XmlException) {
                // Some routers only answer the mapping requests.
            }

            var opened = new List<int>();
            foreach (var port in ports) {
                if (await MapAsync(http, control, service, port, homeIp, 0) || await MapAsync(http, control, service, port, homeIp, 604800)) {
                    opened.Add(port);
                }
            }

            if (opened.Count == ports.Count) {
                return new RouterResult(true, $"The router now sends ports {string.Join(", ", ports)} to this computer ({homeIp}).", homeIp, external);
            }

            problems.Add(opened.Count == 0
                ? "The router refused to open the ports."
                : $"The router opened only ports {string.Join(", ", opened)}.");
            if (routers.Count == 1) {
                return new RouterResult(false, problems[0] + " They have to be opened by hand.", homeIp, external);
            }
        }

        return new RouterResult(false, (problems.FirstOrDefault() ?? "The router did not answer.") + " They have to be opened by hand.", null, null);
    }

    private static async Task<bool> MapAsync(HttpClient http, Uri control, string service, int port, string homeIp, int lease) {
        var args = "<NewRemoteHost></NewRemoteHost>" +
                   $"<NewExternalPort>{port}</NewExternalPort><NewProtocol>TCP</NewProtocol>" +
                   $"<NewInternalPort>{port}</NewInternalPort><NewInternalClient>{homeIp}</NewInternalClient>" +
                   "<NewEnabled>1</NewEnabled><NewPortMappingDescription>Imlight</NewPortMappingDescription>" +
                   $"<NewLeaseDuration>{lease}</NewLeaseDuration>";
        try {
            await SoapAsync(http, control, service, "AddPortMapping", args);

            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Xml.XmlException) {
            return false;
        }
    }

    private static async Task<XDocument> SoapAsync(HttpClient http, Uri control, string service, string action, string args) {
        var body = "<?xml version=\"1.0\"?>" +
                   "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
                   $"<s:Body><u:{action} xmlns:u=\"{service}\">{args}</u:{action}></s:Body></s:Envelope>";
        using var request = new HttpRequestMessage(HttpMethod.Post, control) {
            Content = new StringContent(body, Encoding.UTF8, "text/xml"),
        };
        request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{service}#{action}\"");
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) {
            throw new HttpRequestException($"{action} failed: {(int) response.StatusCode}");
        }

        return XDocument.Parse(text);
    }

    /// <summary>
    /// Finds the router's port opening service on the home network, as (control address, service type) pairs.
    /// </summary>
    private static async Task<List<(Uri Control, string Service)>> FindRoutersAsync() {
        var locations = new HashSet<string>();
        using (var udp = new UdpClient(AddressFamily.InterNetwork)) {
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            var target = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
            foreach (var search in ROUTER_SEARCHES) {
                var message = Encoding.ASCII.GetBytes(
                    "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\n" +
                    $"ST: {search}\r\n\r\n");
                await udp.SendAsync(message, message.Length, target);
            }

            var until = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < until) {
                var receive = udp.ReceiveAsync();
                if (await Task.WhenAny(receive, Task.Delay(until - DateTime.UtcNow)) != receive) {
                    break;
                }

                var reply = Encoding.ASCII.GetString((await receive).Buffer);
                var location = reply.Split("\r\n")
                    .FirstOrDefault(l => l.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase))?[9..].Trim();
                if (location is not null) {
                    locations.Add(location);
                }
            }
        }

        var found = new List<(Uri, string)>();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        foreach (var location in locations) {
            try {
                var description = XDocument.Parse(await http.GetStringAsync(location));
                var baseText = description.Descendants().FirstOrDefault(e => e.Name.LocalName == "URLBase")?.Value;
                var baseUri = Uri.TryCreate(baseText, UriKind.Absolute, out var b) ? b : new Uri(location);
                foreach (var service in description.Descendants().Where(e => e.Name.LocalName == "service")) {
                    var type = service.Elements().FirstOrDefault(e => e.Name.LocalName == "serviceType")?.Value ?? "";
                    var control = service.Elements().FirstOrDefault(e => e.Name.LocalName == "controlURL")?.Value;
                    if ((type.Contains(":WANIPConnection:") || type.Contains(":WANPPPConnection:")) && control is not null
                        && Uri.TryCreate(baseUri, control, out var controlUri)) {
                        found.Add((controlUri, type));
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Xml.XmlException or UriFormatException) {
                // Not a router, or not answering.
            }
        }

        return found.Distinct().ToList();
    }

    /// <summary>This computer's home network address on the way to the router.</summary>
    private static string? HomeAddressTowards(string routerHost) {
        try {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(routerHost, 1900);

            return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString();
        }
        catch (SocketException) {
            return null;
        }
    }

    /// <summary>
    /// True for an address the internet company shares between homes (or a second router in the way),
    /// which friends on the internet cannot reach.
    /// </summary>
    private static bool IsShared(string address) {
        if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) {
            return false;
        }

        var b = ip.GetAddressBytes();

        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] is >= 64 and <= 127);
    }

}

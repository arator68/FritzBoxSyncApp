using DnsServerCore.ApplicationCommon;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace FritzBoxSync;

public sealed class App : IDnsApplication
{
    private readonly object _sync = new();
    private IDnsServer? _dnsServer;
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private Config? _config;

    public string Description =>
        "Synchronizes Technitium DHCP reservation hostnames to FRITZ!Box FriendlyNames by MAC address.";

    public Task InitializeAsync(IDnsServer dnsServer, string? config)
    {
        _dnsServer = dnsServer;

        if (string.IsNullOrWhiteSpace(config))
            throw new InvalidOperationException("FRITZ!Box Sync app config is missing.");

        _config = JsonSerializer.Deserialize<Config>(config, JsonOptions)
            ?? throw new InvalidOperationException("Invalid FRITZ!Box Sync app config.");

        if (_config.IntervalMinutes < 1)
            _config.IntervalMinutes = 15;

        Log("App initialisiert.");

        return StartAsync();
    }

    public Task StartAsync()
    {
        Log("App wird gestartet.");

        if (!_config!.Enabled)
        {
            Log("FRITZ!Box Sync ist deaktiviert.");
            return Task.CompletedTask;
        }

        _cts = new CancellationTokenSource();
        _worker = Task.Run(() => WorkerAsync(_cts.Token));

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        StopWorker();
    }

    private async Task WorkerAsync(CancellationToken cancellationToken)
    {
        if (_config!.RunOnStartup)
        {
            await SafeSyncAsync(cancellationToken);
        }

        using PeriodicTimer timer =
            new(TimeSpan.FromMinutes(_config.IntervalMinutes));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await SafeSyncAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SafeSyncAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SynchronizeAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log("Synchronisation fehlgeschlagen: " + ex);
        }
    }

    private async Task SynchronizeAsync(CancellationToken cancellationToken)
    {
        Config cfg = _config!;

        Log("============================================");
        Log("FRITZ!Box Sync - Synchronisation gestartet");
        Log("============================================");

        using HttpClient techClient = CreateHttpClient();

        Log($"Technitium API: {cfg.TechnitiumApiUrl}");

        List<ReservedLease> reservations =
            await GetTechnitiumReservationsAsync(
                techClient,
                cfg,
                cancellationToken);

        Log($"Technitium: {reservations.Count} reservierte Geräte.");

        using HttpClient fritzClient = CreateFritzClient(cfg);

        string hostListPath =
            await GetHostListPathAsync(
                fritzClient,
                cfg,
                cancellationToken);

        string hostListUrl =
            BuildHostListUrl(cfg, hostListPath);

        string xml =
            await fritzClient.GetStringAsync(
                hostListUrl,
                cancellationToken);

        List<FritzDevice> fritzDevices =
            ParseFritzHostList(xml);

        Log($"FRITZ!Box: {fritzDevices.Count} Geräte.");

        Dictionary<string, FritzDevice> byMac =
            fritzDevices
                .Where(x => !string.IsNullOrWhiteSpace(x.Mac))
                .GroupBy(x => NormalizeMac(x.Mac))
                .ToDictionary(
                    x => x.Key,
                    x => x.First());

        int ok = 0;
        int changes = 0;
        int notFound = 0;
        int errors = 0;

        foreach (ReservedLease lease in reservations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string mac = NormalizeMac(lease.HardwareAddress);

            if (!byMac.TryGetValue(mac, out FritzDevice? fritz))
            {
                notFound++;

                Log(
                    $"NICHT GEFUNDEN: {lease.HostName} [{mac}] IP {lease.Address}");

                continue;
            }

            string currentName = fritz.FriendlyName ?? "";
            string desiredName = lease.HostName ?? "";

            if (string.Equals(
                    currentName,
                    desiredName,
                    StringComparison.Ordinal))
            {
                ok++;
                continue;
            }

            Log("ÄNDERUNG:");
            Log($"  IP            : {lease.Address}");
            Log($"  MAC           : {FormatMac(mac)}");
            Log($"  FRITZ HostName: {fritz.HostName}");
            Log($"  FRITZ Friendly: {currentName}");
            Log($"  Technitium    : {desiredName}");

            if (cfg.DryRun)
            {
                Log("  -> TESTMODUS: keine Änderung");

                changes++;
                continue;
            }

            try
            {
                await SetFriendlyNameByMacAsync(
                    fritzClient,
                    cfg,
                    mac,
                    desiredName,
                    cancellationToken);

                Log("  -> erfolgreich geändert.");

                changes++;
            }
            catch (Exception ex)
            {
                errors++;

                Log(
                    "  -> FEHLER beim Ändern: " +
                    ex.Message);
            }
        }

        Log("============================================");
        Log($"Reservierungen : {reservations.Count}");
        Log($"FRITZ Geräte   : {fritzDevices.Count}");
        Log($"OK             : {ok}");
        Log($"Änderungen     : {changes}");
        Log($"Nicht gefunden : {notFound}");
        Log($"Fehler         : {errors}");

        if (cfg.DryRun)
            Log(
                "TESTMODUS aktiv - es wurden KEINE Änderungen vorgenommen.");

        Log("============================================");
    }

    private static async Task<List<ReservedLease>>
        GetTechnitiumReservationsAsync(
            HttpClient client,
            Config cfg,
            CancellationToken cancellationToken)
    {
        string url =
            $"{cfg.TechnitiumApiUrl.TrimEnd('/')}/api/dhcp/leases/list";

        using HttpRequestMessage request =
            new(HttpMethod.Get, url);

        request.Headers.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                cfg.TechnitiumApiToken);

        try
        {
            using HttpResponseMessage response =
    await client.SendAsync(
        request,
        cancellationToken);

            string responseText =
                await response.Content.ReadAsStringAsync(
                    cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Technitium API returned " +
                    $"{(int)response.StatusCode} " +
                    $"{response.StatusCode}: " +
                    $"{responseText}");
            }

            using JsonDocument doc =
                JsonDocument.Parse(responseText);

            JsonElement leases =
                doc.RootElement
                    .GetProperty("response")
                    .GetProperty("leases");

            List<ReservedLease> result = new();

            foreach (JsonElement lease in leases.EnumerateArray())
            {
                string? type =
                    GetString(lease, "type");

                if (!string.Equals(
                        type,
                        "Reserved",
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                string? mac =
                    GetString(lease, "hardwareAddress");

                string? ip =
                    GetString(lease, "address");

                string? host =
                    GetString(lease, "hostName");

                if (string.IsNullOrWhiteSpace(mac) ||
                    string.IsNullOrWhiteSpace(ip) ||
                    string.IsNullOrWhiteSpace(host))
                    continue;

                result.Add(
                    new ReservedLease(
                        mac,
                        ip,
                        host));
            }

            return result;
        }
        catch (HttpRequestException ex)
        {
            throw new HttpRequestException(
                $"Technitium API Zugriff fehlgeschlagen. " +
                $"URL: {url}. " +
                $"Fehler: {ex.Message}",
                ex);
        }
    }

    private static async Task<string> GetHostListPathAsync(
        HttpClient client,
        Config cfg,
        CancellationToken cancellationToken)
    {
        const string action =
            "X_AVM-DE_GetHostListPath";

        string soap =
            SoapEnvelope($"""
            <u:{action} xmlns:u="urn:dslforum-org:service:Hosts:1"></u:{action}>
            """);

        using HttpRequestMessage request =
            CreateSoapRequest(
                $"{cfg.FritzBoxUrl.TrimEnd('/')}/upnp/control/hosts",
                action,
                soap);

        using HttpResponseMessage response =
            await client.SendAsync(
                request,
                cancellationToken);

        string body =
            await response.Content.ReadAsStringAsync(
                cancellationToken);

        response.EnsureSuccessStatusCode();

        XDocument doc =
            XDocument.Parse(body);

        XElement? value =
            doc.Descendants()
                .FirstOrDefault(
                    x =>
                        x.Name.LocalName ==
                        "NewX_AVM-DE_HostListPath");

        if (value is null ||
            string.IsNullOrWhiteSpace(value.Value))
        {
            throw new InvalidOperationException(
                "FRITZ!Box lieferte keinen HostList-Pfad.");
        }

        return value.Value.Trim();
    }

    private static async Task SetFriendlyNameByMacAsync(
        HttpClient client,
        Config cfg,
        string normalizedMac,
        string friendlyName,
        CancellationToken cancellationToken)
    {
        string action =
            "X_AVM-DE_SetFriendlyNameByMAC";

        string mac =
            FormatMac(normalizedMac);

        string escapedName =
            SecurityElementEscape(friendlyName);

        string soap =
            SoapEnvelope($"""
            <u:{action} xmlns:u="urn:dslforum-org:service:Hosts:1">
              <NewMACAddress>{mac}</NewMACAddress>
              <NewX_AVM-DE_FriendlyName>{escapedName}</NewX_AVM-DE_FriendlyName>
            </u:{action}>
            """);

        using HttpRequestMessage request =
            CreateSoapRequest(
                $"{cfg.FritzBoxUrl.TrimEnd('/')}/upnp/control/hosts",
                action,
                soap);

        using HttpResponseMessage response =
            await client.SendAsync(
                request,
                cancellationToken);

        string body =
            await response.Content.ReadAsStringAsync(
                cancellationToken);

        response.EnsureSuccessStatusCode();

        if (body.Contains(
                "Fault",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "FRITZ!Box SOAP Fault: " + body);
        }
    }

    private static List<FritzDevice> ParseFritzHostList(
        string xml)
    {
        XDocument doc =
            XDocument.Parse(xml);

        List<FritzDevice> result = new();

        foreach (XElement macNode in
                 doc.Descendants()
                    .Where(
                        x =>
                            x.Name.LocalName ==
                            "MACAddress"))
        {
            XElement? record =
                FindRecord(macNode);

            if (record is null)
                continue;

            string? mac =
                ChildValue(record, "MACAddress");

            string? ip =
                ChildValue(record, "IPAddress");

            string? host =
                ChildValue(record, "HostName");

            string? friendly =
                ChildValue(
                    record,
                    "X_AVM-DE_FriendlyName");

            if (string.IsNullOrWhiteSpace(mac))
                continue;

            result.Add(
                new FritzDevice(
                    mac,
                    ip,
                    host,
                    friendly));
        }

        return result;
    }

    private static XElement? FindRecord(
        XElement macNode)
    {
        XElement? current =
            macNode.Parent;

        while (current is not null)
        {
            bool hasIp =
                current.Descendants()
                    .Any(
                        x =>
                            x.Name.LocalName ==
                            "IPAddress");

            bool hasHost =
                current.Descendants()
                    .Any(
                        x =>
                            x.Name.LocalName ==
                            "HostName");

            bool hasFriendly =
                current.Descendants()
                    .Any(
                        x =>
                            x.Name.LocalName ==
                            "X_AVM-DE_FriendlyName");

            if (hasIp ||
                hasHost ||
                hasFriendly)
            {
                return current;
            }

            current =
                current.Parent;
        }

        return macNode.Parent;
    }

    private static string? ChildValue(
        XElement element,
        string localName)
    {
        return element.Descendants()
            .FirstOrDefault(
                x =>
                    x.Name.LocalName ==
                    localName)
            ?.Value
            ?.Trim();
    }

    private static HttpClient CreateHttpClient()
    {
        SocketsHttpHandler handler = new()
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };

        HttpClient client =
            new(handler);

        client.Timeout =
            TimeSpan.FromSeconds(30);

        return client;
    }

    private static HttpClient CreateFritzClient(
    Config cfg)
{
    HttpClientHandler handler = new()
    {
        ServerCertificateCustomValidationCallback =
            HttpClientHandler
                .DangerousAcceptAnyServerCertificateValidator,

        Credentials = new NetworkCredential(
            cfg.FritzUsername,
            cfg.FritzPassword),

        PreAuthenticate = false
    };

    HttpClient client =
        new(handler);

    return client;
}

    private static string BuildHostListUrl(
        Config cfg,
        string path)
    {
        if (path.StartsWith(
                "http://",
                StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(
                "https://",
                StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        return
            $"{cfg.FritzBoxHttpsUrl.TrimEnd('/')}/" +
            $"{path.TrimStart('/')}";
    }

    private static HttpRequestMessage CreateSoapRequest(
        string url,
        string action,
        string body)
    {
        HttpRequestMessage request =
            new(HttpMethod.Post, url);

        request.Headers.TryAddWithoutValidation(
            "SOAPAction",
            $"\"urn:dslforum-org:service:Hosts:1#{action}\"");

        request.Content =
            new StringContent(
                body,
                Encoding.UTF8,
                "text/xml");

        return request;
    }

    private static string SoapEnvelope(
        string body)
    {
        return $"""
        <?xml version="1.0" encoding="utf-8"?>
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"
                    s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
          <s:Body>
            {body}
          </s:Body>
        </s:Envelope>
        """;
    }

    private static string NormalizeMac(
        string mac)
    {
        return new string(
            mac
                .Where(char.IsLetterOrDigit)
                .ToArray())
            .ToUpperInvariant();
    }

    private static string FormatMac(
        string normalized)
    {
        normalized =
            NormalizeMac(normalized);

        if (normalized.Length != 12)
        {
            throw new FormatException(
                $"Ungültige MAC-Adresse: {normalized}");
        }

        return string.Join(
            ":",
            Enumerable.Range(0, 6)
                .Select(
                    i =>
                        normalized.Substring(
                            i * 2,
                            2)));
    }

    private static string? GetString(
        JsonElement element,
        string property)
    {
        if (!element.TryGetProperty(
                property,
                out JsonElement value))
        {
            return null;
        }

        return value.ValueKind ==
               JsonValueKind.Null
            ? null
            : value.ToString();
    }

    private static string SecurityElementEscape(
        string value)
    {
        return value
            .Replace(
                "&",
                "&amp;",
                StringComparison.Ordinal)
            .Replace(
                "<",
                "&lt;",
                StringComparison.Ordinal)
            .Replace(
                ">",
                "&gt;",
                StringComparison.Ordinal)
            .Replace(
                "\"",
                "&quot;",
                StringComparison.Ordinal)
            .Replace(
                "'",
                "&apos;",
                StringComparison.Ordinal);
    }

    private void Log(
        string message)
    {
        _dnsServer?.WriteLog(
            "[FRITZ!Box Sync] " + message);
    }

    private void StopWorker()
    {
        lock (_sync)
        {
            if (_cts is null)
                return;

            try
            {
                _cts.Cancel();
            }
            catch
            {
            }

            _cts.Dispose();
            _cts = null;
            _worker = null;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            PropertyNameCaseInsensitive = true
        };

    private sealed class Config
    {
        public bool Enabled { get; set; } = true;

        public bool DryRun { get; set; } = true;

        public bool RunOnStartup { get; set; } = true;

        public int IntervalMinutes { get; set; } = 15;

        public string FritzBoxUrl { get; set; } =
            "http://192.168.178.1:49000";

        public string FritzBoxHttpsUrl { get; set; } =
            "https://192.168.178.1:49443";

        public string FritzUsername { get; set; } =
            "TechnitiumSync";

        public string FritzPassword { get; set; } = "";

        public string TechnitiumApiUrl { get; set; } =
            "http://192.168.178.2:5380";

        public string TechnitiumApiToken { get; set; } = "";
    }

    private sealed record ReservedLease(
        string HardwareAddress,
        string Address,
        string HostName);

    private sealed record FritzDevice(
        string Mac,
        string? IpAddress,
        string? HostName,
        string? FriendlyName);
}
using DnsServerCore.ApplicationCommon;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
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

    public async Task InitializeAsync(
    IDnsServer dnsServer,
    string? config)
{
    _dnsServer = dnsServer;

    if (string.IsNullOrWhiteSpace(config))
        throw new InvalidOperationException(
            "FRITZ!Box Sync app config is missing.");

    _config = JsonSerializer.Deserialize<Config>(
        config,
        JsonOptions)
        ?? throw new InvalidOperationException(
            "Invalid FRITZ!Box Sync app config.");

    bool configChanged = false;

    if (_config.IntervalMinutes < 1)
    {
        _config.IntervalMinutes = 15;
        configChanged = true;
    }

    if (string.IsNullOrWhiteSpace(_config.FritzBoxWebUrl))
    {
        _config.FritzBoxWebUrl =
            _config.FritzBoxUrl
                .Replace(
                    ":49000",
                    "",
                    StringComparison.OrdinalIgnoreCase)
                .TrimEnd('/');

        configChanged = true;

        Log(
            $"FritzBoxWebUrl complemented: {_config.FritzBoxWebUrl}");
    }

    if (configChanged)
{
    _ = Task.Run(async () =>
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15));

            await SaveAppConfigAsync();
        }
        catch (Exception ex)
{
    Log(
        $"Error saving app configuration: {ex}");
}
    });
}

    Log("App initialized.");

    await StartAsync();
}

private async Task SaveAppConfigAsync()
{
    if (string.IsNullOrWhiteSpace(_config?.TechnitiumApiUrl))
    {
        Log("TechnitiumApiUrl is not configured..");
        return;
    }

    if (string.IsNullOrWhiteSpace(_config.TechnitiumApiToken))
    {
        Log("TechnitiumApiToken is not configured..");
        return;
    }

    JsonSerializerOptions saveOptions = new(JsonOptions)
{
    WriteIndented = true
};

string appConfig =
    JsonSerializer.Serialize(_config, saveOptions);

    string apiUrl =
        $"{_config.TechnitiumApiUrl.TrimEnd('/')}/api/apps/config/set";

    using HttpClient httpClient = CreateHttpClient();

    using FormUrlEncodedContent content =
        new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["token"] = _config.TechnitiumApiToken,
                ["name"] = "FritzboxSync",
                ["config"] = appConfig
            });

    using HttpResponseMessage response =
        await httpClient.PostAsync(apiUrl, content);

    string responseBody =
        await response.Content.ReadAsStringAsync();

    if (!response.IsSuccessStatusCode)
    {
        throw new InvalidOperationException(
            $"Technitium App-Config could not be saved.. " +
            $"HTTP {(int)response.StatusCode}: {responseBody}");
    }

    Log("App configuration successfully saved via the Technitium API..");
}
    public Task StartAsync()
    {
        Log("App is starting.");

        if (!_config!.Enabled)
        {
            Log("FRITZ!Box Sync is disabled..");
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
            Log("Synchronization failed: " + ex);
        }
    }

    private async Task SynchronizeAsync(CancellationToken cancellationToken)
    {
        Config cfg = _config!;

        Log("============================================");
        Log("FRITZ!Box Sync - Synchronization started");
        Log("============================================");

        using HttpClient techClient = CreateHttpClient();

        Log($"Technitium API: {cfg.TechnitiumApiUrl}");

        List<ReservedLease> reservations =
            await GetTechnitiumReservationsAsync(
                techClient,
                cfg,
                cancellationToken);

        Log($"Technitium: {reservations.Count} reserved devices.");

        using HttpClient fritzClient =
            FritzBoxClient.CreateHttpClient(
                cfg.FritzUsername,
                cfg.FritzPassword);

        FritzBoxClient fritzBox =
            new(
                fritzClient,
                cfg);

        List<FritzBoxClient.FritzDevice> fritzDevices =
    await fritzBox.GetHostListAsync(
        cancellationToken);

        

        Log($"FRITZ!Box: {fritzDevices.Count} Devices.");

    Dictionary<string, FritzBoxClient.FritzDevice> byMac =
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

            if (!byMac.TryGetValue(
                mac,
                out FritzBoxClient.FritzDevice? fritz))
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
                Log("  -> TEST MODE: no change");

                changes++;
                continue;
            }

            try
            {
                await fritzBox.SetFriendlyNameByMacAsync(
                    mac,
                    desiredName,
                    cancellationToken);

                Log("  -> successfully changed.");

                changes++;
            }
            catch (Exception ex)
            {
                errors++;

                Log(
                    "  -> Error while modifying: " +
                    ex.Message);
            }
        }

        Log("============================================");
        Log($"Reservations     : {reservations.Count}");
        Log($"FRITZ! devices   : {fritzDevices.Count}");
        Log($"OK               : {ok}");
        Log($"Changes          : {changes}");
        Log($"Not found        : {notFound}");
        Log($"Error            : {errors}");

        if (cfg.EnableIpv6Sync)
        {
            await SynchronizeIpv6Async(
                fritzBox,
                fritzDevices,
                reservations,
                cfg,
                cancellationToken);
        }
        else
        {
            Log("IPv6 synchronization is disabled.");
        }

        if (cfg.DryRun)
            Log(
                "TEST MODE active – NO changes were made.");

        Log("============================================");
    }

   private async Task SynchronizeIpv6Async(
    FritzBoxClient fritzBox,
    List<FritzBoxClient.FritzDevice> fritzDevices,
    List<ReservedLease> reservations,
    Config cfg,
    CancellationToken cancellationToken)
{
    Log("--------------------------------------------");
    Log("IPv6 synchronization started");

    if (string.IsNullOrWhiteSpace(cfg.TechnitiumApiToken))
    {
        Log("IPv6: No Technitium API token configured.");
        return;
    }

    try
    {
        Log("IPv6: FRITZ!Box SID is being retrieved...");

        string sid =
            await fritzBox.GetSidAsync(
                cancellationToken);

        Log("IPv6: FRITZ!Box SID successfully receivedn.");

        Log("IPv6: FRITZ!Box LAN devices are being retrieved....");

        List<FritzBoxClient.FritzLanDevice> lanDevices =
            await fritzBox.GetLanDevicesAsync(
                sid,
                cancellationToken);

        Log(
            $"IPv6: FRITZ!Box LAN devices successfully read: " +
            $"{lanDevices.Count}");

        Dictionary<string, FritzBoxClient.FritzLanDevice>
            lanDevicesByMac =
                lanDevices
                    .Where(x =>
                        !string.IsNullOrWhiteSpace(x.Mac))
                    .GroupBy(x =>
                        NormalizeMac(x.Mac!))
                    .ToDictionary(
                        x => x.Key,
                        x => x.First());

        using HttpClient techClient =
            CreateHttpClient();

        Log(
            "IPv6: Technitium DNS records are being retrieved...");

        List<TechnitiumRecord> records =
            await GetTechnitiumRecordsAsync(
                techClient,
                cfg,
                cancellationToken);

        Log(
            $"IPv6: Technitium DNS records successfully read: " +
            $"{records.Count}");

        int stableDevices = 0;
        int alreadyPresent = 0;
        int added = 0;
        int updated = 0;
        int deleted = 0;
        int aaaaChecked = 0;
        int noStableIpv6 = 0;
        int errors = 0;

        foreach (ReservedLease lease in reservations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string mac =
                NormalizeMac(lease.HardwareAddress);

            if (!lanDevicesByMac.TryGetValue(
                    mac,
                    out FritzBoxClient.FritzLanDevice? device))
            {
                continue;
            }

            List<string> desiredIpv6 =
                GetStableIpv6(device);

            if (desiredIpv6.Count == 0)
            {
                noStableIpv6++;

                Log(
                    $"IPv6: {lease.HostName} " +
                    $"does not have a stable IPv6 address..");

                continue;
            }

            stableDevices++;

            string dnsName =
                NormalizeDnsName(
                    lease.HostName,
                    cfg.TechnitiumDnsZone);

            string desiredComment =
                $"FritzBoxSync - FRITZ!Box: {device.Name}";

            if (desiredComment.Length > 255)
            {
                desiredComment =
                    desiredComment.Substring(0, 255);
            }

            Log(
                $"IPv6 SYNC: {dnsName}");

            Log(
                $"  FRITZ!Box IPv6: {desiredIpv6.Count}");

            foreach (string ip in desiredIpv6)
            {
                Log(
                    $"    FRITZ -> {ip}");
            }

            /*
             * --------------------------------------------------------
             * EXISTIERENDE AAAA-RECORDS DIESES HOSTNAMENS
             * --------------------------------------------------------
             */

            List<TechnitiumRecord> existingRecords =
                records
                    .Where(x =>
                        string.Equals(
                            x.Type,
                            "AAAA",
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(
                            NormalizeDnsName(
                                x.Name,
                                cfg.TechnitiumDnsZone),
                            dnsName,
                            StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(
                            x.IpAddress))
                    .ToList();

            /*
             * --------------------------------------------------------
             * FRITZ!BOX = SOLL
             * TECHNITIUM = IST
             * --------------------------------------------------------
             */

            HashSet<string> desiredSet =
                desiredIpv6
                    .Select(NormalizeIpv6)
                    .ToHashSet(
                        StringComparer.OrdinalIgnoreCase);

            HashSet<string> existingSet =
                existingRecords
                    .Select(x =>
                        NormalizeIpv6(x.IpAddress!))
                    .ToHashSet(
                        StringComparer.OrdinalIgnoreCase);

            /*
             * --------------------------------------------------------
             * 1. NEUE IPv6-ADRESSEN
             * --------------------------------------------------------
             */

            foreach (string desiredIp in desiredSet)
            {
                cancellationToken.ThrowIfCancellationRequested();

                aaaaChecked++;

                if (existingSet.Contains(desiredIp))
                {
                    TechnitiumRecord existing =
                        existingRecords.First(x =>
                            string.Equals(
                                NormalizeIpv6(x.IpAddress!),
                                desiredIp,
                                StringComparison.OrdinalIgnoreCase));

                    bool commentCorrect =
                        string.Equals(
                            existing.Comments,
                            desiredComment,
                            StringComparison.Ordinal);

                    bool ttlCorrect =
                        existing.Ttl == cfg.Ipv6Ttl;

                    if (!commentCorrect ||
                        !ttlCorrect)
                    {
                        Log(
                            $"  -> AAAA UPDATE: {desiredIp}");

                        Log(
                            $"     Comment: " +
                            $"{existing.Comments} -> " +
                            $"{desiredComment}");

                        Log(
                            $"     TTL: " +
                            $"{existing.Ttl} -> " +
                            $"{cfg.Ipv6Ttl}");

                        if (cfg.DryRun)
                        {
                            Log(
                                "     -> TEST MODE: " +
                                "would be updated.");

                            updated++;
                        }
                        else
                        {
                            try
                            {
                                await UpdateTechnitiumAaaaAsync(
                                    techClient,
                                    cfg,
                                    dnsName,
                                    existing,
                                    desiredIp,
                                    desiredComment,
                                    cancellationToken);

                                int index =
                                    records.IndexOf(existing);

                                if (index >= 0)
                                {
                                    records[index] =
                                        existing with
                                        {
                                            IpAddress = desiredIp,
                                            Comments = desiredComment,
                                            Ttl = cfg.Ipv6Ttl
                                        };
                                }

                                updated++;
                            }
                            catch (Exception ex)
                            {
                                errors++;

                                Log(
                                    "     -> Error during update: " +
                                    ex.Message);
                            }
                        }
                    }
                    else
                    {
                        Log(
                            $"  -> AAAA already correct: {desiredIp}");

                        alreadyPresent++;
                    }

                    continue;
                }

                /*
                 * IPv6 fehlt in Technitium
                 */

                Log(
                    $"  -> AAAA MISSING: {desiredIp}");

                if (cfg.DryRun)
                {
                    Log(
                        "     -> TEST MODE: " +
                        "would be created.");

                    added++;
                }
                else
                {
                    try
                    {
                        await AddTechnitiumAaaaAsync(
                            techClient,
                            cfg,
                            dnsName,
                            desiredIp,
                            desiredComment,
                            cancellationToken);

                        records.Add(
                            new TechnitiumRecord(
                                dnsName,
                                "AAAA",
                                desiredIp,
                                desiredComment,
                                cfg.Ipv6Ttl));

                        added++;
                    }
                    catch (Exception ex)
                    {
                        errors++;

                        Log(
                            "     -> Error during ADD: " +
                            ex.Message);
                    }
                }
            }

            /*
             * --------------------------------------------------------
             * 2. ALTE IPv6-ADRESSEN LÖSCHEN
             *
             * Alles was in Technitium vorhanden ist,
             * aber von der FRITZ!Box nicht mehr geliefert wird.
             * --------------------------------------------------------
             */

            foreach (TechnitiumRecord existing
                     in existingRecords)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(
                        existing.IpAddress))
                {
                    continue;
                }

                aaaaChecked++;

                string existingIp =
                    NormalizeIpv6(
                        existing.IpAddress);

                if (desiredSet.Contains(existingIp))
                {
                    continue;
                }

                Log(
                    $"  -> AAAA VERALTET: {existingIp}");

                if (cfg.DryRun)
                {
                    Log(
                        "     -> Test Mode: " +
                        "would be deletedt.");

                    deleted++;
                }
                else
                {
                    try
                    {
                        await DeleteTechnitiumAaaaAsync(
                            techClient,
                            cfg,
                            dnsName,
                            existingIp,
                            cancellationToken);

                        records.Remove(existing);

                        deleted++;
                    }
                    catch (Exception ex)
                    {
                        errors++;

                        Log(
                            "     -> Error during DELETE: " +
                            ex.Message);
                    }
                }
            }
        }

        Log("--------------------------------------------");
        Log(
            $"IPv6 sturdy devices           : {stableDevices}");

        Log(
            $"IPv6 without a stable address : {noStableIpv6}");

        Log(
            $"AAAA already correct          : {alreadyPresent}");

        Log(
            $"AAAA checked overall          : {aaaaChecked}");

        Log(
            $"AAAA newly created            : {added}");

        Log(
            $"AAAA updated                  : {updated}");

        Log(
            $"AAAA deleted                  : {deleted}");

        Log(
            $"IPv6 Error                    : {errors}");

        if (cfg.DryRun)
        {
            Log(
                "IPv6 Test Mode: " +
                "NO changes were made..");
        }
    }
    catch (Exception ex)
    {
        Log(
            "IPv6 synchronization failed: " +
            ex.GetType().Name +
            ": " +
            ex.Message);
    }
}

    private static List<string> GetStableIpv6(
    FritzBoxClient.FritzLanDevice device)
{
    if (device.IpList is null)
        return new List<string>();

    return device.IpList
        .Where(x =>
            string.Equals(
                x.AddrType,
                "IPv6-GUA",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                x.AddrType,
                "IPv6-ULA",
                StringComparison.OrdinalIgnoreCase))
        .Select(x => NormalizeIpv6(x.Ip))
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}

private static string NormalizeIpv6(string ipAddress)
{
    if (string.IsNullOrWhiteSpace(ipAddress))
        return "";

    if (!IPAddress.TryParse(
            ipAddress,
            out IPAddress? address))
    {
        return ipAddress.Trim();
    }

    if (address.AddressFamily !=
        System.Net.Sockets.AddressFamily.InterNetworkV6)
    {
        return ipAddress.Trim();
    }

    return address.ToString();
}

    private static string CreateFritzPbkdf2Response(
        string challenge,
        string password)
    {
        string[] parts =
            challenge.Split('$');

        if (parts.Length != 5 ||
            parts[0] != "2")
        {
            throw new FormatException(
                "Invalid FRITZ!Box PBKDF2 challenge format.");
        }

        int iterations1 =
            int.Parse(parts[1]);

        string salt1Hex = parts[2];

        int iterations2 =
            int.Parse(parts[3]);

        string salt2Hex = parts[4];

        byte[] salt1 =
            Convert.FromHexString(salt1Hex);

        byte[] salt2 =
            Convert.FromHexString(salt2Hex);

        byte[] passwordBytes =
            Encoding.UTF8.GetBytes(password);

        byte[] hash1 =
            Rfc2898DeriveBytes.Pbkdf2(
                passwordBytes,
                salt1,
                iterations1,
                HashAlgorithmName.SHA256,
                32);

        byte[] hash2 =
            Rfc2898DeriveBytes.Pbkdf2(
                hash1,
                salt2,
                iterations2,
                HashAlgorithmName.SHA256,
                32);

        return
            Convert.ToHexString(salt2).ToLowerInvariant() +
            "$" +
            Convert.ToHexString(hash2).ToLowerInvariant();
    }

    

    

    private static async Task<List<TechnitiumRecord>>
    GetTechnitiumRecordsAsync(
        HttpClient client,
        Config cfg,
        CancellationToken cancellationToken)
{
    string zone =
        cfg.TechnitiumDnsZone.Trim().TrimEnd('.');

    string url =
        $"{cfg.TechnitiumApiUrl.TrimEnd('/')}" +
        "/api/zones/records/get" +
        $"?domain={Uri.EscapeDataString(zone)}" +
        $"&zone={Uri.EscapeDataString(zone)}" +
        "&listZone=true";

    using HttpRequestMessage request =
        new(HttpMethod.Get, url);

    request.Headers.Authorization =
        new AuthenticationHeaderValue(
            "Bearer",
            cfg.TechnitiumApiToken);

    using HttpResponseMessage response =
        await client.SendAsync(
            request,
            cancellationToken);

    string body =
        await response.Content.ReadAsStringAsync(
            cancellationToken);

    response.EnsureSuccessStatusCode();

    using JsonDocument doc =
        JsonDocument.Parse(body);

    if (!doc.RootElement.TryGetProperty(
            "response",
            out JsonElement responseElement) ||
        !responseElement.TryGetProperty(
            "records",
            out JsonElement records))
    {
        throw new InvalidOperationException(
            "Technitium API liefert keine records.");
    }

    List<TechnitiumRecord> result = new();

    foreach (JsonElement record in
             records.EnumerateArray())
    {
        string? name =
            GetString(record, "name");

        string? type =
            GetString(record, "type");

        if (string.IsNullOrWhiteSpace(name) ||
            string.IsNullOrWhiteSpace(type))
        {
            continue;
        }

        string? ipAddress = null;

        if (record.TryGetProperty(
                "rData",
                out JsonElement rData) &&
            rData.ValueKind == JsonValueKind.Object)
        {
            ipAddress =
                GetString(rData, "ipAddress");
        }

        string? comments =
            GetString(record, "comments");

        int ttl = 0;

        if (record.TryGetProperty(
                "ttl",
                out JsonElement ttlElement) &&
            ttlElement.ValueKind == JsonValueKind.Number)
        {
            ttlElement.TryGetInt32(out ttl);
        }

        result.Add(
            new TechnitiumRecord(
                name,
                type,
                ipAddress,
                comments,
                ttl));
    }

    return result;
}

   

private static bool IsIpv6Ula(string ipAddress)
{
    if (!IPAddress.TryParse(
            ipAddress,
            out IPAddress? address))
    {
        return false;
    }

    if (address.AddressFamily !=
        System.Net.Sockets.AddressFamily.InterNetworkV6)
    {
        return false;
    }

    byte firstByte =
        address.GetAddressBytes()[0];

    // IPv6 ULA: fc00::/7
    return (firstByte & 0xFE) == 0xFC;
}

    private async Task AddTechnitiumAaaaAsync(
    HttpClient client,
    Config cfg,
    string dnsName,
    string ipAddress,
    string comments,
    CancellationToken cancellationToken)
{
    string zone =
        cfg.TechnitiumDnsZone.Trim().TrimEnd('.');

    string url =
        $"{cfg.TechnitiumApiUrl.TrimEnd('/')}" +
        "/api/zones/records/add" +
        $"?domain={Uri.EscapeDataString(dnsName)}" +
        $"&zone={Uri.EscapeDataString(zone)}" +
        "&type=AAAA" +
        $"&ttl={cfg.Ipv6Ttl}" +
        $"&ipAddress={Uri.EscapeDataString(ipAddress)}" +
        $"&comments={Uri.EscapeDataString(comments)}";

    using HttpRequestMessage request =
        new(HttpMethod.Get, url);

    request.Headers.Authorization =
        new AuthenticationHeaderValue(
            "Bearer",
            cfg.TechnitiumApiToken);

    using HttpResponseMessage response =
        await client.SendAsync(
            request,
            cancellationToken);

    string body =
        await response.Content.ReadAsStringAsync(
            cancellationToken);

    if (!response.IsSuccessStatusCode)
    {
        throw new HttpRequestException(
            $"Technitium AAAA ADD API returned " +
            $"{(int)response.StatusCode} " +
            $"{response.StatusCode}: {body}");
    }

    using JsonDocument doc =
        JsonDocument.Parse(body);

    if (doc.RootElement.TryGetProperty(
            "status",
            out JsonElement statusElement))
    {
        string? status =
            statusElement.GetString();

        if (!string.Equals(
                status,
                "ok",
                StringComparison.OrdinalIgnoreCase))
        {
            string message = "";

            if (doc.RootElement.TryGetProperty(
                    "message",
                    out JsonElement messageElement))
            {
                message =
                    messageElement.GetString() ?? "";
            }

            throw new InvalidOperationException(
                $"Technitium AAAA ADD Fehler: {message}");
        }
    }

    Log(
        $"  -> AAAA angelegt: {ipAddress}");
}

private async Task DeleteTechnitiumAaaaAsync(
    HttpClient client,
    Config cfg,
    string dnsName,
    string ipAddress,
    CancellationToken cancellationToken)
{
    string zone =
        cfg.TechnitiumDnsZone.Trim().TrimEnd('.');

    string url =
        $"{cfg.TechnitiumApiUrl.TrimEnd('/')}" +
        "/api/zones/records/delete" +
        $"?domain={Uri.EscapeDataString(dnsName)}" +
        $"&zone={Uri.EscapeDataString(zone)}" +
        "&type=AAAA" +
        $"&ipAddress={Uri.EscapeDataString(ipAddress)}";

    using HttpRequestMessage request =
        new(HttpMethod.Get, url);

    request.Headers.Authorization =
        new AuthenticationHeaderValue(
            "Bearer",
            cfg.TechnitiumApiToken);

    using HttpResponseMessage response =
        await client.SendAsync(
            request,
            cancellationToken);

    string body =
        await response.Content.ReadAsStringAsync(
            cancellationToken);

    if (!response.IsSuccessStatusCode)
    {
        throw new HttpRequestException(
            $"Technitium AAAA DELETE API returned " +
            $"{(int)response.StatusCode} " +
            $"{response.StatusCode}: {body}");
    }

    using JsonDocument doc =
        JsonDocument.Parse(body);

    if (doc.RootElement.TryGetProperty(
            "status",
            out JsonElement statusElement))
    {
        string? status =
            statusElement.GetString();

        if (!string.Equals(
                status,
                "ok",
                StringComparison.OrdinalIgnoreCase))
        {
            string message = "";

            if (doc.RootElement.TryGetProperty(
                    "message",
                    out JsonElement messageElement))
            {
                message =
                    messageElement.GetString() ?? "";
            }

            throw new InvalidOperationException(
                $"Technitium AAAA DELETE error: {message}");
        }
    }

    Log(
        $"  -> AAAA deleted: {ipAddress}");
}

private async Task UpdateTechnitiumAaaaAsync(
    HttpClient client,
    Config cfg,
    string dnsName,
    TechnitiumRecord existing,
    string newIpAddress,
    string comments,
    CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(
            existing.IpAddress))
    {
        throw new InvalidOperationException(
            "AAAA update without an existing IP address.");
    }

    string zone =
        cfg.TechnitiumDnsZone.Trim().TrimEnd('.');

    string url =
        $"{cfg.TechnitiumApiUrl.TrimEnd('/')}" +
        "/api/zones/records/update" +
        $"?domain={Uri.EscapeDataString(dnsName)}" +
        $"&zone={Uri.EscapeDataString(zone)}" +
        "&type=AAAA" +
        $"&ipAddress={Uri.EscapeDataString(existing.IpAddress)}" +
        $"&newIpAddress={Uri.EscapeDataString(newIpAddress)}" +
        $"&ttl={cfg.Ipv6Ttl}" +
        $"&comments={Uri.EscapeDataString(comments)}";

    using HttpRequestMessage request =
        new(HttpMethod.Get, url);

    request.Headers.Authorization =
        new AuthenticationHeaderValue(
            "Bearer",
            cfg.TechnitiumApiToken);

    using HttpResponseMessage response =
        await client.SendAsync(
            request,
            cancellationToken);

    string body =
        await response.Content.ReadAsStringAsync(
            cancellationToken);

    if (!response.IsSuccessStatusCode)
    {
        throw new HttpRequestException(
            $"Technitium AAAA UPDATE API returned " +
            $"{(int)response.StatusCode} " +
            $"{response.StatusCode}: {body}");
    }

    using JsonDocument doc =
        JsonDocument.Parse(body);

    if (doc.RootElement.TryGetProperty(
            "status",
            out JsonElement statusElement))
    {
        string? status =
            statusElement.GetString();

        if (!string.Equals(
                status,
                "ok",
                StringComparison.OrdinalIgnoreCase))
        {
            string message = "";

            if (doc.RootElement.TryGetProperty(
                    "message",
                    out JsonElement messageElement))
            {
                message =
                    messageElement.GetString() ?? "";
            }

            throw new InvalidOperationException(
                $"Technitium AAAA UPDATE Error: {message}");
        }
    }

    Log(
        $"  -> AAAA updated: " +
        $"{existing.IpAddress} -> {newIpAddress}");
}

    private static string NormalizeDnsName(
        string name,
        string zone)
    {
        string result =
            name.Trim().TrimEnd('.');

        if (!result.Contains('.',
                StringComparison.Ordinal))
        {
            result += "." + zone.Trim().TrimEnd('.');
        }

        return result;
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

    internal sealed class Config
    {
        public bool Enabled { get; set; } = true;

        public bool DryRun { get; set; } = true;

        public bool RunOnStartup { get; set; } = true;

        public bool EnableIpv6Sync { get; set; } = true;

        public int Ipv6Ttl { get; set; } = 3600;

        public int IntervalMinutes { get; set; } = 15;

        public string FritzBoxUrl { get; set; } =
            "http://192.168.178.1:49000";

        public string FritzBoxHttpsUrl { get; set; } =
            "https://192.168.178.1:49443";

        public string FritzBoxWebUrl { get; set; } = "";

        public string FritzUsername { get; set; } =
            "TechnitiumSync";

        public string FritzPassword { get; set; } = "";

        public string TechnitiumApiUrl { get; set; } =
            "http://192.168.178.2:5380";

        public string TechnitiumDnsZone { get; set; } =
            "koch.local";

        public string TechnitiumApiToken { get; set; } = "";
    }

    private sealed record ReservedLease(
        string HardwareAddress,
        string Address,
        string HostName);

    private sealed record TechnitiumRecord(
        string Name,
        string Type,
        string? IpAddress,
        string? Comments,
        int Ttl);

}
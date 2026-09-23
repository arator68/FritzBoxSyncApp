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
    private int _configSaveScheduled;

    public string Description =>
        "Synchronizes Technitium DHCP reservation hostnames to FRITZ!Box FriendlyNames by MAC address.";

    public async Task InitializeAsync(
    IDnsServer dnsServer,
    string? config)
{
    // Falls die App durch das Speichern der Konfiguration
    // erneut initialisiert wird, den bisherigen Worker sauber beenden.
    StopWorker();

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
        Log("FRITZ!Box Sync is disabled.");
        return Task.CompletedTask;
    }

    CancellationTokenSource cts = new();

    lock (_sync)
    {
        if (_cts is not null)
        {
            cts.Dispose();
            Log("FRITZ!Box Sync worker is already running.");
            return Task.CompletedTask;
        }

        _cts = cts;
        _worker = Task.Run(() => WorkerAsync(cts.Token));
    }

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

        List<Ipv6PtrDesired> desiredPtrs = new();

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

                string reverseName = CreateIpv6ReverseName(ip);
                string reverseZone = CreateIpv6ReverseZone(ip);

                if (!string.IsNullOrWhiteSpace(reverseName) && !string.IsNullOrWhiteSpace(reverseZone))
                {
                    reverseName = $"{reverseName}.{reverseZone}";

                    desiredPtrs.Add(
                        new Ipv6PtrDesired(
                            ip,
                            reverseName,
                            reverseZone,
                            dnsName,
                            desiredComment));
                }
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

        Log(
            $"IPv6 PTR Sync enabled         : {cfg.EnableIpv6PtrSync}");

        if (cfg.EnableIpv6PtrSync)
        {
            await SynchronizeIpv6PtrAsync(
                techClient,
                cfg,
                desiredPtrs,
                cancellationToken);
        }

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


    private async Task SynchronizeIpv6PtrAsync(
        HttpClient client,
        Config cfg,
        List<Ipv6PtrDesired> desired,
        CancellationToken cancellationToken)
    {
        Log("--------------------------------------------");
        Log("IPv6 PTR synchronization started");
        Log($"PTR: desired records prepared: {desired.Count}");

        if (desired.Count == 0)
        {
            Log("PTR: no stable IPv6 addresses available.");

            /*
            * Keine PTR-Records verändern.
            *
            * Bereits als FritzBoxSync verwaltete Reverse-Zonen
            * dürfen trotzdem geprüft werden.
            *
            * Eine Zone wird dabei ausschließlich gelöscht,
            * wenn sie tatsächlich leer ist.
            */
            await CleanupManagedIpv6ReverseZonesAsync(
                client,
                cfg,
                desired,
                cancellationToken);

            return;
        }

        try
        {
            int checkedCount = 0;
            int alreadyCorrect = 0;
            int added = 0;
            int updated = 0;
            int deleted = 0;
            int errors = 0;

            foreach (var zoneGroup in
                     desired.GroupBy(
                         x => x.ReverseZone,
                         StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();

                string reverseZone = zoneGroup.Key;
                List<Ipv6PtrDesired> zoneDesired =
                    zoneGroup.ToList();

                Log($"PTR: checking reverse zone {reverseZone}");

                if (!await EnsureTechnitiumReverseZoneAsync(
                        client,
                        cfg,
                        reverseZone,
                        CreateIpv6ReverseNetworkCidr(
                            zoneDesired[0].IpAddress),
                        cancellationToken))
                {
                    errors++;
                    continue;
                }

                foreach (Ipv6PtrDesired wanted in zoneDesired)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    checkedCount++;

                    Log(
                        $"PTR check: {wanted.IpAddress} -> {wanted.TargetName} " +
                        $"(owner: {wanted.ReverseName})");

                    TechnitiumPtrRecord? current =
                        await GetTechnitiumPtrRecordAsync(
                            client,
                            cfg,
                            reverseZone,
                            wanted.ReverseName,
                            cancellationToken);

                    if (current is not null)
                    {
                        bool targetCorrect =
                            string.Equals(
                                NormalizeDnsName(
                                    current.PtrName,
                                    cfg.TechnitiumDnsZone),
                                NormalizeDnsName(
                                    wanted.TargetName,
                                    cfg.TechnitiumDnsZone),
                                StringComparison.OrdinalIgnoreCase);

                        bool commentCorrect =
                            string.Equals(
                                current.Comments,
                                wanted.Comment,
                                StringComparison.Ordinal);

                        bool ttlCorrect =
                            current.Ttl == cfg.Ipv6PtrTtl;

                        if (targetCorrect &&
                            commentCorrect &&
                            ttlCorrect)
                        {
                            Log("  -> PTR already correct.");
                            alreadyCorrect++;
                            continue;
                        }

                        Log("  -> PTR UPDATE required.");
                        Log($"     current target : {current.PtrName}");
                        Log($"     desired target : {wanted.TargetName}");
                        Log($"     current comment: {current.Comments}");
                        Log($"     desired comment: {wanted.Comment}");
                        Log($"     current TTL    : {current.Ttl}");
                        Log($"     desired TTL    : {cfg.Ipv6PtrTtl}");

                        if (cfg.DryRun)
                        {
                            Log("     -> TEST MODE: would be updated.");
                            updated++;
                        }
                        else
                        {
                            try
                            {
                                await UpdateTechnitiumPtrAsync(
                                    client,
                                    cfg,
                                    reverseZone,
                                    current,
                                    wanted.TargetName,
                                    wanted.Comment,
                                    cancellationToken);

                                updated++;
                            }
                            catch (Exception ex)
                            {
                                errors++;
                                Log(
                                    "     -> PTR update error: " +
                                    ex.Message);
                            }
                        }

                        continue;
                    }

                    Log(
                        $"  -> PTR MISSING: {wanted.ReverseName}");

                    if (cfg.DryRun)
                    {
                        Log("     -> TEST MODE: would be created.");
                        added++;
                    }
                    else
                    {
                        try
                        {
                            await AddTechnitiumPtrAsync(
                                client,
                                cfg,
                                reverseZone,
                                wanted.ReverseName,
                                wanted.TargetName,
                                wanted.Comment,
                                cancellationToken);

                            added++;
                        }
                        catch (Exception ex)
                        {
                            errors++;
                            Log(
                                "     -> PTR add error: " +
                                ex.Message);
                        }
                    }
                }
            }

             /*
             * --------------------------------------------------------
             * 3. ALTE, VON FRITZBOXSYNC VERWALTETE PTR-RECORDS
             *
             * Es werden ausschließlich PTR-Records mit unserem
             * eindeutigen FritzBoxSync-Kommentar betrachtet.
             * Manuell angelegte PTR-Records bleiben unangetastet.
             *
             * Sicherheitsregel:
             * Wenn desired leer ist, wurde oben bereits abgebrochen.
             * Dadurch kann ein leerer FRITZ!Box-IPv6-Status niemals
             * zu einem Massen-DELETE führen.
             * --------------------------------------------------------
             */
            int deleteCandidates = 0;

            foreach (var zoneGroup in
                     desired.GroupBy(
                         x => x.ReverseZone,
                         StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();

                string reverseZone = zoneGroup.Key;

                List<Ipv6PtrDesired> zoneDesired =
                    zoneGroup.ToList();

                HashSet<string> desiredOwners =
                    zoneDesired
                        .Select(x => NormalizeReverseName(x.ReverseName))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                Log(
                    $"PTR: checking obsolete managed records in {reverseZone}");

                List<TechnitiumPtrRecord> zoneRecords =
                    await GetTechnitiumPtrRecordsAsync(
                        client,
                        cfg,
                        reverseZone,
                        cancellationToken);

                foreach (TechnitiumPtrRecord existing in zoneRecords)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (string.IsNullOrWhiteSpace(existing.Comments) ||
                        !existing.Comments.StartsWith(
                            "FritzBoxSync - FRITZ!Box:",
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string owner =
                        NormalizeReverseName(existing.Name);

                    if (desiredOwners.Contains(owner))
                    {
                        continue;
                    }

                    deleteCandidates++;

                    Log(
                        "  -> PTR DELETE candidate: obsolete managed PTR");
                    Log($"     owner   : {existing.Name}");
                    Log($"     target  : {existing.PtrName}");
                    Log($"     comment : {existing.Comments}");

                    if (cfg.DryRun)
                    {
                        Log("     -> TEST MODE: would be deleted.");
                        continue;
                    }

                    try
                    {
                        await DeleteTechnitiumPtrAsync(
                            client,
                            cfg,
                            reverseZone,
                            existing,
                            cancellationToken);

                        deleted++;
                    }
                    catch (Exception ex)
                    {
                        errors++;

                        Log(
                            "     -> PTR delete error: " +
                            ex.Message);
                    }
                }
            }

            Log("--------------------------------------------");
            Log($"PTR checked                  : {checkedCount}");
            Log($"PTR already correct          : {alreadyCorrect}");
            Log($"PTR newly created            : {added}");
            Log($"PTR updated                  : {updated}");
            Log($"PTR delete candidates        : {deleteCandidates}");
            Log($"PTR deleted                  : {deleted}");
            Log($"PTR errors                   : {errors}");
            Log("PTR DELETE is enabled with DryRun protection.");

            await CleanupManagedIpv6ReverseZonesAsync(
                client,
                cfg,
                desired,
                cancellationToken);
        }
        catch (Exception ex)
        {
            Log(
                "IPv6 PTR synchronization failed: " +
                ex.GetType().Name +
                ": " +
                ex.Message);
        }
    }

    private static string CreateIpv6ReverseName(
        string ipAddress)
    {
        if (!IPAddress.TryParse(
                ipAddress,
                out IPAddress? address) ||
            address.AddressFamily !=
                System.Net.Sockets.AddressFamily.InterNetworkV6)
            return "";

        string hex =
            Convert.ToHexString(
                address.GetAddressBytes())
            .ToLowerInvariant();

        int prefixNibbles =
            IsIpv6Ula(ipAddress)
                ? 14
                : 16;

        // Technitium expects the record owner relative to the reverse zone.
        // Example:
        // 2001:9e8:47b0:8100:b8fc:7dff:feae:37b9
        // zone  = 0.0.1.8.0.b.7.4.8.e.9.0.1.0.0.2.ip6.arpa
        // owner = 9.b.7.3.e.a.e.f.f.f.d.7.c.f.8.b
        return string.Join(
            ".",
            hex
                .Substring(prefixNibbles)
                .Reverse());
    }

    private static string CreateIpv6ReverseZone(
        string ipAddress)
    {
        if (!IPAddress.TryParse(
                ipAddress,
                out IPAddress? address) ||
            address.AddressFamily !=
                System.Net.Sockets.AddressFamily.InterNetworkV6)
            return "";

        string hex =
            Convert.ToHexString(
                address.GetAddressBytes())
            .ToLowerInvariant();

        int prefixNibbles =
            IsIpv6Ula(ipAddress)
                ? 14
                : 16;

        return string.Join(
                   ".",
                   hex
                       .Substring(0, prefixNibbles)
                       .Reverse()) +
               ".ip6.arpa";
    }

    private static string CreateIpv6ReverseNetworkCidr(
        string ipAddress)
    {
        if (!IPAddress.TryParse(
                ipAddress,
                out IPAddress? address) ||
            address.AddressFamily !=
                System.Net.Sockets.AddressFamily.InterNetworkV6)
            return "";

        int prefixLength =
            IsIpv6Ula(ipAddress)
                ? 56
                : 64;

        byte[] bytes =
            address.GetAddressBytes();

        for (int i = prefixLength / 8;
             i < bytes.Length;
             i++)
        {
            bytes[i] = 0;
        }

        return new IPAddress(bytes) +
               "/" +
               prefixLength;
    }

    private async Task<bool> EnsureTechnitiumReverseZoneAsync(
        HttpClient client,
        Config cfg,
        string reverseZone,
        string networkCidr,
        CancellationToken cancellationToken)
    {
        Log($"PTR: checking zone existence: {reverseZone}");

        string listUrl =
            $"{cfg.TechnitiumApiUrl.TrimEnd('/')}" +
            "/api/zones/list";

        using HttpRequestMessage request =
            new(HttpMethod.Get, listUrl);

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

       if (ZoneExists(
            doc.RootElement,
            reverseZone))
        {
            Log($"PTR: reverse zone exists: {reverseZone}");

            /*
            * Eine bereits vorhandene Reverse-Zone wird nur dann
            * als FritzBoxSync-Zone übernommen, wenn darin bereits
            * mindestens ein von FritzBoxSync verwalteter PTR-Record
            * vorhanden ist.
            *
            * Manuelle PTR-Records oder eine leere Zone reichen
            * ausdrücklich NICHT für eine Übernahme aus.
            */
            try
            {
                List<TechnitiumPtrRecord> existingRecords =
                    await GetTechnitiumPtrRecordsAsync(
                        client,
                        cfg,
                        reverseZone,
                        cancellationToken);

                bool hasManagedPtr =
                    existingRecords.Any(record =>
                        !string.IsNullOrWhiteSpace(record.Comments) &&
                        record.Comments.StartsWith(
                            "FritzBoxSync - FRITZ!Box:",
                            StringComparison.Ordinal));

                if (hasManagedPtr)
                {
                    Log(
                        "PTR: existing reverse zone contains " +
                        "FritzBoxSync PTR records.");

                    if (cfg.DryRun)
                    {
                        Log(
                            "PTR: TEST MODE - existing reverse zone " +
                            "would be registered as managed.");
                    }
                    else if (AddManagedIpv6ReverseZone(
                                cfg,
                                reverseZone))
                    {
                        Log(
                            "PTR: existing reverse zone " +
                            $"registered as managed: {reverseZone}");

                        ScheduleAppConfigSave();
                    }
                }
                else
                {
                    Log(
                        "PTR: existing reverse zone contains " +
                        "no FritzBoxSync PTR records.");

                    Log(
                        "PTR: existing reverse zone will NOT " +
                        "be registered as managed.");
                }
            }
            catch (Exception ex)
            {
                Log(
                    "PTR: could not inspect existing reverse zone: " +
                    ex.Message);
            }

            return true;
        }
        if (cfg.DryRun)
        {
            Log("PTR: TEST MODE - zone would be created.");
            return true;
        }

        if (AddManagedIpv6ReverseZone(
                cfg,
                reverseZone))
        {
            Log(
                $"PTR: reverse zone registered as managed: {reverseZone}");

            ScheduleAppConfigSave();
        }

        if (cfg.DryRun)
        {
            Log("PTR: TEST MODE - zone would be created.");
            return true;
        }

        string createUrl =
            $"{cfg.TechnitiumApiUrl.TrimEnd('/')}" +
            "/api/zones/create" +
            $"?zone={Uri.EscapeDataString(networkCidr)}" +
            "&type=Primary";

        using HttpRequestMessage createRequest =
            new(HttpMethod.Get, createUrl);

        createRequest.Headers.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                cfg.TechnitiumApiToken);

        using HttpResponseMessage createResponse =
            await client.SendAsync(
                createRequest,
                cancellationToken);

        string createBody =
            await createResponse.Content.ReadAsStringAsync(
                cancellationToken);

        createResponse.EnsureSuccessStatusCode();

        EnsureTechnitiumApiOk(
            createBody,
            "Technitium reverse zone CREATE");

        Log(
            $"PTR: reverse zone created: {reverseZone}");

        return true;
    }

    private static bool ZoneExists(
        JsonElement root,
        string zoneName)
    {
        if (!root.TryGetProperty(
                "response",
                out JsonElement response))
            return false;

        if (!response.TryGetProperty(
                "zones",
                out JsonElement zones) ||
            zones.ValueKind != JsonValueKind.Array)
            return false;

        foreach (JsonElement zone in zones.EnumerateArray())
        {
            string? name =
                GetString(zone, "name");

            if (string.Equals(
                    name?.TrimEnd('.'),
                    zoneName.TrimEnd('.'),
                    StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }


    private static string NormalizeReverseName(string name)
    {
        return name
            .Trim()
            .TrimEnd('.')
            .ToLowerInvariant();
    }

    private static async Task<List<TechnitiumPtrRecord>>
        GetTechnitiumPtrRecordsAsync(
            HttpClient client,
            Config cfg,
            string reverseZone,
            CancellationToken cancellationToken)
    {
        string url =
            $"{cfg.TechnitiumApiUrl.TrimEnd('/')}" +
            "/api/zones/records/get" +
            $"?domain={Uri.EscapeDataString(reverseZone)}" +
            $"&zone={Uri.EscapeDataString(reverseZone)}" +
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
                out JsonElement records) ||
            records.ValueKind != JsonValueKind.Array)
        {
            return new List<TechnitiumPtrRecord>();
        }

        List<TechnitiumPtrRecord> result = new();

        foreach (JsonElement record in records.EnumerateArray())
        {
            if (!string.Equals(
                    GetString(record, "type"),
                    "PTR",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? name =
                GetString(record, "name");

            if (string.IsNullOrWhiteSpace(name))
                continue;

            string ptrName = "";

            if (record.TryGetProperty(
                    "rData",
                    out JsonElement rData) &&
                rData.ValueKind == JsonValueKind.Object)
            {
                ptrName =
                    GetString(rData, "ptrName") ?? "";
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
                new TechnitiumPtrRecord(
                    name.TrimEnd('.'),
                    ptrName.TrimEnd('.'),
                    comments,
                    ttl));
        }

        return result;
    }

    private async Task DeleteTechnitiumPtrAsync(
        HttpClient client,
        Config cfg,
        string reverseZone,
        TechnitiumPtrRecord existing,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(existing.PtrName))
        {
            throw new InvalidOperationException(
                "PTR DELETE without a PTR target is not allowed.");
        }

        string url =
            $"{cfg.TechnitiumApiUrl.TrimEnd('/')}" +
            "/api/zones/records/delete" +
            $"?domain={Uri.EscapeDataString(existing.Name)}" +
            $"&zone={Uri.EscapeDataString(reverseZone)}" +
            "&type=PTR" +
            $"&ptrName={Uri.EscapeDataString(existing.PtrName)}";

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
                $"Technitium PTR DELETE API returned " +
                $"{(int)response.StatusCode} " +
                $"{response.StatusCode}: {body}");
        }

        EnsureTechnitiumApiOk(
            body,
            "Technitium PTR DELETE");

        Log(
            $"     -> PTR deleted: {existing.Name} -> {existing.PtrName}");
    }

    private static async Task<TechnitiumPtrRecord?>
        GetTechnitiumPtrRecordAsync(
            HttpClient client,
            Config cfg,
            string reverseZone,
            string reverseName,
            CancellationToken cancellationToken)
    {
        string url =
            $"{cfg.TechnitiumApiUrl.TrimEnd('/')}" +
            "/api/zones/records/get" +
            $"?domain={Uri.EscapeDataString(reverseName)}" +
            $"&zone={Uri.EscapeDataString(reverseZone)}";

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
            return null;
        }

        foreach (JsonElement record in records.EnumerateArray())
        {
            if (!string.Equals(
                    GetString(record, "type"),
                    "PTR",
                    StringComparison.OrdinalIgnoreCase))
                continue;

            string? name =
                GetString(record, "name");

            if (string.IsNullOrWhiteSpace(name))
                continue;

            string ptrName = "";

            if (record.TryGetProperty(
                    "rData",
                    out JsonElement rData) &&
                rData.ValueKind == JsonValueKind.Object)
            {
                ptrName =
                    GetString(rData, "ptrName") ?? "";
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

            return new TechnitiumPtrRecord(
                name.TrimEnd('.'),
                ptrName.TrimEnd('.'),
                comments,
                ttl);
        }

        return null;
    }

    private static async Task AddTechnitiumPtrAsync(
        HttpClient client,
        Config cfg,
        string reverseZone,
        string reverseName,
        string targetName,
        string comments,
        CancellationToken cancellationToken)
    {
        string url =
            $"{cfg.TechnitiumApiUrl.TrimEnd('/')}" +
            "/api/zones/records/add" +
            $"?domain={Uri.EscapeDataString(reverseName)}" +
            $"&zone={Uri.EscapeDataString(reverseZone)}" +
            "&type=PTR" +
            $"&ttl={cfg.Ipv6PtrTtl}" +
            $"&ptrName={Uri.EscapeDataString(targetName)}" +
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
                $"Technitium PTR ADD API returned " +
                $"{(int)response.StatusCode} " +
                $"{response.StatusCode}: {body}");
        }

        EnsureTechnitiumApiOk(
            body,
            "Technitium PTR ADD");
    }

    private static async Task UpdateTechnitiumPtrAsync(
        HttpClient client,
        Config cfg,
        string reverseZone,
        TechnitiumPtrRecord existing,
        string targetName,
        string comments,
        CancellationToken cancellationToken)
    {
        string url =
            $"{cfg.TechnitiumApiUrl.TrimEnd('/')}" +
            "/api/zones/records/update" +
            $"?domain={Uri.EscapeDataString(existing.Name)}" +
            $"&zone={Uri.EscapeDataString(reverseZone)}" +
            "&type=PTR" +
            $"&ptrName={Uri.EscapeDataString(existing.PtrName)}" +
            $"&newPtrName={Uri.EscapeDataString(targetName)}" +
            $"&ttl={cfg.Ipv6PtrTtl}" +
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
                $"Technitium PTR UPDATE API returned " +
                $"{(int)response.StatusCode} " +
                $"{response.StatusCode}: {body}");
        }

        EnsureTechnitiumApiOk(
            body,
            "Technitium PTR UPDATE");
    }

    private static void EnsureTechnitiumApiOk(
    string body,
    string operation)
{
    using JsonDocument doc = JsonDocument.Parse(body);

    if (!doc.RootElement.TryGetProperty(
            "status",
            out JsonElement statusElement))
    {
        throw new InvalidOperationException(
            $"{operation} error: Technitium API response does not contain a status property.");
    }

    string status = statusElement.GetString() ?? "";

    if (string.Equals(
            status,
            "ok",
            StringComparison.OrdinalIgnoreCase))
    {
        return;
    }

    string errorMessage = "";

    if (doc.RootElement.TryGetProperty(
            "errorMessage",
            out JsonElement errorMessageElement))
    {
        errorMessage =
            errorMessageElement.GetString() ?? "";
    }

    if (string.IsNullOrWhiteSpace(errorMessage))
    {
        errorMessage = $"API returned status '{status}'.";
    }

    throw new InvalidOperationException(
        $"{operation} error: {errorMessage}");
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

    EnsureTechnitiumApiOk(body,"Technitium AAAA ADD");

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

    EnsureTechnitiumApiOk(body,"Technitium AAAA DELETE");

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
    Task? worker;
    CancellationTokenSource? cts;

    lock (_sync)
    {
        cts = _cts;
        worker = _worker;

        _cts = null;
        _worker = null;
    }

    if (cts is null)
        return;

    try
    {
        cts.Cancel();

        if (worker is not null &&
            Task.CurrentId != worker.Id)
        {
            try
            {
                worker.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
    finally
    {
        cts.Dispose();
    }
}

private static bool IsManagedIpv6ReverseZone(
    Config cfg,
    string reverseZone)
{
    string normalized =
        NormalizeReverseName(reverseZone);

    return cfg.ManagedIpv6ReverseZones.Any(
        x => string.Equals(
            NormalizeReverseName(x),
            normalized,
            StringComparison.OrdinalIgnoreCase));
}


private static bool AddManagedIpv6ReverseZone(
    Config cfg,
    string reverseZone)
{
    string normalized =
        NormalizeReverseName(reverseZone);

    if (string.IsNullOrWhiteSpace(normalized))
        return false;

    if (IsManagedIpv6ReverseZone(
            cfg,
            normalized))
    {
        return false;
    }

    cfg.ManagedIpv6ReverseZones.Add(normalized);

    return true;
}


private static bool RemoveManagedIpv6ReverseZone(
    Config cfg,
    string reverseZone)
{
    string normalized =
        NormalizeReverseName(reverseZone);

    int removed =
        cfg.ManagedIpv6ReverseZones.RemoveAll(
            x => string.Equals(
                NormalizeReverseName(x),
                normalized,
                StringComparison.OrdinalIgnoreCase));

    return removed > 0;
}

private void ScheduleAppConfigSave()
{
    if (Interlocked.Exchange(
            ref _configSaveScheduled,
            1) != 0)
    {
        return;
    }

    _ = Task.Run(async () =>
    {
        try
        {
            await Task.Delay(
                TimeSpan.FromSeconds(15));

            await SaveAppConfigAsync();
        }
        catch (Exception ex)
        {
            Log(
                $"Error saving app configuration: {ex}");
        }
        finally
        {
            Interlocked.Exchange(
                ref _configSaveScheduled,
                0);
        }
    });
}

private async Task<bool> IsTechnitiumReverseZoneEmptyAsync(
    HttpClient client,
    Config cfg,
    string reverseZone,
    CancellationToken cancellationToken)
{
    string url =
        $"{cfg.TechnitiumApiUrl.TrimEnd('/')}" +
        "/api/zones/records/get" +
        $"?domain={Uri.EscapeDataString(reverseZone)}" +
        $"&zone={Uri.EscapeDataString(reverseZone)}" +
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
            out JsonElement records) ||
        records.ValueKind != JsonValueKind.Array)
    {
        return true;
    }

    foreach (JsonElement record
             in records.EnumerateArray())
    {
        string? type =
            GetString(record, "type");

        if (string.IsNullOrWhiteSpace(type))
            continue;

        if (string.Equals(
                type,
                "SOA",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                type,
                "NS",
                StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        return false;
    }

    return true;
}

private async Task DeleteTechnitiumReverseZoneAsync(
    HttpClient client,
    Config cfg,
    string reverseZone,
    CancellationToken cancellationToken)
{
    string url =
        $"{cfg.TechnitiumApiUrl.TrimEnd('/')}" +
        "/api/zones/delete" +
        $"?zone={Uri.EscapeDataString(reverseZone)}";

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
            $"Technitium reverse zone DELETE API returned " +
            $"{(int)response.StatusCode} " +
            $"{response.StatusCode}: {body}");
    }

    EnsureTechnitiumApiOk(
        body,
        "Technitium reverse zone DELETE");

    Log(
        $"     -> Reverse zone deleted: {reverseZone}");
}

private async Task CleanupManagedIpv6ReverseZonesAsync(
    HttpClient client,
    Config cfg,
    List<Ipv6PtrDesired> desired,
    CancellationToken cancellationToken)
{
    if (cfg.ManagedIpv6ReverseZones.Count == 0)
    {
        Log("PTR: no managed reverse zones registered.");
        return;
    }

    HashSet<string> desiredZones =
        desired
            .Select(x => NormalizeReverseName(x.ReverseZone))
            .ToHashSet(
                StringComparer.OrdinalIgnoreCase);

    List<string> managedZones =
        cfg.ManagedIpv6ReverseZones
            .Select(NormalizeReverseName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    int deleted = 0;
    int kept = 0;

    foreach (string reverseZone in managedZones)
    {
        cancellationToken.ThrowIfCancellationRequested();

        /*
         * Diese Zone wird aktuell noch benötigt.
         */
        if (desiredZones.Contains(reverseZone))
        {
            continue;
        }

        Log(
            $"PTR: obsolete managed reverse zone: {reverseZone}");

        bool empty;

        try
        {
            empty =
                await IsTechnitiumReverseZoneEmptyAsync(
                    client,
                    cfg,
                    reverseZone,
                    cancellationToken);
        }
        catch (Exception ex)
        {
            Log(
                $"     -> Could not inspect zone: {ex.Message}");

            kept++;
            continue;
        }

        if (!empty)
        {
            Log(
                "     -> Zone is NOT empty. " +
                "It will NOT be deleted.");

            kept++;
            continue;
        }

        Log(
            "     -> Zone is empty.");

        if (cfg.DryRun)
        {
            Log(
                "     -> TEST MODE: " +
                "would delete reverse zone.");

            continue;
        }

        try
        {
            await DeleteTechnitiumReverseZoneAsync(
                client,
                cfg,
                reverseZone,
                cancellationToken);

            if (RemoveManagedIpv6ReverseZone(
                    cfg,
                    reverseZone))
            {
                ScheduleAppConfigSave();
            }

            deleted++;
        }
        catch (Exception ex)
        {
            Log(
                $"     -> Reverse zone delete error: " +
                ex.Message);

            kept++;
        }
    }

    Log("--------------------------------------------");
    Log(
        $"PTR obsolete zones deleted   : {deleted}");
    Log(
        $"PTR obsolete zones kept      : {kept}");
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

        public bool EnableIpv6PtrSync { get; set; } = true;

        public List<string> ManagedIpv6ReverseZones { get; set; } = new();

        public int Ipv6Ttl { get; set; } = 3600;

        public int Ipv6PtrTtl { get; set; } = 3600;

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

    private sealed record Ipv6PtrDesired(
        string IpAddress,
        string ReverseName,
        string ReverseZone,
        string TargetName,
        string Comment);

    private sealed record TechnitiumPtrRecord(
        string Name,
        string PtrName,
        string? Comments,
        int Ttl);

    private sealed record TechnitiumRecord(
        string Name,
        string Type,
        string? IpAddress,
        string? Comments,
        int Ttl);

}
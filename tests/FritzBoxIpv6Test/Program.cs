using System.Text;

namespace FritzBoxSync;

internal static class Program
{
    private static readonly string[] TemporaryTypes =
    {
        "IPv6-GUA-Temporary",
        "IPv6-ULA-Temporary"
    };

    private static readonly string[] StableTypes =
    {
        "IPv6-GUA",
        "IPv6-ULA"
    };

    private static async Task Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        Console.WriteLine("========================================");
        Console.WriteLine(" FritzBox IPv4 / IPv6 Test");
        Console.WriteLine("========================================");
        Console.WriteLine();

        App.Config config = new();

        Console.Write($"FRITZ!Box Web URL [{config.FritzBoxWebUrl}]: ");
        string? webUrl = Console.ReadLine();
        if (!string.IsNullOrWhiteSpace(webUrl))
            config.FritzBoxWebUrl = webUrl.Trim().TrimEnd('/');

        Console.Write($"FRITZ!Box Benutzer [{config.FritzUsername}]: ");
        string? username = Console.ReadLine();
        if (!string.IsNullOrWhiteSpace(username))
            config.FritzUsername = username.Trim();

        Console.Write("FRITZ!Box Passwort: ");
        config.FritzPassword = ReadPassword();
        Console.WriteLine();
        Console.WriteLine();

        Console.WriteLine("Intervall für Verlaufsmessung:");
        Console.WriteLine("  0 = einmalig");
        Console.WriteLine("  5 = alle 5 Minuten");
        Console.WriteLine("  15 = alle 15 Minuten");
        Console.Write("Intervall in Minuten [15]: ");
        string? intervalText = Console.ReadLine();
        int intervalMinutes = 15;
        if (int.TryParse(intervalText, out int parsed) && parsed >= 0)
            intervalMinutes = parsed;

        string csvPath = Path.Combine(
            AppContext.BaseDirectory,
            "ip-history.csv");

        Console.WriteLine();
        Console.WriteLine($"CSV-Verlauf: {csvPath}");
        Console.WriteLine();

        using HttpClient httpClient =
            FritzBoxClient.CreateHttpClient(
                config.FritzUsername,
                config.FritzPassword);

        FritzBoxClient fritzBox =
            new(httpClient, config);

        do
        {
            try
            {
                await RunScanAsync(
                    fritzBox,
                    csvPath,
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine("FEHLER");
                Console.WriteLine(ex.Message);
            }

            if (intervalMinutes <= 0)
                break;

            Console.WriteLine();
            Console.WriteLine(
                $"Nächster Scan in {intervalMinutes} Minuten. " +
                "Strg+C zum Beenden.");

            await Task.Delay(
                TimeSpan.FromMinutes(intervalMinutes));

        } while (true);
    }

    private static async Task RunScanAsync(
        FritzBoxClient fritzBox,
        string csvPath,
        CancellationToken cancellationToken)
    {
        DateTime timestamp = DateTime.Now;

        Console.WriteLine("----------------------------------------");
        Console.WriteLine($"Scan: {timestamp:dd.MM.yyyy HH:mm:ss}");
        Console.WriteLine("FRITZ!Box Login ...");

        string sid = await fritzBox.GetSidAsync(cancellationToken);
        Console.WriteLine("  SID: OK");

        Console.WriteLine("LAN-Geräte werden gelesen ...");

        List<FritzBoxClient.FritzLanDevice> devices =
            await fritzBox.GetLanDevicesAsync(
                sid,
                cancellationToken);

        Console.WriteLine($"  Geräte: {devices.Count}");
        Console.WriteLine();

        int ipv4Count = 0;
        int stableCount = 0;
        int temporaryCount = 0;
        int linkLocalCount = 0;
        int ipv6OtherCount = 0;

        foreach (FritzBoxClient.FritzLanDevice device in
                 devices.OrderBy(x => x.FriendlyName ?? x.Name ?? x.Mac))
        {
            List<FritzBoxClient.FritzIpEntry> ipv4 = device.IpList
                .Where(x => string.Equals(
                    x.AddrType,
                    "IPv4",
                    StringComparison.OrdinalIgnoreCase))
                .ToList();

            List<FritzBoxClient.FritzIpEntry> ipv6 = device.IpList
                .Where(x => x.AddrType.StartsWith(
                    "IPv6-",
                    StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (ipv4.Count == 0 && ipv6.Count == 0)
                continue;

            Console.WriteLine(
                $"{device.FriendlyName ?? device.Name ?? "<ohne Name>"}");
            Console.WriteLine($"  MAC: {device.Mac}");

            if (ipv4.Count > 0)
            {
                Console.WriteLine("  IPv4");

                foreach (FritzBoxClient.FritzIpEntry entry in ipv4)
                {
                    Console.WriteLine(
                        $"    {entry.Ip,-45} {entry.AddrType}");

                    ipv4Count++;

                    AppendCsv(
                        csvPath,
                        timestamp,
                        device,
                        entry,
                        "IPv4");
                }
            }

            if (ipv6.Count > 0)
            {
                Console.WriteLine("  IPv6");

                foreach (FritzBoxClient.FritzIpEntry entry in ipv6)
                {
                    string status = GetStatus(entry.AddrType);

                    Console.WriteLine(
                        $"    {entry.Ip,-45} {entry.AddrType,-24} {status}");

                    if (StableTypes.Contains(
                            entry.AddrType,
                            StringComparer.OrdinalIgnoreCase))
                    {
                        stableCount++;
                    }
                    else if (TemporaryTypes.Contains(
                                 entry.AddrType,
                                 StringComparer.OrdinalIgnoreCase))
                    {
                        temporaryCount++;
                    }
                    else if (entry.AddrType.StartsWith(
                                 "IPv6-LLA",
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        linkLocalCount++;
                    }
                    else
                    {
                        ipv6OtherCount++;
                    }

                    AppendCsv(
                        csvPath,
                        timestamp,
                        device,
                        entry,
                        status);
                }
            }

            Console.WriteLine();
        }

        Console.WriteLine("Zusammenfassung");
        Console.WriteLine($"  IPv4-Adressen gesamt : {ipv4Count}");
        Console.WriteLine($"  stabile IPv6         : {stableCount}");
        Console.WriteLine($"  temporäre IPv6       : {temporaryCount}");
        Console.WriteLine($"  Link-Local IPv6      : {linkLocalCount}");
        Console.WriteLine($"  sonstige IPv6        : {ipv6OtherCount}");
    }

    private static string GetStatus(string addrType)
    {
        if (StableTypes.Contains(
                addrType,
                StringComparer.OrdinalIgnoreCase))
        {
            return "STABIL";
        }

        if (TemporaryTypes.Contains(
                addrType,
                StringComparer.OrdinalIgnoreCase))
        {
            return "TEMPORÄR";
        }

        if (addrType.StartsWith(
                "IPv6-LLA",
                StringComparison.OrdinalIgnoreCase))
        {
            return "LINK-LOCAL";
        }

        return "SONSTIG";
    }

    private static void AppendCsv(
        string path,
        DateTime timestamp,
        FritzBoxClient.FritzLanDevice device,
        FritzBoxClient.FritzIpEntry entry,
        string status)
    {
        bool newFile = !File.Exists(path);

        using StreamWriter writer =
            new(path, append: true, Encoding.UTF8);

        if (newFile)
        {
            writer.WriteLine(
                "Zeit;Gerät;MAC;IP;AddrType;Status");
        }

        writer.WriteLine(string.Join(";",
            Csv(timestamp.ToString("yyyy-MM-dd HH:mm:ss")),
            Csv(device.FriendlyName ?? device.Name ?? ""),
            Csv(device.Mac),
            Csv(entry.Ip),
            Csv(entry.AddrType),
            Csv(status)));
    }

    private static string Csv(string value)
    {
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static string ReadPassword()
    {
        StringBuilder password = new();

        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
                break;

            if (key.Key == ConsoleKey.Backspace)
            {
                if (password.Length > 0)
                    password.Length--;

                continue;
            }

            if (!char.IsControl(key.KeyChar))
                password.Append(key.KeyChar);
        }

        return password.ToString();
    }
}

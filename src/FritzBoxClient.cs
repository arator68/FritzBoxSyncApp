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

public sealed class FritzBoxClient
{
    private readonly HttpClient _client;
    private readonly App.Config _config;

    internal FritzBoxClient(
        HttpClient client,
        App.Config config)
    {
        _client = client;
        _config = config;
    }

    public static HttpClient CreateHttpClient(
        string username,
        string password)
    {
        HttpClientHandler handler = new()
        {
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,

            Credentials =
                new NetworkCredential(
                    username,
                    password),

            PreAuthenticate = false
        };

        HttpClient client = new(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        return client;
    }

    public async Task<string> GetSidAsync(
        CancellationToken cancellationToken)
    {

        string baseUrl =
            _config.FritzBoxWebUrl.TrimEnd('/');

        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException(
                "TEST: FritzBoxWebUrl ist leer.");

        string loginUrl =
            $"{baseUrl}/login_sid.lua?version=2" +
            $"&username={Uri.EscapeDataString(_config.FritzUsername)}";

        string challengeXml;

        try
        {
            challengeXml =
                await _client.GetStringAsync(
                    loginUrl,
                    cancellationToken);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"FRITZ SID Challenge fehlgeschlagen. " +
                $"URL: {loginUrl}. " +
                $"Fehler: {ex.Message}",
                ex);
        }

        XDocument challengeDoc =
            XDocument.Parse(challengeXml);

        string challenge =
            challengeDoc.Root?
                .Element("Challenge")?
                .Value?
                .Trim()
            ?? throw new InvalidOperationException(
                "FRITZ!Box lieferte keine Login-Challenge.");

        if (!challenge.StartsWith(
                "2$",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Nicht unterstütztes FRITZ!Box Challenge-Format: " +
                challenge);
        }

        string response =
            CreatePbkdf2Response(
                challenge,
                _config.FritzPassword);

        string loginResponseUrl =
            $"{baseUrl}/login_sid.lua?version=2" +
            $"&username={Uri.EscapeDataString(_config.FritzUsername)}" +
            $"&response={Uri.EscapeDataString(response)}";

        string loginXml;

            try
            {
                loginXml =
                    await _client.GetStringAsync(
                        loginResponseUrl,
                        cancellationToken);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"FRITZ SID Login fehlgeschlagen. " +
                    $"URL: {loginResponseUrl}. " +
                    $"Fehler: {ex.Message}",
                    ex);
            }

        XDocument loginDoc =
            XDocument.Parse(loginXml);

        string sid =
            loginDoc.Root?
                .Element("SID")?
                .Value?
                .Trim()
            ?? throw new InvalidOperationException(
                "FRITZ!Box lieferte keine SID.");

        if (sid == "0000000000000000")
        {
            throw new UnauthorizedAccessException(
                "FRITZ!Box Anmeldung fehlgeschlagen.");
        }

        return sid;
    }

    public async Task<List<FritzDevice>> GetHostListAsync(
        CancellationToken cancellationToken)
    {
        string hostListPath =
            await GetHostListPathAsync(
                cancellationToken);

        string hostListUrl =
            BuildHostListUrl(hostListPath);

        string xml =
            await _client.GetStringAsync(
                hostListUrl,
                cancellationToken);

        return ParseHostList(xml);
    }

    public async Task SetFriendlyNameByMacAsync(
        string normalizedMac,
        string friendlyName,
        CancellationToken cancellationToken)
    {
        const string action =
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
                $"{_config.FritzBoxUrl.TrimEnd('/')}/upnp/control/hosts",
                action,
                soap);

        using HttpResponseMessage response =
            await _client.SendAsync(
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

    public async Task<List<FritzLanDevice>> GetLanDevicesAsync(
    string sid,
    CancellationToken cancellationToken)
{
    string url =
        $"{_config.FritzBoxWebUrl.TrimEnd('/')}/api/v0/generic/landevice";

    HttpRequestMessage request =
        new(HttpMethod.Get, url);

    request.Headers.Add(
        "Authorization",
        $"AVM-SID {sid}");

    HttpResponseMessage response =
        await _client.SendAsync(
            request,
            cancellationToken);

    if (!response.IsSuccessStatusCode)
    {
        throw new InvalidOperationException(
            $"FRITZ LAN-Geräte konnten nicht abgerufen werden. " +
            $"URL: {url}. " +
            $"HTTP: {(int)response.StatusCode} " +
            $"{response.ReasonPhrase}");
    }

    string json =
        await response.Content.ReadAsStringAsync(
            cancellationToken);

    using JsonDocument document =
        JsonDocument.Parse(json);

    JsonElement root =
        document.RootElement;

    if (!root.TryGetProperty(
        "landevice",
        out JsonElement devices) ||
    devices.ValueKind != JsonValueKind.Array)
    {
        throw new InvalidOperationException(
            "FRITZ LAN-Geräte Antwort enthält kein gültiges " +
            "'landevice'-Array.");
    }

List<FritzLanDevice> result =
    new();
    
    foreach (JsonElement device in
         devices.EnumerateArray())
    {
        string? mac =
            GetString(device, "mac");

        if (string.IsNullOrWhiteSpace(mac))
            continue;

        string? name =
            GetString(device, "name");

        string? friendlyName =
            GetString(device, "friendlyname");

        List<FritzIpEntry> ipList =
            new();

        if (device.TryGetProperty(
                "iplist",
                out JsonElement ipListElement) &&
            ipListElement.ValueKind ==
                JsonValueKind.Array)
        {
            foreach (
                JsonElement ipContainer
                in ipListElement.EnumerateArray())
            {
                if (!ipContainer.TryGetProperty(
                        "entry",
                        out JsonElement entries))
                {
                    continue;
                }

                if (entries.ValueKind !=
                    JsonValueKind.Array)
                {
                    continue;
                }

                foreach (
                    JsonElement entry
                    in entries.EnumerateArray())
                {
                    string? addrType =
                        GetString(entry, "addrtype");

                    string? ip =
                        GetString(entry, "ip");

                    if (string.IsNullOrWhiteSpace(
                            addrType) ||
                        string.IsNullOrWhiteSpace(ip))
                    {
                        continue;
                    }

                    ipList.Add(
                        new FritzIpEntry(
                            addrType,
                            ip));
                }
            }
        }

        result.Add(
            new FritzLanDevice(
                mac,
                name,
                friendlyName,
                ipList));
    }

    return result;
}

    private async Task<string> GetHostListPathAsync(
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
                $"{_config.FritzBoxUrl.TrimEnd('/')}/upnp/control/hosts",
                action,
                soap);

        using HttpResponseMessage response =
            await _client.SendAsync(
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

    private string BuildHostListUrl(
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
            $"{_config.FritzBoxHttpsUrl.TrimEnd('/')}/" +
            $"{path.TrimStart('/')}";
    }

    private static List<FritzDevice> ParseHostList(
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

    private static string CreatePbkdf2Response(
        string challenge,
        string password)
    {
        string[] parts =
            challenge.Split('$');

        if (parts.Length != 5 ||
            parts[0] != "2")
        {
            throw new FormatException(
                "Ungültiges FRITZ!Box PBKDF2-Challenge-Format.");
        }

        int iterations1 =
            int.Parse(parts[1]);

        byte[] salt1 =
            Convert.FromHexString(parts[2]);

        int iterations2 =
            int.Parse(parts[3]);

        byte[] salt2 =
            Convert.FromHexString(parts[4]);

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

    private static string NormalizeMac(
        string mac)
    {
        return new string(
            mac
                .Where(char.IsLetterOrDigit)
                .ToArray())
            .ToUpperInvariant();
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

    public sealed record FritzDevice(
        string Mac,
        string? IpAddress,
        string? HostName,
        string? FriendlyName);

    public sealed record FritzLanDevice(
        string Mac,
        string? Name,
        string? FriendlyName,
        List<FritzIpEntry> IpList);

    public sealed record FritzIpEntry(
        string AddrType,
        string Ip);
}
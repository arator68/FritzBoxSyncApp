# FritzBoxSync

FritzBoxSync is a Technitium DNS Server application that automatically synchronizes device hostnames from Technitium DHCP with the corresponding Friendly Names on a FRITZ!Box.

## Features

- Reads reserved DHCP leases from Technitium DNS Server.
- Matches devices by MAC address.
- Reads the current device list from the FRITZ!Box.
- Compares Technitium hostnames with FRITZ!Box Friendly Names.
- Updates the FRITZ!Box Friendly Name when a difference is detected.
- Supports dry-run mode for testing without making changes.
- Supports synchronization at application startup and at a configurable interval.
- Uses the Technitium DNS Server API and the FRITZ!Box host service.

## How It Works

The synchronization process is based on the device MAC address:

1. FritzBoxSync retrieves reserved DHCP leases from Technitium.
2. The application retrieves the device list from the FRITZ!Box.
3. Devices are matched using their MAC addresses.
4. The Technitium hostname is compared with the FRITZ!Box Friendly Name.
5. If the names differ, FritzBoxSync updates the Friendly Name on the FRITZ!Box.
6. Devices that cannot be matched are skipped.

Example:

```text
Technitium:
Desktop-PC.example.local

FRITZ!Box:
Desktop-PC

        ↓ synchronization

FRITZ!Box:
Desktop-PC.example.local
```

## Requirements

- Technitium DNS Server 15.5.0 or compatible version
- Technitium DHCP enabled
- A compatible FRITZ!Box with the required host service available
- A FRITZ!Box user with the required permissions
- Network connectivity between the Technitium DNS Server and the FRITZ!Box
- A .NET runtime compatible with the installed Technitium DNS Server application version

## Configuration

The application uses `dnsApp.config`.

Example:

```json
{
  "enabled": true,
  "dryRun": false,
  "runOnStartup": true,
  "intervalMinutes": 15,

  "fritzBoxUrl": "http://192.168.178.1:49000",
  "fritzBoxHttpsUrl": "https://192.168.178.1:49443",
  "fritzUsername": "TechnitiumSync",
  "fritzPassword": "YOUR_PASSWORD",

  "technitiumApiUrl": "http://192.168.178.2:5380",
  "technitiumApiToken": "YOUR_API_TOKEN"
}
```

### Configuration Options

| Option | Description |
|---|---|
| `enabled` | Enables or disables the application. |
| `dryRun` | If `true`, changes are only reported and are not written to the FRITZ!Box. |
| `runOnStartup` | Runs a synchronization when the application starts. |
| `intervalMinutes` | Interval between automatic synchronization runs. |
| `fritzBoxUrl` | FRITZ!Box HTTP control URL. |
| `fritzBoxHttpsUrl` | FRITZ!Box HTTPS URL used for the host list. |
| `fritzUsername` | FRITZ!Box user name used for authentication. |
| `fritzPassword` | FRITZ!Box password. |
| `technitiumApiUrl` | Technitium DNS Server API URL. |
| `technitiumApiToken` | Technitium API bearer token. |

## Dry-Run Mode

For initial testing, set:

```json
"dryRun": true
```

The application will detect and log changes but will not modify the FRITZ!Box.

Once the configuration has been verified, set:

```json
"dryRun": false
```

The application will apply detected changes to the FRITZ!Box.

## Installation

1. Build the application in Release mode.
2. Package the Release output into a ZIP file.
3. Install or update the application through the Technitium DNS Server web interface.
4. Configure `dnsApp.config`.
5. Restart or reload the application.
6. Check the Technitium DNS Server log for FritzBoxSync messages.

A successful synchronization logs the number of reserved Technitium devices and FRITZ!Box devices, followed by any detected changes.

## Building from Source

Open a terminal in the `src` directory:

```powershell
cd "E:\Entwicklung\FritzBoxSyncApp-source-v2\FritzBoxSyncApp\src"
dotnet build .\FritzBoxSyncApp.csproj -c Release
```

The Release files are generated in:

```text
src\bin\Release
```

The Release package should contain at least:

```text
FritzBoxSyncApp.dll
FritzBoxSyncApp.deps.json
dnsApp.config
```

## Logging

FritzBoxSync writes its messages to the Technitium DNS Server log using the prefix:

```text
[FRITZ!Box Sync]
```

A detected change includes the IP address, MAC address, current FRITZ!Box name, and Technitium hostname.

## Current Scope

FritzBoxSync currently focuses on IPv4 devices managed through Technitium DHCP reservations.

IPv6 devices using SLAAC and IPv6 Privacy Extensions are not synchronized in the same way because their addresses are not currently managed through Technitium DHCP reservations.

## Security

Do not publish or commit the following values:

- FRITZ!Box password
- Technitium API token

Use a dedicated FRITZ!Box user for the synchronization application where possible.

## License

FritzBoxSync is licensed under the MIT License.

See the [LICENSE](LICENSE) file for the full license text.

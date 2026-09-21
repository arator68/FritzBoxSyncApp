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
- Runs directly as a Technitium DNS Server application.
- No external service is required.

## Download

Download the latest release from the GitHub Releases page:

[Download FritzBoxSync](../../releases)

The release contains a ready-to-install ZIP package for Technitium DNS Server.

> **Important:** GitHub also provides automatically generated "Source code" ZIP and TAR.GZ files with each release. These are the project source files and are **not** the files to install in Technitium.
>
> Download the `FritzBoxSyncApp-*.zip` release asset instead.

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
Technitium DHCP reservation
        |
        | MAC address
        v
   FritzBoxSync
        |
        v
FRITZ!Box Friendly Name
```

For example:

```text
Technitium:

Desktop-PC.example.local

FRITZ!Box:

Desktop-PC

        ↓ synchronization

FRITZ!Box:

Desktop-PC.example.local
```

The Technitium DHCP reservation hostname is used as the source name.

## Requirements

- Technitium DNS Server 15.5.0
- Technitium DHCP enabled
- A compatible FRITZ!Box with the required host service available
- A FRITZ!Box user with the required permissions
- Network connectivity between the Technitium DNS Server and the FRITZ!Box
- A .NET runtime compatible with the installed Technitium DNS Server application version

## Installation

FritzBoxSync is installed through the Technitium DNS Server web interface.

1. Download the latest `FritzBoxSyncApp-*.zip` from the [GitHub Releases](../../releases) page.
2. Open the Technitium DNS Server web interface.
3. Go to **Apps → Install App**.
4. Select the downloaded `FritzBoxSyncApp-*.zip` file.
5. Install the application.
6. Open the FritzBoxSync application settings.
7. Configure the FRITZ!Box and Technitium connection settings.
8. Start or restart the application if required.
9. Check the Technitium DNS Server log for `[FRITZ!Box Sync]` messages.

No manual extraction into the Technitium application directory is required.

## Configuration

The application can be configured through the Technitium DNS Server application settings.

The configuration is stored in `dnsApp.config`.

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

## Synchronization

FritzBoxSync can synchronize automatically:

- when the application starts, if `runOnStartup` is enabled
- periodically according to `intervalMinutes`

For example:

```json
"runOnStartup": true,
"intervalMinutes": 15
```

causes a synchronization at application startup and then every 15 minutes.

## Logging

FritzBoxSync writes its messages to the Technitium DNS Server log using the prefix:

```text
[FRITZ!Box Sync]
```

A detected change includes the IP address, MAC address, current FRITZ!Box name, and Technitium hostname.

The synchronization summary reports information such as:

- number of reserved Technitium DHCP devices
- number of FRITZ!Box devices
- successfully matched devices
- detected changes
- devices that could not be matched
- errors encountered during synchronization

## Current Scope

FritzBoxSync currently focuses on IPv4 devices managed through Technitium DHCP reservations.

IPv6 devices using SLAAC and IPv6 Privacy Extensions are not synchronized in the same way because their addresses are not currently managed through Technitium DHCP reservations.

## Security

Do not publish or commit the following values:

- FRITZ!Box password
- Technitium API token

Use a dedicated FRITZ!Box user for the synchronization application where possible.

The release package contains configuration placeholders and does not contain the developer's personal credentials or API token.

## Updating

To update FritzBoxSync:

1. Download the latest `FritzBoxSyncApp-*.zip` from the [GitHub Releases](../../releases) page.
2. Open the Technitium DNS Server web interface.
3. Go to **Apps**.
4. Update or reinstall FritzBoxSync using the new ZIP package.
5. Verify the application configuration after the update.
6. Restart the application if required.

## Building from Source

This section is intended for developers who want to build FritzBoxSync themselves.

Clone the repository and the required Technitium repositories so that the directory structure looks like this:

```text
Development/
├── DnsServer/
├── TechnitiumLibrary/
└── FritzBoxSyncApp/
```

The FritzBoxSync project uses project references to the Technitium DNS Server and Technitium Library source trees.

Build the project from the repository root:

```powershell
dotnet build .\src\FritzBoxSyncApp.csproj -c Release
```

The Release files are generated in:

```text
src\bin\Release
```

The release package should contain at least:

```text
FritzBoxSyncApp.dll
FritzBoxSyncApp.deps.json
dnsApp.config
```

The repository also contains a Visual Studio Code build task that can be used to build the required dependencies and FritzBoxSync together.

## Development

The project uses:

- C#
- .NET 10
- Technitium DNS Server application APIs
- Technitium DNS Server DHCP API
- FRITZ!Box UPnP host service

GitHub Actions is used to build release packages automatically.

A tagged version such as:

```text
v1.0.0
```

triggers the release build.

## License

FritzBoxSync is licensed under the MIT License.

See the [LICENSE](LICENSE) file for the full license text.

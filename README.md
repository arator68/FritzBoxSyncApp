**# FritzBoxSync**



FritzBoxSync is a Technitium DNS Server application that automatically synchronizes device hostnames from Technitium DHCP with the corresponding Friendly Names on a FRITZ!Box.



**## FritzBoxSync v1.0.2

### Added

- IPv6 AAAA synchronization
- IPv6 PTR synchronization
- Support for IPv6 GUA and ULA addresses
- Support for multiple stable IPv6 addresses per device
- Automatic IPv6 reverse zone creation
- Automatic PTR record synchronization
- Automatic cleanup of obsolete managed PTR records
- Automatic cleanup of obsolete IPv6 reverse zones
- Handling of changing FRITZ!Box IPv6 prefixes
- Dry-run support for IPv6 PTR and reverse-zone cleanup

### Improved

- Managed IPv6 reverse zones are tracked automatically
- Foreign/manual PTR records are protected from automatic deletion
- IPv6 synchronization logging includes detailed counters
- Reverse zones are deleted only when no other records remain

## Features**



- Reads reserved DHCP leases from Technitium DNS Server.

- Matches devices by MAC address.

- Reads the current device list from the FRITZ!Box.

- Compares Technitium hostnames with FRITZ!Box Friendly Names.

- Updates the FRITZ!Box Friendly Name when a difference is detected.
- Supports IPv6 AAAA synchronization.
- Supports IPv6 PTR synchronization.
- Supports IPv6 Global Unicast Addresses (GUA) and Unique Local Addresses (ULA).
- Supports multiple stable IPv6 addresses per device.
- Automatically creates required IPv6 reverse zones.
- Automatically cleans up obsolete managed IPv6 PTR records and reverse zones after GUA prefix changes.
- Protects PTR records that are not managed by FritzBoxSync from automatic deletion.

- Supports dry-run mode for testing without making changes.

- Supports synchronization at application startup and at a configurable interval.

- Uses the Technitium DNS Server API and the FRITZ!Box host service.

- Runs directly as a Technitium DNS Server application.

- No external service is required.



**## Download**



Download the latest release from the GitHub Releases page:



[Download FritzBoxSync](../../releases)



The release contains a ready-to-install ZIP package for Technitium DNS Server.



> ****Important:**** GitHub also provides automatically generated "Source code" ZIP and TAR.GZ files with each release. These are the project source files and are ****not**** the files to install in Technitium.

>

> Download the `FritzBoxSyncApp-*.zip` release asset instead.



**## How It Works**



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



**## Requirements**



- Technitium DNS Server 15.5.0

- Technitium DHCP enabled

- A compatible FRITZ!Box with the required host service available

- A FRITZ!Box user with the required permissions

- Network connectivity between the Technitium DNS Server and the FRITZ!Box

- A .NET runtime compatible with the installed Technitium DNS Server application version



**## Installation**



FritzBoxSync is installed through the Technitium DNS Server web interface.



1. Download the latest `FritzBoxSyncApp-*.zip` from the [GitHub Releases](../../releases) page.

2. Open the Technitium DNS Server web interface.

3. Go to ****Apps → Install App****.

4. Select the downloaded `FritzBoxSyncApp-*.zip` file.

5. Install the application.

6. Open the FritzBoxSync application settings.

7. Configure the FRITZ!Box and Technitium connection settings.

8. Start or restart the application if required.

9. Check the Technitium DNS Server log for `[FRITZ!Box Sync]` messages.



No manual extraction into the Technitium application directory is required.



**## Configuration**



The application can be configured through the Technitium DNS Server application settings.



The configuration is stored in `dnsApp.config`.



Example:



```json
{
  "enabled": true,
  "dryRun": true,
  "runOnStartup": true,

  "enableIpv6Sync": true,
  "enableIpv6PtrSync": true,
  "managedIpv6ReverseZones": [],

  "ipv6Ttl": 3600,
  "ipv6PtrTtl": 3600,

  "intervalMinutes": 15,

  "fritzBoxUrl": "http://YOUR_FRITZBOX_IP:49000",
  "fritzBoxHttpsUrl": "https://YOUR_FRITZBOX_IP:49443",
  "fritzBoxWebUrl": "http://YOUR_FRITZBOX_IP",

  "fritzUsername": "YOUR_FRITZ_USERNAME",
  "fritzPassword": "YOUR_FRITZ_PASSWORD",

  "technitiumApiUrl": "http://YOUR_TECHNITIUM_IP:5380",
  "technitiumDnsZone": "YOUR_DNS_ZONE",
  "technitiumApiToken": "YOUR_API_TOKEN"
}
```



**### Configuration Options

| Option | Description |
| --- | --- |
| `enabled` | Enables or disables the application. |
| `dryRun` | If `true`, changes are only reported and no changes are made. |
| `runOnStartup` | Runs a synchronization when the application starts. |
| `enableIpv6Sync` | Enables or disables IPv6 AAAA synchronization. |
| `enableIpv6PtrSync` | Enables or disables IPv6 PTR synchronization. |
| `managedIpv6ReverseZones` | List of IPv6 reverse zones currently managed by FritzBoxSync. The application maintains this list automatically. |
| `ipv6Ttl` | TTL in seconds for synchronized IPv6 AAAA records. |
| `ipv6PtrTtl` | TTL in seconds for synchronized IPv6 PTR records. |
| `intervalMinutes` | Interval between automatic synchronization runs. |
| `fritzBoxUrl` | FRITZ!Box HTTP control URL. |
| `fritzBoxHttpsUrl` | FRITZ!Box HTTPS URL used for the host list. |
| `fritzBoxWebUrl` | FRITZ!Box web interface URL. |
| `fritzUsername` | FRITZ!Box user name used for authentication. |
| `fritzPassword` | FRITZ!Box password. |
| `technitiumApiUrl` | Technitium DNS Server API URL. |
| `technitiumDnsZone` | DNS zone used for synchronized DNS records. |
| `technitiumApiToken` | Technitium API token. |

The `managedIpv6ReverseZones` list is maintained automatically by FritzBoxSync and normally does not need to be edited manually.

When a required reverse zone does not exist, FritzBoxSync creates it and registers it as managed. Existing reverse zones containing FritzBoxSync-managed PTR records can also be recognized as managed.

## Dry-Run Mode**



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



**## Synchronization**



FritzBoxSync can synchronize automatically:



- when the application starts, if `runOnStartup` is enabled

- periodically according to `intervalMinutes`



For example:



```json

"runOnStartup": true,

"intervalMinutes": 15

```



causes a synchronization at application startup and then every 15 minutes.



**## Logging**



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



The IPv6 synchronization summary reports information such as:



- number of devices with stable IPv6 addresses

- number of devices without a stable IPv6 address

- number of AAAA records already correct

- number of AAAA records created

- number of AAAA records updated

- number of outdated AAAA records deleted

- number of synchronization errors



**## Current Scope**



FritzBoxSync synchronizes devices managed through Technitium DHCP reservations.



**### IPv4 Synchronization**



For IPv4 synchronization:



- Technitium DHCP reservations are used as the source of device hostnames.

- Devices are matched with FRITZ!Box devices using their MAC addresses.

- The Technitium hostname is synchronized to the FRITZ!Box Friendly Name.



**### IPv6 Synchronization**



FritzBoxSync also synchronizes stable IPv6 addresses for devices that are found in both Technitium DHCP reservations and the FRITZ!Box LAN device list.



The application supports:



- IPv6 Global Unicast Addresses (GUA)

- IPv6 Unique Local Addresses (ULA)

- Multiple stable IPv6 addresses per device

- Automatic creation of missing AAAA records

- Updating of existing AAAA records

- Removal of outdated AAAA records

- Configurable IPv6 TTL

- Synchronization of Technitium DNS record comments



IPv6 addresses are matched to devices using their MAC addresses. Temporary or privacy-related IPv6 addresses are not treated as stable addresses.



If a device currently has no stable IPv6 address, existing AAAA records are not automatically deleted. This prevents temporary loss of IPv6 connectivity from causing unwanted DNS record deletion.


### IPv6 PTR Synchronization

FritzBoxSync can maintain reverse DNS PTR records for stable IPv6 addresses.

The application supports:

- IPv6 Global Unicast Addresses (GUA)
- IPv6 Unique Local Addresses (ULA)
- Multiple PTR records for a device
- Automatic creation of required `ip6.arpa` reverse zones
- Automatic creation of missing PTR records
- Updating of existing managed PTR records
- Removal of outdated managed PTR records
- Automatic cleanup of obsolete managed reverse zones

FritzBoxSync identifies managed PTR records using the comment prefix:

```text
FritzBoxSync - FRITZ!Box:
```

Only PTR records carrying this management comment are considered for automatic obsolete-record cleanup. Other PTR records are not automatically deleted.

### IPv6 Prefix Changes

A FRITZ!Box IPv6 Global Unicast prefix can change after an Internet reconnect or a FRITZ!Box restart.

When the prefix changes, FritzBoxSync automatically:

1. Detects the new stable IPv6 addresses.
2. Creates or updates the corresponding AAAA records.
3. Creates the required new IPv6 reverse zone.
4. Creates the corresponding PTR records.
5. Detects previously managed reverse zones that are no longer required.
6. Removes obsolete FritzBoxSync-managed PTR records from those zones.
7. Deletes an obsolete reverse zone only when no other records remain.
8. Removes the obsolete zone from the managed reverse-zone list.

Reverse zones containing other records are kept.



**## Security**



Do not publish or commit the following values:



- FRITZ!Box password

- Technitium API token



Use a dedicated FRITZ!Box user for the synchronization application where possible.



The release package contains configuration placeholders and does not contain the developer's personal credentials or API token.



**## Updating**



To update FritzBoxSync:



1. Download the latest `FritzBoxSyncApp-*.zip` from the [GitHub Releases](../../releases) page.

2. Open the Technitium DNS Server web interface.

3. Go to ****Apps****.

4. Update or reinstall FritzBoxSync using the new ZIP package.

5. Verify the application configuration after the update.

6. Restart the application if required.



**## Building from Source**



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



**## Development**



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



**## License**



FritzBoxSync is licensed under the MIT License.



See the [LICENSE](LICENSE) file for the full license text.

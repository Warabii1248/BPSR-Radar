# Third-party notices

BPSR-Radar is MIT licensed (see `LICENSE`). The released archive is a
self-contained build, so it also contains the libraries below and the .NET
runtime. Their licences are their own.

License identifiers were read from each package's own metadata rather than
from memory; the versions are the ones the release is built against.

| Component | Version | License | Source |
|---|---|---|---|
| Google.Protobuf | 3.33.0 | BSD-3-Clause | https://github.com/protocolbuffers/protobuf |
| Microsoft.Extensions.ObjectPool | 10.0.0-rc.2.25502.107 | MIT | https://github.com/dotnet/runtime |
| PacketDotNet | 1.4.9-pre49 | **MPL-2.0** | https://github.com/dotpcap/packetnet |
| Serilog | 4.3.1-dev-02387 | **Apache-2.0** | https://github.com/serilog/serilog |
| SharpPcap | 6.3.1 | MIT | https://github.com/chmorgan/sharppcap |
| ZstdSharp.Port | 0.8.6 | MIT | https://github.com/oleg-st/ZstdSharp |
| .NET runtime | 9.0 | MIT | https://github.com/dotnet/runtime |

## PacketDotNet (MPL-2.0)

`PacketDotNet.dll` is redistributed unmodified. The Mozilla Public License
2.0 requires that its source be available to anyone who receives the binary:
it is at https://github.com/dotpcap/packetnet, and the full licence text is
at https://mozilla.org/MPL/2.0/. No PacketDotNet file has been modified, and
nothing in BPSR-Radar's own source is covered by the MPL.

## Serilog (Apache-2.0)

`Serilog.dll` is redistributed unmodified. The Apache License 2.0 text is at
https://www.apache.org/licenses/LICENSE-2.0. The project carries no NOTICE
file, so there is nothing further to reproduce here.

## Npcap

Npcap is **not** included and is not redistributed. It is installed
separately by the user from https://npcap.com/ and is covered by its own
licence, which does not permit redistribution without a licence from the
Nmap Project. BPSR-Radar only calls it through SharpPcap.

## Game data

`BPSR-Radar/Data/*.json` are name and type tables extracted from the game
client, used to display entity names. They are game content and are not
covered by this project's licence.

## Upstream

Parts of this project are derived from an MIT-licensed open-source project;
that copyright notice is retained in `LICENSE`.

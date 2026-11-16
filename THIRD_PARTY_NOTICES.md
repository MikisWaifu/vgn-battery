# Third-party notices

## Protocol research references

The C# application in this repository is independently implemented. USB device
identifiers and the Nordic52 / CompX and Keychron battery-report formats were
researched using:

- [Fan4Metal/mouse_tray: Nordic52 device registry and protocol description](https://github.com/Fan4Metal/mouse_tray/blob/master/mouse_tray/drivers/chipset/nordic52.py)
- [HeyOkay/HaloBattery: additional CompX / Dragonfly receiver observations](https://github.com/HeyOkay/HaloBattery/blob/main/providers/pulsar.py)
- [VGN official Web HUB](https://drive.vgnlab.com.cn/): F2 Master GT device identifiers and its 17-byte battery report format (registry/control logic observed in `assets/index-BfN6tzNB.js`). Official JavaScript is not redistributed.
- [csutcliff/keychron-battery-dkms](https://github.com/csutcliff/keychron-battery-dkms): Keychron's 64-byte battery-status report, interface identifiers and battery-field layout. Used as a protocol research reference; its source code is not copied or bundled.

These projects' source files and executables are not included in this repository
or the release ZIP. Their own licenses remain applicable to their code. The MIT
license at the root of this repository does not relicense upstream projects.

VGN, VXE, ATK and Keychron are used only to identify compatible products. This is an
unofficial project, not endorsed by the hardware manufacturers.

## Bundled Microsoft .NET runtime

The self-contained Windows release bundles Microsoft.NETCore.App and
Microsoft.WindowsDesktop.App. Their package license files and the .NET runtime's
third-party notices are included under `licenses/` in each release ZIP.

`build-release.ps1` obtains these files from the exact runtime package versions
recorded in the current publish output. The source build uses the installed
.NET SDK; the SDK itself is not redistributed.

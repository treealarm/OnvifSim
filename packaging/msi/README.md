# MSI

WiX 5 installer for Windows x64, built by the **msi** job of `.github/workflows/release.yml` from the
unpacked `OnvifSim-win-x64.zip`. It installs into `Program Files\TreeAlarm\OnvifSim` with a Start menu
entry that starts the simulator (a console window with its log) and opens the editor in the browser.
The world stays per user in `%LOCALAPPDATA%\OnvifSim`, so uninstalling keeps it.

The icon (`src/OnvifSim.Server/OnvifSim.ico`, also built into `OnvifSim.exe`) and the dialog pictures
`Banner.bmp` (493×58) and `Dialog.bmp` (493×312) are made from the snap's `icon.png`.

Build it on Windows with the .NET SDK:

```powershell
Expand-Archive OnvifSim-win-x64.zip -DestinationPath payload
dotnet build packaging/msi/OnvifSim.Setup.wixproj -c Release -p:Platform=x64 -p:PayloadDir=$PWD/payload/OnvifSim-win-x64/ -p:Version=0.1.0
```

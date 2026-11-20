# Device Lab

Device Lab is a Windows app for Android emulator testing. It creates virtual devices, installs test APKs, reads logs, and saves GPS test coordinates for each device.

## Install on Windows

1. Download **DeviceLab-setup-win-x64.exe** from [GitHub Releases](https://github.com/mrmaftahsa/emulat/releases/latest) and run it.
2. Open Device Lab. If it opens **Setup**, click **Set up Android**, then **Next**. Review Google’s Android SDK terms if prompted and click **Install**. Device Lab installs Java and the Android components it needs automatically.
3. If Device Lab asks for Windows virtualization, click **Enable Windows virtualization**. Approve the Windows prompt and restart if asked. Device Lab checks again when it opens. [Windows virtualization help](https://developer.android.com/studio/run/emulator-acceleration#vm-windows)
4. Click **New device** and **Create and start** to use the recommended settings. Once Android connects, you can install a test APK or apply GPS test coordinates.

A clean setup uses about 2–3 GB of downloads and needs at least 9 GB of free space on the Android SDK drive. An existing Android SDK can be reused. Device Lab keeps its downloaded Java runtime in your user profile and uses it automatically. Code signing is not configured.

## Build and extend

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) on Windows and run:

```powershell
dotnet run --project src/DeviceLab/DeviceLab.csproj
```

The **Build Windows app** workflow builds the x64 installer and tests Windows startup. Release builds also test provisioning a clean SDK and creating an AVD before publishing downloads. From **Actions**, enter the version in the workflow dispatch form to publish a release, or select **Verify a clean Android download and AVD creation** to run the check on its own.

- `MainWindow.xaml` and `MainWindow.xaml.cs` contain the device controls and setup entry points.
- `SetupWindow.xaml` and `AndroidSetup.cs` guide users through downloading the dependencies.
- `AndroidTools.cs` calls the official emulator, ADB, and AVD manager tools.
- `Preferences.cs` saves the SDK path and per-device coordinates to `%LOCALAPPDATA%\DeviceLab\settings.json`.
- `installer/DeviceLab.iss` defines the Windows installer.

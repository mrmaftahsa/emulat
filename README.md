# Device Lab

Device Lab is a Windows desktop app for running repeatable Android emulator tests. It uses the official Android SDK and keeps each device’s saved test coordinates on your PC.

## Install on Windows

Open this repository’s **Actions** tab, choose the latest successful **Build Windows app** run, and download the **DeviceLab-windows-x64** artifact. Unzip it, then choose **DeviceLab-setup-win-x64.exe** to install the app or extract **DeviceLab-win-x64.zip** and launch **DeviceLab.exe** for a portable build. Signing is not configured for these builds.

1. Install [Android Studio](https://developer.android.com/studio). In **SDK Manager**, install **Android Emulator**, **Android SDK Platform-Tools**, **Android SDK Command-line Tools**, and an x86-64 Android system image.
2. Open **SDK setup** in Device Lab. Confirm the SDK folder and click **Save and run diagnostics**. Device Lab can use the JDK bundled with a standard Android Studio installation or one set in `JAVA_HOME`.
3. Open **Devices** and click **New device**. Select an installed system image and create an AVD. Existing AVDs appear automatically when they use the same Android SDK user environment.
4. Start a device. Once ADB connects, install a test APK, read the latest logcat entries, or enter latitude and longitude under **Test location**. Use **Save location** to keep coordinates and **Apply to emulator** to send them for the current run.

Device Lab targets Windows 10 build 19041 or newer on x64. For emulator acceleration, follow the [Android Emulator acceleration guide](https://developer.android.com/studio/run/emulator-acceleration#vm-windows). The Android SDK and system images are installed separately from Device Lab.

## Build and extend

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) on Windows and run:

```powershell
dotnet run --project src/DeviceLab/DeviceLab.csproj
```

For a self-contained build:

```powershell
dotnet publish src/DeviceLab/DeviceLab.csproj -c Release -r win-x64 --self-contained true -o publish
```

The GitHub Actions workflow builds and tests the Windows installer on pushes to `main`, pull requests, and manual runs. The installation recipe lives in `installer/DeviceLab.iss`.

- `MainWindow.xaml` and `MainWindow.xaml.cs` contain the desktop screens and workflows.
- `AndroidTools.cs` calls the official emulator, ADB, and AVD manager tools.
- `Preferences.cs` saves the SDK path and per-device coordinates to `%LOCALAPPDATA%\DeviceLab\settings.json`.

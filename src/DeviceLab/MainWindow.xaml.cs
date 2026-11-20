using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DeviceLab;

public partial class MainWindow : Window
{
    private readonly Preferences preferences;
    private readonly DispatcherTimer pollTimer;
    private AndroidTools tools;
    private MEmuTools? memu;
    private CancellationTokenSource? memuDownload;
    private bool useMEmu;
    private IReadOnlyList<AndroidDevice> devices = [];
    private Task currentPoll = Task.CompletedTask;
    private bool busy;
    private bool setupReady;
    private bool accelerationReady;
    private bool closed;
    private bool updatingSelection;
    private string? displayedDevice;
    private bool CanListDevices => useMEmu ? memu?.IsInstalled == true : tools.HasEmulator;
    private bool CanControlDevices => useMEmu ? memu?.IsInstalled == true : tools.HasAdb;
    private AndroidDevice? SelectedDevice => DevicesList.SelectedItem as AndroidDevice;

    public MainWindow()
    {
        preferences = Preferences.Load(out var warning);
        var savedSdk = preferences.SdkDirectory;
        tools = new(savedSdk is not null && Path.IsPathFullyQualified(savedSdk)
            ? savedSdk : AndroidTools.FindSdk());
        useMEmu = preferences.UseMEmu ?? !tools.HasEmulator;
        pollTimer = new() { Interval = TimeSpan.FromSeconds(5) };
        pollTimer.Tick += PollTick;
        InitializeComponent();
        SdkPath.Text = tools.SdkDirectory;
        EmulatorChoice.SelectedIndex = useMEmu ? 0 : 1;
        UpdateDeviceList();
        SetStatus(warning ?? "Ready.");
    }

    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        await PerformAsync("Checking this PC…", async () =>
        {
            if (await FindMEmuAsync() is { } executable)
            {
                memu = new(executable);
                if (preferences.UseMEmu is null)
                {
                    useMEmu = true;
                    EmulatorChoice.SelectedIndex = 0;
                }
            }

            if (closed)
                return;
            await UpdateEnvironmentAsync();
            if (!setupReady || (!useMEmu && !accelerationReady))
                SetupNav.IsChecked = true;
            await RefreshDevicesAsync();
        });
        if (!closed)
            pollTimer.Start();
    }

    private void WindowClosed(object? sender, EventArgs e)
    {
        closed = true;
        pollTimer.Stop();
        memuDownload?.Cancel();
    }

    private void NavigationChanged(object sender, RoutedEventArgs e)
    {
        if (DevicePage is null || SetupPage is null)
            return;

        var showSetup = SetupNav.IsChecked == true;
        DevicePage.Visibility = showSetup ? Visibility.Collapsed : Visibility.Visible;
        SetupPage.Visibility = showSetup ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void EmulatorChoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || busy || (EmulatorChoice.SelectedIndex == 0) == useMEmu)
            return;

        await PerformAsync("Switching emulator…", async () =>
        {
            useMEmu = EmulatorChoice.SelectedIndex == 0;
            preferences.UseMEmu = useMEmu;
            devices = [];
            UpdateDeviceList();
            if (useMEmu && memu?.IsInstalled != true
                && await FindMEmuAsync() is { } detected)
                memu = new(detected);

            await UpdateEnvironmentAsync();
            if (setupReady && (useMEmu || accelerationReady))
                DevicesNav.IsChecked = true;
            else
                SetupNav.IsChecked = true;
            preferences.Save();
            await RefreshDevicesAsync();
        });
    }

    private async void FindMEmuClicked(object sender, RoutedEventArgs e)
    {
        await PerformAsync("Looking for MEmu on this PC…", async () =>
        {
            var executable = await FindMEmuAsync();
            if (executable is null)
            {
                SetStatus("MEmu wasn’t found. Open your existing copy and try Find installed MEmu again, or click Install MEmu.");
                return;
            }

            await ConnectMEmuAsync(executable);
        });
    }

    private async void InstallMEmuClicked(object sender, RoutedEventArgs e)
    {
        await PerformAsync("Checking for an existing MEmu installation…", async () =>
        {
            if (await FindMEmuAsync() is { } existing)
            {
                await ConnectMEmuAsync(existing);
                return;
            }

            if (closed)
                return;

            using var cancellation = new CancellationTokenSource();
            var directory = Directory.CreateTempSubdirectory("DeviceLab-MEmu-");
            memuDownload = cancellation;
            MEmuProgressPanel.Visibility = Visibility.Visible;
            CancelMEmuButton.Visibility = Visibility.Visible;
            CancelMEmuButton.IsEnabled = true;
            SetMEmuProgress(new("Downloading the official MEmu installer…", 0));
            try
            {
                var path = Path.Combine(directory.FullName, "MEmu-Setup.exe");
                await MEmuInstaller.DownloadAsync(path, new Progress<SetupProgress>(progress =>
                {
                    if (!closed && memuDownload == cancellation && !cancellation.IsCancellationRequested)
                        SetMEmuProgress(progress);
                }), cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();

                memuDownload = null;
                CancelMEmuButton.Visibility = Visibility.Collapsed;
                SetMEmuProgress(new("Finish the MEmu Setup window. Device Lab will connect when setup completes."));
                var result = await MEmuInstaller.RunAsync(path);
                if (closed)
                    return;
                if (result is 3010 or 1641)
                {
                    SetStatus("Restart Windows, then open Device Lab to finish connecting to MEmu.");
                    return;
                }

                SetMEmuProgress(new("Connecting Device Lab to MEmu…"));
                var executable = await FindMEmuAsync();
                for (var attempt = 0; !closed && executable is null && result == 0 && attempt < 8; attempt++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2));
                    executable = await FindMEmuAsync();
                }

                if (closed)
                    return;
                if (executable is null)
                {
                    SetStatus("Setup closed before Device Lab could find MEmu. Finish any open setup window, then click Find installed MEmu.", error: true);
                    return;
                }
                await ConnectMEmuAsync(executable);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                if (!closed)
                    SetStatus("Download canceled. Click Install MEmu to try again.");
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                if (!closed)
                    SetStatus("The MEmu download was interrupted. Check your internet connection and try again.", error: true);
            }
            finally
            {
                memuDownload = null;
                if (!closed)
                    MEmuProgressPanel.Visibility = Visibility.Collapsed;
                try
                {
                    directory.Delete(recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A finishing installer can still have the download open.
                }
            }
        });
    }

    private void CancelMEmuClicked(object sender, RoutedEventArgs e)
    {
        memuDownload?.Cancel();
        CancelMEmuButton.IsEnabled = false;
        SetMEmuProgress(new("Canceling download…"));
    }

    private void SetMEmuProgress(SetupProgress progress)
    {
        MEmuProgressBar.IsIndeterminate = progress.Percent is null;
        MEmuProgressBar.Value = progress.Percent ?? 0;
        MEmuProgressText.Text = progress.Percent is { } percent ? $"{progress.Message} {percent}%" : progress.Message;
    }

    private Task<string?> FindMEmuAsync()
    {
        var savedPath = preferences.MEmuExecutable;
        return Task.Run(() => MEmuTools.FindExecutable(savedPath));
    }

    private async void BrowseMEmuClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the MEmu installation folder" };
        if (memu is not null)
            dialog.InitialDirectory = Path.GetDirectoryName(memu.Executable) ?? "";
        if (dialog.ShowDialog(this) != true)
            return;

        await PerformAsync("Connecting to MEmu…", async () =>
        {
            var executable = MEmuTools.FindInFolder(dialog.FolderName)
                ?? throw new InvalidOperationException("Choose your MEmu installation folder. It should contain memuc.exe.");
            await ConnectMEmuAsync(executable);
        });
    }

    private async Task ConnectMEmuAsync(string executable)
    {
        if (closed)
            return;

        var detected = new MEmuTools(executable);
        var available = await detected.ListDevicesAsync();
        if (closed)
            return;

        preferences.MEmuExecutable = executable;
        preferences.UseMEmu = true;
        preferences.Save();

        memu = detected;
        devices = [];
        UpdateDeviceList();
        devices = available;
        await UpdateEnvironmentAsync();
        UpdateDeviceList();
        DevicesNav.IsChecked = true;
        SetStatus("Connected to MEmu.");
    }

    private void OpenSetupClicked(object sender, RoutedEventArgs e) => SetupNav.IsChecked = true;

    private void SearchChanged(object sender, TextChangedEventArgs e)
    {
        if (DevicesList is not null)
            UpdateDeviceList();
    }

    private void DeviceSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!updatingSelection)
            ShowSelectedDevice();
    }

    private async void RefreshClicked(object sender, RoutedEventArgs e) =>
        await PerformAsync("Refreshing virtual devices…", () => RefreshDevicesAsync(), "Device list refreshed.");

    private void PollTick(object? sender, EventArgs e)
    {
        if (!busy && currentPoll.IsCompleted && DevicesNav.IsChecked == true && CanListDevices)
            currentPoll = PollDevicesAsync();
    }

    private async Task PollDevicesAsync()
    {
        try
        {
            await RefreshDevicesAsync();
        }
        catch (Exception ex) when (IsOperationError(ex))
        {
            // An explicit refresh reports tool errors without interrupting other workflows.
        }
    }

    private async Task RefreshDevicesAsync(string? selectName = null, int? selectMEmuIndex = null)
    {
        IReadOnlyList<AndroidDevice> fresh = [];
        if (CanListDevices)
            fresh = useMEmu ? await memu!.ListDevicesAsync() : await tools.ListDevicesAsync();
        if (closed)
            return;
        if (selectName is not null || selectMEmuIndex is not null || !fresh.SequenceEqual(devices))
        {
            devices = fresh;
            UpdateDeviceList(selectName, selectMEmuIndex);
        }
        if (!CanListDevices)
            SetStatus(useMEmu ? "Open Setup and click Install MEmu." : "Choose Set up Android to get started.");
    }

    private void UpdateDeviceList(string? selectName = null, int? selectMEmuIndex = null)
    {
        var previousName = selectName ?? SelectedDevice?.Name;
        var previousIndex = selectMEmuIndex ?? SelectedDevice?.MEmuIndex;
        var filter = SearchBox.Text.Trim();
        var visible = devices.Where(device => device.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || device.Status.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();

        updatingSelection = true;
        DevicesList.ItemsSource = visible;
        DevicesList.SelectedItem = visible.FirstOrDefault(device => useMEmu
            ? previousIndex is not null && device.MEmuIndex == previousIndex : device.Name == previousName)
            ?? visible.FirstOrDefault();
        updatingSelection = false;

        DeviceCount.Text = $"{devices.Count} registered  ·  {devices.Count(device => device.IsOnline)} running";
        EmptyList.Text = filter.Length > 0 ? "No devices match this filter." : "No virtual devices found.";
        EmptyList.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowSelectedDevice();
    }

    private void ShowSelectedDevice()
    {
        var selected = SelectedDevice;
        DeviceDetails.Visibility = selected is null ? Visibility.Collapsed : Visibility.Visible;
        EmptyDetail.Visibility = selected is null ? Visibility.Visible : Visibility.Collapsed;
        OpenSetupButton.Visibility = setupReady ? Visibility.Collapsed : Visibility.Visible;
        CreateFirstButton.Visibility = setupReady && devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = !setupReady ? (useMEmu ? "Connect to MEmu" : "Set up Android")
            : devices.Count > 0 ? "Select a virtual device" : "You’re ready to create a device";
        EmptyHint.Text = !setupReady ? (useMEmu ? "Install MEmu from Setup to get started."
                : "Device Lab can download the tools this PC needs.")
            : devices.Count > 0 ? "Clear the filter to see all registered devices."
            : useMEmu ? "Create a device with MEmu’s default Android version."
                : "Use the recommended Android version to get started.";
        LocationCard.Visibility = useMEmu ? Visibility.Collapsed : Visibility.Visible;

        if (selected is not null)
        {
            SelectedName.Text = selected.Name;
            SelectedStatus.Text = selected.MEmuIndex is { } index ? $"{selected.Status}  ·  MEmu #{index}"
                : selected.Serial is null ? selected.Status : $"{selected.Status}  ·  {selected.Serial}";
            LaunchError.Text = selected.LaunchError;
            LaunchError.Visibility = selected.LaunchError is null ? Visibility.Collapsed : Visibility.Visible;
        }

        var deviceKey = selected?.MEmuIndex is { } memuIndex ? $"MEmu:{memuIndex}" : selected?.Name;
        if (displayedDevice != deviceKey)
        {
            displayedDevice = deviceKey;
            var location = !useMEmu && selected is not null && preferences.Locations.TryGetValue(selected.Name, out var saved)
                ? saved : null;
            Latitude.Text = location?.Latitude.ToString(CultureInfo.CurrentCulture) ?? "";
            Longitude.Text = location?.Longitude.ToString(CultureInfo.CurrentCulture) ?? "";
            Logs.Clear();
            Logs.Visibility = Visibility.Collapsed;
        }

        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var device = SelectedDevice;
        var online = device?.IsOnline == true;
        var canControl = !busy && CanControlDevices;
        var canSetLocation = !useMEmu && device is not null && TryReadLocation(out _);
        RefreshButton.IsEnabled = !busy && CanListDevices;
        EmulatorChoice.IsEnabled = !busy;
        BrowseMEmuButton.IsEnabled = !busy;
        InstallMEmuButton.IsEnabled = !busy;
        FindMEmuButton.IsEnabled = !busy;
        NewButton.IsEnabled = !busy && setupReady;
        CreateFirstButton.IsEnabled = !busy && setupReady;
        GuidedSetupButton.IsEnabled = !busy;
        EnableVirtualizationButton.IsEnabled = !busy;
        CheckVirtualizationButton.IsEnabled = !busy;
        DeviceEditor.IsEnabled = !busy;
        BrowseButton.IsEnabled = !busy;
        SdkPath.IsEnabled = !busy;
        DiagnosticsButton.IsEnabled = !busy;
        StartButton.IsEnabled = canControl && device?.Status == "Stopped";
        StopButton.IsEnabled = canControl && (useMEmu ? online : device?.Serial is not null);
        InstallButton.IsEnabled = canControl && online;
        LogsButton.IsEnabled = canControl && online;
        SaveLocationButton.IsEnabled = !busy && canSetLocation;
        ApplyLocationButton.IsEnabled = canControl && online && canSetLocation;
        WorkProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task UpdateEnvironmentAsync()
    {
        AndroidSetupSections.Visibility = useMEmu ? Visibility.Collapsed : Visibility.Visible;
        MEmuSetupCard.Visibility = useMEmu ? Visibility.Visible : Visibility.Collapsed;
        if (useMEmu)
        {
            setupReady = memu?.IsInstalled == true;
            MEmuSummary.Text = setupReady ? "MEmu is connected. Open Devices to use your emulators."
                : "Device Lab can download the official MEmu installer (about 770 MB). Follow the installer’s prompts to finish setup.";
            InstallMEmuButton.Visibility = setupReady ? Visibility.Collapsed : Visibility.Visible;
            FindMEmuButton.Visibility = setupReady ? Visibility.Collapsed : Visibility.Visible;
            ShowSelectedDevice();
            return;
        }

        var plan = await new AndroidSetup(tools).PlanAsync();
        setupReady = plan.IsReady;
        SetupSummary.Text = setupReady ? "The Android tools are ready."
            : !plan.NeedsAndroid ? "Install Java automatically to create new virtual devices."
            : "Device Lab can download and install the Android components for you.";
        GuidedSetupButton.Visibility = setupReady ? Visibility.Collapsed : Visibility.Visible;

        accelerationReady = false;
        VirtualizationCard.Visibility = Visibility.Collapsed;
        if (tools.HasEmulator)
        {
            var acceleration = await tools.CheckAccelerationAsync();
            accelerationReady = acceleration.Status == "Ready";
            VirtualizationCard.Visibility = accelerationReady ? Visibility.Collapsed : Visibility.Visible;
            VirtualizationSummary.Text = "Enable Windows virtualization to run Android. Windows may ask for administrator approval and a restart.";
        }

        ShowSelectedDevice();
    }

    private async void GuidedSetupClicked(object sender, RoutedEventArgs e)
    {
        await PerformAsync("Checking Android setup…", async () =>
        {
            var setup = new AndroidSetup(tools);
            var plan = await setup.PlanAsync();
            var finished = plan.IsReady || new SetupWindow(setup, plan) { Owner = this }.ShowDialog() == true;
            await UpdateEnvironmentAsync();
            await RefreshDevicesAsync();
            if (!finished)
                return;

            if (accelerationReady)
            {
                DevicesNav.IsChecked = true;
                SetStatus("Android is ready. Choose New device to get started.");
            }
            else
            {
                SetStatus("Android is ready. Finish the Windows virtualization step to start a device.");
            }
        });
    }

    private async void EnableVirtualizationClicked(object sender, RoutedEventArgs e)
    {
        await PerformAsync("Waiting for Windows…", async () =>
        {
            await AndroidSetup.EnableWindowsVirtualizationAsync();
            VirtualizationSummary.Text = "Windows virtualization was enabled. Restart your PC, then open Device Lab again.";
        }, "Restart your PC to finish setting up virtualization.");
    }

    private async void CheckVirtualizationClicked(object sender, RoutedEventArgs e)
    {
        await PerformAsync("Checking virtualization…", async () =>
        {
            await UpdateEnvironmentAsync();
            if (setupReady && accelerationReady)
                DevicesNav.IsChecked = true;
        });
    }

    private void VirtualizationHelpClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(
                "https://developer.android.com/studio/run/emulator-acceleration#vm-windows") { UseShellExecute = true });
        }
        catch (Win32Exception ex)
        {
            SetStatus(ex.Message, error: true);
        }
    }

    private void BrowseClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the Android SDK folder" };
        if (Directory.Exists(SdkPath.Text))
            dialog.InitialDirectory = SdkPath.Text;
        if (dialog.ShowDialog(this) == true)
            SdkPath.Text = dialog.FolderName;
    }

    private void SdkPathChanged(object sender, TextChangedEventArgs e)
    {
        if (DiagnosticsList is null || DiagnosticsHint is null)
            return;
        DiagnosticsList.ItemsSource = null;
        DiagnosticsHint.Text = "Save this path and run diagnostics to check the environment.";
    }

    private async void DiagnosticsClicked(object sender, RoutedEventArgs e)
    {
        await PerformAsync("Checking the Android SDK…", async () =>
        {
            var candidate = SdkPath.Text.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(candidate))
                throw new InvalidOperationException("Choose an absolute path to the Android SDK folder.");

            var updated = new AndroidTools(candidate);
            var checks = await updated.DiagnoseAsync();
            var previous = preferences.SdkDirectory;
            preferences.SdkDirectory = updated.SdkDirectory;
            try
            {
                preferences.Save();
            }
            catch
            {
                preferences.SdkDirectory = previous;
                throw;
            }

            if (!string.Equals(tools.SdkDirectory, updated.SdkDirectory, StringComparison.OrdinalIgnoreCase))
            {
                tools = updated;
                devices = [];
                UpdateDeviceList();
            }

            SdkPath.Text = tools.SdkDirectory;
            DiagnosticsList.ItemsSource = checks;
            DiagnosticsHint.Text = $"{checks.Count(check => check.Status == "Ready")} ready  ·  {checks.Count(check => check.Status == "Check")} need attention";

            await UpdateEnvironmentAsync();
            if (tools.HasEmulator)
                await RefreshDevicesAsync();
        }, "Diagnostics complete.");
    }

    private async void NewDeviceClicked(object sender, RoutedEventArgs e)
    {
        if (busy)
            return;
        if (useMEmu)
        {
            await PerformAsync("Creating a MEmu device…", async () =>
            {
                var device = await memu!.CreateDeviceAsync();
                SearchBox.Clear();
                await RefreshDevicesAsync(selectMEmuIndex: device.MEmuIndex);
                SetStatus($"Starting {device.Name}…");
                await memu.StartDeviceAsync(device);
                await RefreshDevicesAsync(selectMEmuIndex: device.MEmuIndex);
            }, "MEmu created the new device. Start requested.");
            return;
        }

        try
        {
            var images = tools.ListSystemImages().Where(image => image.Package.EndsWith(";x86_64", StringComparison.Ordinal)).ToArray();
            if (images.Length == 0)
            {
                SetupNav.IsChecked = true;
                SetStatus("Choose Set up Android to download an Android system.");
                return;
            }

            int next = 1;
            var names = devices.Select(device => device.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            while (names.Contains($"Android_{next}"))
                next++;
            var startImmediately = accelerationReady;
            var dialog = new CreateDeviceWindow(images, $"Android_{next}", startImmediately) { Owner = this };
            if (dialog.ShowDialog() != true)
                return;

            var name = dialog.DeviceName;
            var image = dialog.SelectedImage;
            await PerformAsync($"Creating {name}…", async () =>
            {
                await tools.CreateDeviceAsync(name, image);
                SearchBox.Clear();
                await RefreshDevicesAsync(name);
                var device = devices.FirstOrDefault(device => device.Name == name)
                    ?? throw new InvalidOperationException("The Android tools did not confirm that the device was created.");
                if (startImmediately)
                {
                    SetStatus($"Starting {name}…");
                    await tools.StartDeviceAsync(device);
                    await RefreshDevicesAsync(name);
                }
            }, startImmediately ? $"Start requested for {name}." : $"Created {name}. Open Setup to check virtualization.");
        }
        catch (Exception ex) when (IsOperationError(ex))
        {
            SetStatus(ex.Message, error: true);
        }
    }

    private async void StartClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is { } device)
            await PerformAsync($"Starting {device.Name}…", async () =>
            {
                await (useMEmu ? memu!.StartDeviceAsync(device) : tools.StartDeviceAsync(device));
                await RefreshDevicesAsync();
            }, $"Start requested for {device.Name}.");
    }

    private async void StopClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is { } device)
            await PerformAsync($"Stopping {device.Name}…", async () =>
            {
                await (useMEmu ? memu!.StopDeviceAsync(device) : tools.StopDeviceAsync(device));
                await RefreshDevicesAsync();
            }, $"Stop requested for {device.Name}.");
    }

    private async void InstallClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is not { } device)
            return;
        var dialog = new OpenFileDialog { Title = "Choose a test APK", Filter = "Android APK (*.apk)|*.apk" };
        if (dialog.ShowDialog(this) == true)
        {
            await PerformAsync($"Installing {Path.GetFileName(dialog.FileName)}…",
                () => useMEmu ? memu!.InstallAsync(device, dialog.FileName) : tools.InstallAsync(device, dialog.FileName), $"APK installed on {device.Name}.");
        }
    }

    private void CoordinatesChanged(object sender, TextChangedEventArgs e)
    {
        if (ApplyLocationButton is not null)
            UpdateButtons();
    }

    private bool TryReadLocation(out LocationProfile location)
    {
        location = new(0, 0);
        if (!TryCoordinate(Latitude.Text, 90, out var latitude)
            || !TryCoordinate(Longitude.Text, 180, out var longitude))
            return false;

        location = new(latitude, longitude);
        return true;
    }

    private static bool TryCoordinate(string text, double limit, out double coordinate)
    {
        var valid = double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out coordinate)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out coordinate);
        return valid && double.IsFinite(coordinate) && Math.Abs(coordinate) <= limit;
    }

    private void SaveLocationClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is not { } device || !TryReadLocation(out var location))
            return;

        var hadPrevious = preferences.Locations.TryGetValue(device.Name, out var previous);
        preferences.Locations[device.Name] = location;
        try
        {
            preferences.Save();
            SetStatus($"Saved the test location for {device.Name}.");
        }
        catch (Exception ex) when (IsOperationError(ex))
        {
            if (hadPrevious)
                preferences.Locations[device.Name] = previous!;
            else
                preferences.Locations.Remove(device.Name);
            SetStatus(ex.Message, error: true);
        }
    }

    private async void ApplyLocationClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is { } device && TryReadLocation(out var location))
            await PerformAsync("Applying the test location…", () => tools.ApplyLocationAsync(device, location),
                $"Test location applied to {device.Name}.");
    }

    private async void LogsClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is not { } device)
            return;
        await PerformAsync("Reading device logs…", async () =>
        {
            var output = await (useMEmu ? memu!.ReadLogsAsync(device) : tools.ReadLogsAsync(device));
            Logs.Text = string.IsNullOrWhiteSpace(output) ? "No log entries returned." : output;
            Logs.Visibility = Visibility.Visible;
            Logs.ScrollToEnd();
        }, $"Log snapshot loaded for {device.Name}.");
    }

    private async Task PerformAsync(string progress, Func<Task> operation, string? success = null)
    {
        if (busy)
            return;

        busy = true;
        SetStatus(progress);
        UpdateButtons();
        try
        {
            await currentPoll;
            if (closed)
                return;
            await operation();
            if (!closed && success is not null)
                SetStatus(success);
            else if (!closed && StatusMessage.Text == progress)
                SetStatus("Ready.");
        }
        catch (Exception ex) when (IsOperationError(ex))
        {
            if (!closed)
                SetStatus(ex.Message, error: true);
        }
        finally
        {
            busy = false;
            if (!closed)
                UpdateButtons();
        }
    }

    private void SetStatus(string message, bool error = false)
    {
        StatusMessage.Text = message;
        StatusMessage.ToolTip = message;
        StatusMessage.Foreground = (Brush)FindResource(error ? "Error" : "Muted");
    }

    private static bool IsOperationError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException
            or TimeoutException or Win32Exception or NotSupportedException;
}

using System.ComponentModel;
using System.Globalization;
using System.IO;
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
    private IReadOnlyList<AndroidDevice> devices = [];
    private Task currentPoll = Task.CompletedTask;
    private bool busy;
    private bool closed;
    private bool updatingSelection;
    private string? displayedDevice;
    private AndroidDevice? SelectedDevice => DevicesList.SelectedItem as AndroidDevice;

    public MainWindow()
    {
        preferences = Preferences.Load(out var warning);
        var savedSdk = preferences.SdkDirectory;
        tools = new(savedSdk is not null && Path.IsPathFullyQualified(savedSdk)
            ? savedSdk : AndroidTools.FindSdk());
        pollTimer = new() { Interval = TimeSpan.FromSeconds(5) };
        pollTimer.Tick += PollTick;
        InitializeComponent();
        SdkPath.Text = tools.SdkDirectory;
        UpdateDeviceList();
        SetStatus(warning ?? "Ready.");
    }

    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        await PerformAsync("Reading virtual devices…", () => RefreshDevicesAsync());
        if (!closed)
            pollTimer.Start();
    }

    private void WindowClosed(object? sender, EventArgs e)
    {
        closed = true;
        pollTimer.Stop();
    }

    private void NavigationChanged(object sender, RoutedEventArgs e)
    {
        if (DevicePage is null || SetupPage is null)
            return;

        var showSetup = SetupNav.IsChecked == true;
        DevicePage.Visibility = showSetup ? Visibility.Collapsed : Visibility.Visible;
        SetupPage.Visibility = showSetup ? Visibility.Visible : Visibility.Collapsed;
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
        if (!busy && currentPoll.IsCompleted && DevicesNav.IsChecked == true && tools.HasEmulator)
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

    private async Task RefreshDevicesAsync(string? selectName = null)
    {
        var fresh = tools.HasEmulator ? await tools.ListDevicesAsync() : [];
        if (closed)
            return;
        if (selectName is not null || !fresh.SequenceEqual(devices))
        {
            devices = fresh;
            UpdateDeviceList(selectName);
        }
        if (!tools.HasEmulator)
            SetStatus("Open SDK setup to connect an Android Emulator installation.");
    }

    private void UpdateDeviceList(string? selectName = null)
    {
        var previousName = selectName ?? SelectedDevice?.Name;
        var filter = SearchBox.Text.Trim();
        var visible = devices.Where(device => device.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || device.Status.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();

        updatingSelection = true;
        DevicesList.ItemsSource = visible;
        DevicesList.SelectedItem = visible.FirstOrDefault(device => device.Name == previousName)
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
        OpenSetupButton.Visibility = tools.HasEmulator ? Visibility.Collapsed : Visibility.Visible;
        EmptyTitle.Text = !tools.HasEmulator ? "Connect the Android SDK"
            : devices.Count > 0 ? "Select a virtual device" : "Create your first virtual device";
        EmptyHint.Text = !tools.HasEmulator ? "Set the SDK folder and check the emulator tools."
            : devices.Count > 0 ? "Clear the filter to see all registered devices."
            : "Choose New device and select an installed Android system image.";

        if (selected is not null)
        {
            SelectedName.Text = selected.Name;
            SelectedStatus.Text = selected.Serial is null ? selected.Status : $"{selected.Status}  ·  {selected.Serial}";
        }

        if (displayedDevice != selected?.Name)
        {
            displayedDevice = selected?.Name;
            var location = displayedDevice is not null && preferences.Locations.TryGetValue(displayedDevice, out var saved)
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
        var canControl = !busy && tools.HasAdb;
        var canSetLocation = device is not null && TryReadLocation(out _);
        RefreshButton.IsEnabled = !busy && tools.HasEmulator;
        NewButton.IsEnabled = !busy && tools.HasEmulator;
        DeviceEditor.IsEnabled = !busy;
        BrowseButton.IsEnabled = !busy;
        SdkPath.IsEnabled = !busy;
        DiagnosticsButton.IsEnabled = !busy;
        StartButton.IsEnabled = canControl && device?.Status == "Stopped";
        StopButton.IsEnabled = canControl && device?.Serial is not null;
        InstallButton.IsEnabled = canControl && online;
        LogsButton.IsEnabled = canControl && online;
        SaveLocationButton.IsEnabled = !busy && canSetLocation;
        ApplyLocationButton.IsEnabled = canControl && online && canSetLocation;
        WorkProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
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

            if (tools.HasEmulator)
                await RefreshDevicesAsync();
        }, "Diagnostics complete.");
    }

    private async void NewDeviceClicked(object sender, RoutedEventArgs e)
    {
        if (busy)
            return;
        try
        {
            var images = tools.ListSystemImages();
            if (images.Count == 0)
                throw new InvalidOperationException("Install an Android system image in Android Studio’s SDK Manager first.");
            var dialog = new CreateDeviceWindow(images) { Owner = this };
            if (dialog.ShowDialog() != true)
                return;

            var name = dialog.DeviceName;
            var image = dialog.SelectedImage;
            await PerformAsync($"Creating {name}…", async () =>
            {
                await tools.CreateDeviceAsync(name, image);
                SearchBox.Clear();
                await RefreshDevicesAsync(name);
                if (!devices.Any(device => device.Name == name))
                    throw new InvalidOperationException("The Android tools did not confirm that the device was created.");
            }, $"Created {name}.");
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
                await tools.StartDeviceAsync(device);
                await RefreshDevicesAsync();
            }, $"Start requested for {device.Name}.");
    }

    private async void StopClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is { } device)
            await PerformAsync($"Stopping {device.Name}…", async () =>
            {
                await tools.StopDeviceAsync(device);
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
                () => tools.InstallAsync(device, dialog.FileName), $"APK installed on {device.Name}.");
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
            var output = await tools.ReadLogsAsync(device);
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

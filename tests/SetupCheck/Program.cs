using DeviceLab;

if (!OperatingSystem.IsWindows() || args.Length != 1)
    throw new InvalidOperationException("Run on Windows and pass a new directory for the test SDK.");

var workingDirectory = Path.GetFullPath(args[0]);
if (Directory.Exists(workingDirectory))
    throw new InvalidOperationException("The test directory must be new.");

var sdkDirectory = Path.Combine(workingDirectory, "Android SDK");
var androidHome = Path.Combine(workingDirectory, "Android user");
var avds = Path.Combine(androidHome, "avd");
var runtimeDirectory = Path.Combine(workingDirectory, "Device Lab");
Directory.CreateDirectory(avds);
Environment.SetEnvironmentVariable("ANDROID_USER_HOME", androidHome);
Environment.SetEnvironmentVariable("ANDROID_AVD_HOME", avds);
Environment.SetEnvironmentVariable("ANDROID_EMULATOR_HOME", androidHome);

var tools = new AndroidTools(sdkDirectory, runtimeDirectory);
var setup = new AndroidSetup(tools);
var plan = await setup.PlanAsync();
Assert(plan.NeedsCommandLineTools && plan.Packages.Contains(AndroidSetup.RecommendedSystem),
    "The clean test directory must require Android tools and a system image.");

var progress = new ConsoleProgress();
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(40));

// Exercise the upgrade path that needs only Java, even on runners which already have a system JDK.
await setup.InstallAsync(new(null, false, []), null, progress, deadline.Token);
Assert(await tools.FindJavaHomeAsync(deadline.Token) == tools.ManagedJavaHome,
    "Device Lab must use its downloaded Java runtime.");

plan = await setup.PlanAsync(deadline.Token);
var license = await setup.ReadLicenseAsync(deadline.Token);
// This unattended check installs the packages only in the explicitly supplied disposable SDK.
await setup.InstallAsync(plan, license, progress, deadline.Token);
plan = await setup.PlanAsync(deadline.Token);
Assert(plan.IsReady, "Setup must leave a complete Android SDK.");

var image = tools.ListSystemImages().Single(image => image.Package == AndroidSetup.RecommendedSystem);
await tools.CreateDeviceAsync("Setup_Check", image);
Assert((await tools.ListDevicesAsync()).Any(device => device.Name == "Setup_Check"),
    "The downloaded tools must create and discover an AVD.");
await setup.InstallAsync(plan, null, progress, deadline.Token);
Console.WriteLine("Verified Java-only repair, clean Android setup, AVD creation, and repeat setup.");

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

internal sealed class ConsoleProgress : IProgress<SetupProgress>
{
    private string? previous;

    public void Report(SetupProgress value)
    {
        if (value.Message != previous || value.Percent is 25 or 50 or 75 or 100)
            Console.WriteLine($"{value.Message} {value.Percent?.ToString()}");
        previous = value.Message;
    }
}

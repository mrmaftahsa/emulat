using DeviceLab;

const string StateVariable = "DEVICELAB_MEMU_FIXTURE";
if (Environment.GetEnvironmentVariable(StateVariable) is { } fixture)
{
    RunMockCli(fixture, args);
    return;
}

var directory = Directory.CreateTempSubdirectory("DeviceLab MEmu check ");
try
{
    var state = Path.Combine(directory.FullName, "state.txt");
    var apk = Path.Combine(directory.FullName, "test app.apk");
    File.WriteAllText(apk, "APK fixture");
    Environment.SetEnvironmentVariable(StateVariable, state);
    var executable = Path.Combine(AppContext.BaseDirectory, "memuc.exe");
    if (!OperatingSystem.IsWindows())
        File.Copy(Path.Combine(AppContext.BaseDirectory, "memuc"), executable, overwrite: true);

    Assert(MEmuTools.FindExecutable(executable) == executable, "A saved MEmu path must be reused.");
    var tools = new MEmuTools(executable);
    var devices = await tools.ListDevicesAsync();
    Assert(devices.Count == 2 && devices[0].Name == "Demo, Android"
        && devices[0].MEmuIndex == 0 && devices[0].IsOnline && !devices[1].IsOnline, "MEmu inventory mismatch.");

    var stopped = devices[1];
    await tools.StartDeviceAsync(stopped);
    var running = (await tools.ListDevicesAsync()).Single(device => device.MEmuIndex == stopped.MEmuIndex);
    Assert(running.IsOnline, "Start must target the selected MEmu index.");
    await tools.InstallAsync(running, apk);
    Assert((await tools.ReadLogsAsync(running)).Contains("test log entry"), "Logs must use the selected instance’s ADB.");
    await tools.StopDeviceAsync(running);
    Assert(!(await tools.ListDevicesAsync()).Single(device => device.MEmuIndex == 1).IsOnline,
        "Stop must target the selected MEmu index.");

    var created = await tools.CreateDeviceAsync();
    Assert(created.MEmuIndex == 2 && created.Name == "New MEmu", "Create must identify the new instance.");

    try
    {
        await tools.StartDeviceAsync(stopped with { Name = "Wrong instance" });
        throw new Exception("A stale VM selection must not be used.");
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("selected MEmu device changed"))
    {
        Console.WriteLine("Verified MEmu discovery, inventory, start, stop, creation, APK installation, logcat and stale selection handling with a simulated MEmu CLI.");
    }
}
finally
{
    Environment.SetEnvironmentVariable(StateVariable, null);
    directory.Delete(recursive: true);
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void RunMockCli(string statePath, string[] arguments)
{
    var state = File.Exists(statePath) ? File.ReadAllText(statePath) : "";
    var secondRunning = state.Contains('+');
    var thirdCreated = state.Contains('C');
    switch (arguments)
    {
        case ["listvms"] or ["listvms", "--running"]:
            var runningOnly = arguments.Length == 2;
            Console.WriteLine("0,Demo, Android,386754,86338,1,2404");
            if (!runningOnly || secondRunning)
                Console.WriteLine(secondRunning ? "1,Test VM,43262,53432,1,1205" : "1,Test VM,0,0,0,-1");
            if (!runningOnly && thirdCreated)
                Console.WriteLine("2,New MEmu,0,0,0,-1");
            break;
        case ["start", "-i", "1"]:
            File.WriteAllText(statePath, state + "+");
            break;
        case ["stop", "-i", "1"]:
            File.WriteAllText(statePath, state.Replace("+", ""));
            break;
        case ["create"]:
            File.WriteAllText(statePath, state + "C");
            break;
        case ["installapp", "-i", "1", var path] when Path.GetFileName(path) == "test app.apk" && File.Exists(path):
            Console.WriteLine("Success");
            break;
        case ["-i", "1", "adb", "logcat -d -t 250 -v threadtime"]:
            Console.WriteLine("test log entry");
            break;
        default:
            Console.Error.WriteLine($"Unexpected MEmu arguments: {string.Join('|', arguments)}");
            Environment.ExitCode = 1;
            break;
    }
}

using Android.App;
using Android.Content;

namespace DiTunnel.Platform.Android;

// Keep UI, Xray and AWG runtimes in their own processes on Android 8.1 too.
public static class AndroidProcessIdentity
{
    public static string CurrentName
    {
        get
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(28))
                return Application.ProcessName
                    ?? throw new InvalidOperationException("Android не предоставил имя текущего процесса.");

            try
            {
                var name = File.ReadAllText("/proc/self/cmdline").Split('\0')[0];
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            var manager = Application.Context.GetSystemService(Context.ActivityService) as ActivityManager;
            var pid = global::Android.OS.Process.MyPid();
            var processName = manager?.RunningAppProcesses?.FirstOrDefault(process => process.Pid == pid)?.ProcessName;
            // Never assume the main process: loading both Go runtimes together is unsafe.
            return !string.IsNullOrWhiteSpace(processName) ? processName
                : throw new InvalidOperationException("Android не предоставил имя текущего процесса.");
        }
    }
}

using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using DiTunnel.Core.Connection;
using Microsoft.Win32;
using Windows.Management.Deployment;

namespace DiTunnel.Platform.Windows;

/// <summary>Executables from shortcuts, App Paths, current-user packages and running applications.</summary>
public sealed class WindowsInstalledApplicationProvider : IInstalledApplicationProvider
{
    public Task<IReadOnlyList<InstalledApplication>> GetInstalledApplicationsAsync(CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<IReadOnlyList<InstalledApplication>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.TrySetResult(Enumerate(cancellationToken)); }
            catch (OperationCanceledException) { completion.TrySetCanceled(cancellationToken); }
            catch (Exception error) { completion.TrySetException(error); }
        }) { IsBackground = true, Name = "DiTunnel applications" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static IReadOnlyList<InstalledApplication> Enumerate(CancellationToken token)
    {
        var applications = new Dictionary<string, InstalledApplication>(StringComparer.OrdinalIgnoreCase);
        void Add(string? path, string? name = null)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim('"')));
                if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)
                    || Path.GetFileName(path).StartsWith("unins", StringComparison.OrdinalIgnoreCase)) return;
                name ??= FileVersionInfo.GetVersionInfo(path).FileDescription;
                applications.TryAdd(path, new(path, string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(path) : name));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception) { }
        }
        foreach (var folder in new[] { Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.CommonStartMenu })
        {
            var root = Environment.GetFolderPath(folder);
            if (!Directory.Exists(root)) continue;
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var file in Directory.EnumerateFiles(root, "*.lnk", options))
            {
                token.ThrowIfCancellationRequested();
                object? shortcut = null;
                try
                {
                    shortcut = new ShellLink();
                    ((IPersistFile)shortcut).Load(file, 0);
                    var path = new StringBuilder(32768);
                    ((IShellLink)shortcut).GetPath(path, path.Capacity, 0, 4);
                    Add(path.ToString(), Path.GetFileNameWithoutExtension(file));
                }
                catch (Exception error) when (error is COMException or IOException or UnauthorizedAccessException) { }
                finally { if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut); }
            }
        }
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var registry = RegistryKey.OpenBaseKey(hive, view);
            using var appPaths = registry.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
            foreach (var key in appPaths?.GetSubKeyNames() ?? [])
            {
                token.ThrowIfCancellationRequested();
                try { using var app = appPaths!.OpenSubKey(key); Add(app?.GetValue(null) as string); }
                catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException) { }
            }
        }
        try
        {
            // Current-user enumeration needs no elevation and includes Store/MSIX apps
            // whose Start menu entries are shell identities rather than .lnk files.
            foreach (var package in new PackageManager().FindPackagesForUser(string.Empty))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (package.IsFramework || package.IsResourcePackage) continue;
                    foreach (var application in ReadPackageManifest(package.InstalledLocation.Path, package.DisplayName))
                        Add(application.Id, application.Name);
                }
                catch (Exception error) when (error is COMException or IOException or UnauthorizedAccessException or XmlException or ArgumentException) { }
            }
        }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException) { }

        // CLI helpers need not have a Start menu entry or an App Paths registration.
        // Inspect only image paths in this session; do not read command lines or launch anything.
        using var current = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                token.ThrowIfCancellationRequested();
                try { if (process.SessionId == current.SessionId) Add(process.MainModule?.FileName); }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }
        var codexRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        try
        {
            if (Directory.Exists(codexRoot))
                foreach (var version in Directory.EnumerateDirectories(codexRoot, "*", new EnumerationOptions { IgnoreInaccessible = true }))
                {
                    token.ThrowIfCancellationRequested();
                    Add(Path.Combine(version, "codex.exe"), "Codex CLI");
                }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return applications.Values.OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    internal static IReadOnlyList<InstalledApplication> ReadPackageManifest(string root, string packageName)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var reader = XmlReader.Create(Path.Combine(fullRoot, "AppxManifest.xml"), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024
        });
        var manifest = XDocument.Load(reader);
        var applications = manifest.Root?.Elements().FirstOrDefault(element => element.Name.LocalName == "Applications");
        var result = new List<InstalledApplication>();
        foreach (var entry in applications?.Elements().Where(element => element.Name.LocalName == "Application") ?? [])
        {
            var executable = (string?)entry.Attribute("Executable");
            if (string.IsNullOrWhiteSpace(executable) || Path.IsPathRooted(executable)) continue;
            var path = Path.GetFullPath(Path.Combine(fullRoot, executable));
            if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || !File.Exists(path)) continue;
            var displayName = (string?)entry.Elements().FirstOrDefault(element => element.Name.LocalName == "VisualElements")?.Attribute("DisplayName");
            if (string.IsNullOrWhiteSpace(displayName) || displayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) displayName = packageName;
            result.Add(new(path, string.IsNullOrWhiteSpace(displayName) ? Path.GetFileNameWithoutExtension(path) : displayName));
        }
        return result;
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLink;

    // Only the first vtable method is used; no shortcut execution or resolution.
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLink
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int characters, nint findData, uint flags);
    }
}

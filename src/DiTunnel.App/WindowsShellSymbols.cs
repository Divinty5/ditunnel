using System.ComponentModel;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

namespace DiTunnel.App;

/// <summary>Resolves exact Microsoft symbols outside Explorer and outside the UI thread.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsShellSymbols
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Symbols
    {
        public uint Version, ModuleKind, Timestamp, ImageSize;
        public uint AppId, ItemCount, Shortcut, Resource, Draw;
    }

    private static readonly SemaphoreSlim ResolverLock = new(1);
    private static readonly string[] Names =
    [
        "?GetAppID@CTaskGroup@@UEAAPEBGXZ",
        "?GetNumItems@CTaskGroup@@UEAAHXZ",
        "?GetShortcutIDList@CTaskGroup@@UEAAPEBU_ITEMIDLIST_ABSOLUTE@@XZ",
        "?GetIconResource@CTaskGroup@@UEAAPEBGXZ"
    ];

    internal static async Task<(uint ProcessId, Symbols Value)> ResolveAsync(CancellationToken cancellation)
    {
        var window = FindWindow("Shell_TrayWnd", null);
        GetWindowThreadProcessId(window, out var pid);
        if (pid == 0) throw new InvalidOperationException("The taskbar is not available.");
        using var process = Process.GetProcessById(checked((int)pid));
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        if (!string.Equals(process.MainModule?.FileName, explorer, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The taskbar is not Windows Explorer.");
        var modules = process.Modules.Cast<ProcessModule>().ToArray();
        // ExplorerPatcher exports the functions; no download or PDB is needed.
        if (modules.Any(module => module.ModuleName.StartsWith("ep_taskbar.", StringComparison.OrdinalIgnoreCase)))
            return (pid, new Symbols { Version = 1 });
        var modern = modules.FirstOrDefault(module => string.Equals(module.ModuleName, "taskbar.dll", StringComparison.OrdinalIgnoreCase));
        var image = modern?.FileName ?? explorer;
        var expected = modern is null ? explorer : Path.Combine(Environment.SystemDirectory, "taskbar.dll");
        if (!string.Equals(image, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected taskbar module path.");
        await ResolverLock.WaitAsync(cancellation).ConfigureAwait(false);
        try { return (pid, await ResolveImageAsync(image, modern is null ? 2u : 1u, cancellation).ConfigureAwait(false)); }
        finally { ResolverLock.Release(); }
    }

    internal static async Task<Symbols> ResolveImageAsync(string image, uint kind, CancellationToken cancellation)
    {
        using var stream = File.OpenRead(image);
        using var reader = new PEReader(stream);
        var headers = reader.PEHeaders;
        if (headers.CoffHeader.Machine != Machine.Amd64) throw new NotSupportedException("The shell is not x64.");
        var codeView = reader.ReadCodeViewDebugDirectoryData(reader.ReadDebugDirectory().Single(entry => entry.Type == DebugDirectoryEntryType.CodeView));
        var name = Path.GetFileName(codeView.Path);
        if (!string.Equals(name, "taskbar.pdb", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(name, "explorer.pdb", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unexpected shell PDB name.");
        var key = codeView.Guid.ToString("N") + codeView.Age.ToString("X");
        var directory = Path.Combine(UserSettings.DataDirectory, "ShellSymbols", name, key);
        Directory.CreateDirectory(directory);
        await using var hashStream = File.OpenRead(image);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, cancellation).ConfigureAwait(false));
        var metadata = Path.Combine(directory, "rvas.json");
        if (File.Exists(metadata))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<Cache>(await File.ReadAllTextAsync(metadata, cancellation).ConfigureAwait(false), CacheOptions);
                if (cached?.ImageHash == hash && Valid(headers, cached.Value, kind)) return cached.Value;
            }
            catch (JsonException) { /* Re-resolve incomplete caches. */ }
        }
        var pdb = Path.Combine(directory, name);
        if (!File.Exists(pdb))
        {
            // Only the public Windows PDB identity is sent. No application/user data.
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
            using var response = await http.GetAsync($"https://msdl.microsoft.com/download/symbols/{name}/{key}/{name}",
                HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            const long maximum = 128 * 1024 * 1024;
            if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("Shell PDB exceeds the size limit.");
            var temporary = pdb + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var output = File.Create(temporary))
                await using (var input = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false))
                {
                    var buffer = new byte[65536]; long total = 0; int count;
                    while ((count = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false)) != 0)
                    {
                        total += count;
                        if (total > maximum) throw new InvalidDataException("Shell PDB exceeds the size limit.");
                        await output.WriteAsync(buffer.AsMemory(0, count), cancellation).ConfigureAwait(false);
                    }
                }
                File.Move(temporary, pdb, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        cancellation.ThrowIfCancellationRequested();
        var value = ResolvePdb(image, directory, headers, kind);
        if (!Valid(headers, value, kind)) throw new InvalidDataException("Shell symbol addresses do not match the image.");
        var json = JsonSerializer.Serialize(new Cache(hash, value), CacheOptions);
        var cacheTemporary = metadata + ".tmp";
        await File.WriteAllTextAsync(cacheTemporary, json, cancellation).ConfigureAwait(false);
        File.Move(cacheTemporary, metadata, overwrite: true);
        return value;
    }

    internal static bool Valid(PEHeaders headers, Symbols value, uint kind)
    {
        if (value.Version != 1 || value.ModuleKind != kind ||
            value.Timestamp != unchecked((uint)headers.CoffHeader.TimeDateStamp) || value.ImageSize != headers.PEHeader?.SizeOfImage) return false;
        return new[] { value.AppId, value.ItemCount, value.Shortcut, value.Resource, value.Draw }.All(rva =>
            rva > 0 && headers.SectionHeaders.Any(section => (section.SectionCharacteristics & SectionCharacteristics.MemExecute) != 0 &&
                rva >= section.VirtualAddress && rva - section.VirtualAddress < section.VirtualSize));
    }

    private static Symbols ResolvePdb(string image, string directory, PEHeaders headers, uint kind)
    {
        // DbgHelp is single-threaded. Use a private session, never Explorer's session.
        using var identity = new EventWaitHandle(false, EventResetMode.AutoReset);
        var session = identity.SafeWaitHandle.DangerousGetHandle();
        var previous = SymGetOptions();
        SymSetOptions(0x400); // SYMOPT_EXACT_SYMBOLS: reject a PDB for another build.
        try
        {
            if (!SymInitialize(session, directory, false)) throw new Win32Exception();
            try
            {
                var loaded = SymLoadModuleEx(session, 0, image, "DiTunnelShell", 0x10000000, 0, 0, 0);
                if (loaded == 0) throw new Win32Exception();
                var names = Names.Append(kind == 1
                    ? "?GetIcon@CTaskBtnGroup@@UEAAJPEAUITaskItem@@PEAPEAUHICON__@@@Z"
                    : "?_DrawRegularButton@CTaskBtnGroup@@AEAAXPEAUHDC__@@AEBUBUTTONRENDERINFO@@@Z");
                var rvas = new List<uint>();
                var info = Marshal.AllocHGlobal(88 + 2048);
                try
                {
                    foreach (var name in names)
                    {
                        Marshal.Copy(new byte[88 + 2048], 0, info, 88 + 2048);
                        Marshal.WriteInt32(info, 88); Marshal.WriteInt32(info, 80, 1024);
                        if (!SymFromName(session, "DiTunnelShell!" + name, info)) throw new Win32Exception();
                        rvas.Add(checked((uint)((ulong)Marshal.ReadInt64(info, 56) - loaded)));
                    }
                }
                finally { Marshal.FreeHGlobal(info); }
                return new Symbols { Version = 1, ModuleKind = kind, Timestamp = unchecked((uint)headers.CoffHeader.TimeDateStamp),
                    ImageSize = checked((uint)headers.PEHeader!.SizeOfImage), AppId = rvas[0], ItemCount = rvas[1],
                    Shortcut = rvas[2], Resource = rvas[3], Draw = rvas[4] };
            }
            finally { SymCleanup(session); }
        }
        finally { SymSetOptions(previous); }
    }

    // IncludeFields is intentional for the fixed-width interop structure.
    private sealed record Cache(string ImageHash, Symbols Value);
    private static readonly JsonSerializerOptions CacheOptions = new() { IncludeFields = true };
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string name, string? title);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("dbghelp.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint SymSetOptions(uint options);
    [DllImport("dbghelp.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint SymGetOptions();
    [DllImport("dbghelp.dll", EntryPoint = "SymInitializeW", CharSet = CharSet.Unicode, SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SymInitialize(nint session, string path, [MarshalAs(UnmanagedType.Bool)] bool invade);
    [DllImport("dbghelp.dll", EntryPoint = "SymLoadModuleExW", CharSet = CharSet.Unicode, SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern ulong SymLoadModuleEx(nint session, nint file, string path, string name, ulong address, uint size, nint data, uint flags);
    [DllImport("dbghelp.dll", EntryPoint = "SymFromNameW", CharSet = CharSet.Unicode, SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SymFromName(nint session, string name, nint symbol);
    [DllImport("dbghelp.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SymCleanup(nint session);
}

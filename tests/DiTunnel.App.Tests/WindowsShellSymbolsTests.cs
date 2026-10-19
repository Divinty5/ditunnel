using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using DiTunnel.App;

namespace DiTunnel.App.Tests;

public class WindowsShellSymbolsTests
{
    private sealed class StalledBody : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
    }
    private sealed class Responses : HttpMessageHandler
    {
        private int count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = Interlocked.Increment(ref count) == 1 ? new StreamContent(new StalledBody()) : new ByteArrayContent([1, 2, 3]) });
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Stalled_body_times_out_cleans_partial_file_and_allows_retry()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "DiTunnel-symbol-download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var http = new HttpClient(new Responses());
            var pdb = Path.Combine(directory, "synthetic.pdb");
            var uri = new Uri("https://example.test/synthetic.pdb");
            Task Download(TimeSpan timeout) => WindowsShellSymbols.DownloadPdbAsync(http, uri, pdb, default, timeout);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Download(TimeSpan.FromMilliseconds(100)));
            Assert.Empty(Directory.GetFiles(directory));
            await WindowsShellSymbols.DownloadPdbAsync(http, uri, pdb, default, TimeSpan.FromSeconds(5));
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(pdb));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void PayloadHasTheNativeFixedWidthLayout()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(36, Marshal.SizeOf<WindowsShellSymbols.Symbols>());
        Assert.Equal(16, Marshal.OffsetOf<WindowsShellSymbols.Symbols>("AppId").ToInt32());
        Assert.Equal(32, Marshal.OffsetOf<WindowsShellSymbols.Symbols>("Draw").ToInt32());
    }

    [Fact]
    public void RejectsForeignBuildsAndNonExecutableAddresses()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var file = File.OpenRead(typeof(WindowsShellSymbolsTests).Assembly.Location);
        using var pe = new PEReader(file);
        var headers = pe.PEHeaders;
        var code = headers.SectionHeaders.First(section => (section.SectionCharacteristics & SectionCharacteristics.MemExecute) != 0);
        var rva = checked((uint)code.VirtualAddress + 1);
        var symbols = new WindowsShellSymbols.Symbols { Version = 1, ModuleKind = 1,
            Timestamp = unchecked((uint)headers.CoffHeader.TimeDateStamp), ImageSize = checked((uint)headers.PEHeader!.SizeOfImage),
            AppId = rva, ItemCount = rva, Shortcut = rva, Resource = rva, Draw = rva };
        Assert.True(WindowsShellSymbols.Valid(headers, symbols, 1));
        Assert.False(WindowsShellSymbols.Valid(headers, symbols, 2));
        symbols.Timestamp++; Assert.False(WindowsShellSymbols.Valid(headers, symbols, 1)); symbols.Timestamp--;
        symbols.ImageSize++; Assert.False(WindowsShellSymbols.Valid(headers, symbols, 1)); symbols.ImageSize--;
        symbols.Version++; Assert.False(WindowsShellSymbols.Valid(headers, symbols, 1)); symbols.Version--;
        foreach (var invalid in new[] { 0u, (uint)headers.PEHeader.SizeOfHeaders - 1, (uint)headers.PEHeader.SizeOfImage, uint.MaxValue })
        {
            symbols.Draw = invalid;
            Assert.False(WindowsShellSymbols.Valid(headers, symbols, 1));
        }
    }
}

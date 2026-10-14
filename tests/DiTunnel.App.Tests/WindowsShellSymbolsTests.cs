using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using DiTunnel.App;

namespace DiTunnel.App.Tests;

public class WindowsShellSymbolsTests
{
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

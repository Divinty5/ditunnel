using DiTunnel.App.ViewModels;
using DiTunnel.Core.Profiles;
using SkiaSharp;
using ZXing;
using ZXing.Common;

namespace DiTunnel.App.Tests;

public sealed class ProfileSharingTests
{
    private const string Key = "AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE=";
    private static ImportedProfile Profile(string extra = "") => new("Сервер друга", "AmneziaWG", $"""
        [Interface]
        PrivateKey = {Key}
        Address = 192.0.2.2/32
        DNS = 192.0.2.53
        Jc = 4
        Jmin = 10
        Jmax = 40
        S1 = 16
        S2 = 32
        H1 = 12345
        H2 = 23456
        H3 = 34567
        H4 = 45678
        {extra}
        [Peer]
        PublicKey = {Key}
        PresharedKey = {Key}
        Endpoint = vpn.example.com:51820
        AllowedIPs = 0.0.0.0/0
        PersistentKeepalive = 25
        """);

    private sealed class Store(ImportedProfile profile) : IProfileStore
    {
        public IReadOnlyList<ImportedProfile> Load() => [profile, new("HY2", "Hysteria 2", "hy2://test@192.0.2.1:443")];
        public void Save(IEnumerable<ImportedProfile> profiles) { }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AwgSharingIsEnabledOnWindowsAndAndroidAndFollowsSelection(bool android)
    {
        var profile = Profile() with { ProtocolVersion = "2.0" };
        var vm = new MainViewModel(null, new Store(profile), platform: android ? AppPlatform.Android : AppPlatform.Windows);
        try
        {
            vm.SelectedProfile = vm.Profiles.Single(row => row.Profile.Kind == "AmneziaWG");
            Assert.True(vm.CanShareServer);
            var imported = Assert.Single(ProfileParser.Parse(vm.SelectedServerUri!));
            Assert.Equal(profile.Content, imported.Content);
            Assert.Equal(profile.Name, imported.Name);
            Assert.Equal(profile.ProtocolVersion, imported.ProtocolVersion);
            vm.SelectedProfile = vm.Profiles.Single(row => row.Profile.Kind == "Hysteria 2");
            Assert.Equal("hy2://test@192.0.2.1:443", vm.SelectedServerUri);
            vm.SelectedProfile = null;
            Assert.False(vm.CanShareServer);
        }
        finally { await vm.ShutdownAsync(); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("S3 = 12\nS4 = 12\nI1 = <b 0x01020304><r 10>")]
    [InlineData("RandomTrailers = true\nDisableCookies = false\nContentPaddingAddition = 10-20")]
    public void RenderedAwgQrCodeCanBeDecodedAndImported(string extra)
    {
        var profile = Profile(extra);
        var link = ProfileShareFormatter.CreateLink(profile)!;
        using var bitmap = SKBitmap.Decode(QrCodeImage.CreatePng(link));
        var rgb = new byte[bitmap.Width * bitmap.Height * 3];
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
            {
                var color = bitmap.GetPixel(x, y);
                var index = (y * bitmap.Width + x) * 3;
                rgb[index] = color.Red; rgb[index + 1] = color.Green; rgb[index + 2] = color.Blue;
            }
        var reader = new BarcodeReaderGeneric { Options = new DecodingOptions { PossibleFormats = [BarcodeFormat.QR_CODE] } };
        var decoded = reader.Decode(new RGBLuminanceSource(rgb, bitmap.Width, bitmap.Height, RGBLuminanceSource.BitmapFormat.RGB24));
        Assert.NotNull(decoded);
        Assert.Equal(link, decoded.Text);
        var imported = Assert.Single(ProfileParser.Parse(decoded.Text));
        Assert.Equal(profile.Content, imported.Content);
        Assert.Equal(profile.Name, imported.Name);
    }

    [Fact]
    public void OversizedProfileRemainsShareableViaClipboardWhenQrCapacityIsExceeded()
    {
        var bytes = new byte[6000];
        new Random(42).NextBytes(bytes);
        var noise = Convert.ToBase64String(bytes);
        var profile = Profile() with { Content = Profile().Content + "\n#" + noise };
        var link = ProfileShareFormatter.CreateLink(profile)!;
        Assert.Equal(profile.Content, Assert.Single(ProfileParser.Parse(link)).Content);
        Assert.Throws<QRCoder.Exceptions.DataTooLongException>(() => QrCodeImage.CreatePng(link));
    }
}
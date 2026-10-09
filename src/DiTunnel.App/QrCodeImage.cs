using Avalonia.Media.Imaging;
using QRCoder;

namespace DiTunnel.App;

internal static class QrCodeImage
{
    internal static byte[] CreatePng(string text) => PngByteQRCodeHelper.GetQRCode(text, QRCodeGenerator.ECCLevel.M, 12);

    public static Bitmap Create(string text)
    {
        using var stream = new MemoryStream(CreatePng(text));
        return new Bitmap(stream);
    }
}
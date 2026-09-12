using Avalonia.Media.Imaging;
using QRCoder;

namespace DiTunnel.App;

internal static class QrCodeImage
{
    public static Bitmap Create(string text)
    {
        var bytes = PngByteQRCodeHelper.GetQRCode(text, QRCodeGenerator.ECCLevel.M, 12);
        return new Bitmap(new MemoryStream(bytes));
    }
}

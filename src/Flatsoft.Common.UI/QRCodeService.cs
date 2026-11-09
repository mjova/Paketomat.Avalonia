using System.Drawing.Imaging;
using System.IO;
using System.Windows.Media.Imaging;
using QRCoder;

namespace Flatsoft.Common.UI
{
    public static class QrCodeService
    {
        public static byte[]? GenerateQRCode(string url)
        {
            using var qrGenerator = new QRCodeGenerator();
            using var qrCodeData = qrGenerator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
            using var qrCode = new QRCode(qrCodeData);
            using var bitmap = qrCode.GetGraphic(20);
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }

        public static BitmapImage GetQRCodeImage(string url)
        {
            byte[] imageData = GenerateQRCode(url) ?? throw new InvalidOperationException("Failed to generate QR code image data.");
            using var ms = new MemoryStream(imageData);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = ms;
            image.EndInit();
            image.Freeze(); // Makes it cross-thread accessible
            return image;
        }
    }
}

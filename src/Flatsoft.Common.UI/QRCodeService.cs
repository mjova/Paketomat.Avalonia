using QRCoder;

namespace Flatsoft.Common.UI
{
	// v4: samo PNG bajtovi (QRCoder PngByteQRCode, bez System.Drawing). Prikaz od njih pravi sliku; v2 GetQRCodeImage je
	// vraćao WPF BitmapImage.
	public static class QrCodeService
	{
		public static byte[] GenerateQRCode(string url)
		{
			using var qrGenerator = new QRCodeGenerator();
			using var qrCodeData = qrGenerator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
			var qrCode = new PngByteQRCode(qrCodeData);
			return qrCode.GetGraphic(20);
		}
	}
}

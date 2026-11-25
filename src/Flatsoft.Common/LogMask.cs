namespace Flatsoft.Common
{
	/// <summary>
	/// v4: pristupni kod u logu (prijava kurira, servisni kod, skenirani barkod). Skriva se prva trećina koda, a umesto nje
	/// uvek stoji isti broj zvezdica, da se iz loga ne vidi koliko je znakova skriveno; ostatak ostaje za proveru ko se
	/// prijavio. Delovi odvojeni sa ';' (ručna prijava kurira: barkod;kod) se maskiraju posebno.
	/// </summary>
	public static class LogMask
	{
		public const string Hidden = "*****";

		public static string Credential(string? value)
			=> string.IsNullOrEmpty(value) ? Hidden : string.Join(';', value.Split(';').Select(MaskPart));

		private static string MaskPart(string part) => Hidden + part[((part.Length + 2) / 3)..];
	}
}
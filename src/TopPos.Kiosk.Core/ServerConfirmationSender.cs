using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using TopPos.Service;

namespace TopPos.Client.WPF
{
	public enum ServerConfirmationKind
	{
		/// <summary>Primalac je preuzeo paket — <c>PrimaocPraznjenjePozicijePotvrda</c>.</summary>
		RecipientPickup,
	}

	/// <summary>
	/// Potvrda radnje sa vratima koja zbog prolazne greške nije stigla do servera. Polja zavise od vrste:
	/// <see cref="ServerConfirmationKind.RecipientPickup"/> — <see cref="UnlockCode"/>, <see cref="TelephoneNumber"/>.
	/// Potvrda ostavljanja ne ide u red: kurir/pošiljalac ne nastavlja dok je server ne potvrdi ili dok sam ne odluči šta sa
	/// paketom (server poziciju zauzima tek potvrdom, pa bi zakasnela potvrda mogla da stigne posle dodele istih vrata).
	/// </summary>
	public record ServerConfirmation([property: JsonConverter(typeof(JsonStringEnumConverter))] ServerConfirmationKind Kind,
									int DoorNumber,
									int? UnlockCode = null,
									string? TelephoneNumber = null) : QueuedConfirmation
	{
		public static ServerConfirmation ForRecipientPickup(int unlockCode, string telephoneNumber, int doorNumber)
			=> new(ServerConfirmationKind.RecipientPickup, doorNumber, UnlockCode: unlockCode, TelephoneNumber: telephoneNumber);
	}

	/// <summary>
	/// Trajni red potvrda preuzimanja (<see cref="ConfirmationQueue{T}"/> nad <see cref="ServerConfirmation"/>).
	/// Aktivator potvrdu šalje odmah; u red ide samo kad slanje ne uspe zbog prolazne greške (mreža, 5xx, isteklo vreme).
	/// Odbijanje servera (400) se ne ponavlja. Ponovljenu potvrdu server prihvata sa 200 (de-test, 2026-10-09).
	/// </summary>
	public class ServerConfirmationSender : IDisposable
	{
		private readonly ConfirmationQueue<ServerConfirmation> _queue;
		private readonly TopPosServiceClient _serviceClient;

		public ServerConfirmationSender(ServerConfirmationSenderConfiguration configuration, TopPosServiceClient serviceClient, ILogger logger)
		{
			ArgumentNullException.ThrowIfNull(configuration);
			_serviceClient = serviceClient ?? throw new ArgumentNullException(nameof(serviceClient));
			ArgumentNullException.ThrowIfNull(logger);

			_queue = new ConfirmationQueue<ServerConfirmation>(nameof(ServerConfirmationSender), "potvrda preuzimanja",
																configuration.ServerConfirmationFilename,
																TimeSpan.FromMilliseconds(configuration.ProcessingDelayInMilliseconds ?? 1000),
																Send, Describe, logger);
		}

		/// <summary>
		/// Dodaje potvrdu u red i snima red na disk. Vraća <c>true</c> samo ako je red upisan na disk; <c>false</c> znači da
		/// potvrda (ako je dodata) postoji samo u memoriji — šalje se dok program radi, ali je restart pre uspešnog slanja gubi.
		/// </summary>
		public Task<bool> Add(ServerConfirmation confirmation) => _queue.Add(confirmation);

		private async Task<ConfirmationSendResult> Send(ServerConfirmation c, CancellationToken cancellationToken)
		{
			(bool confirmed, string? error, bool recoverable) result;
			switch (c.Kind) {
				case ServerConfirmationKind.RecipientPickup when c.DoorNumber > 0 && c.UnlockCode.HasValue && c.TelephoneNumber != null:
					result = await _serviceClient.RecipientPackagePickupConfirm(c.UnlockCode.Value, c.TelephoneNumber, c.DoorNumber, cancellationToken)
												.ConfigureAwait(false);
					break;
				default:
					// nepotpun zapis (npr. ručno izmenjen fajl; izostavljen DoorNumber se učita kao 0) ne sme da se ponavlja 48 h
					return ConfirmationSendResult.Rejected;
			}
			return Classify(result.confirmed, result.recoverable);
		}

		/// <summary>Ishod slanja iz odgovora <see cref="TopPosServiceClient.RecipientPackagePickupConfirm"/>.</summary>
		public static ConfirmationSendResult Classify(bool confirmed, bool recoverable)
			=> confirmed ? ConfirmationSendResult.Delivered
				: recoverable ? ConfirmationSendResult.Retry
				: ConfirmationSendResult.Rejected;

		private static string Describe(ServerConfirmation c) => c.Kind switch {
			ServerConfirmationKind.RecipientPickup => $"potvrda preuzimanja [UnlockCode={c.UnlockCode}, Telefon={c.TelephoneNumber}, Vrata={c.DoorNumber}, CreatedUtc={c.CreatedUtc:O}]",
			_ => $"potvrda {c} [CreatedUtc={c.CreatedUtc:O}]",
		};

		public void Dispose() => _queue.Dispose();
	}
}

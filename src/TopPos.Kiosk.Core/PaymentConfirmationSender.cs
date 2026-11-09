using Microsoft.Extensions.Logging;
using TopPos.Service;

namespace TopPos.Client.WPF;
public record PaymentConfirmation(string UnlockCode, string TransactionNumber, string TidNumber, decimal Amount) : QueuedConfirmation;

/// <summary>Trajni red potvrda plaćanja (<c>PotvrdaPlacanja</c>) — <see cref="ConfirmationQueue{T}"/> nad <see cref="PaymentConfirmation"/>.</summary>
public class PaymentConfirmationSender : IDisposable
{
    // Odgovor servera na ponovljenu potvrdu za već evidentiranu uplatu (de-test, 2026-10-09): 400 sa ovom porukom. Obično
    // znači da je odgovor na prvo slanje izgubljen. Poruka je vezana za paket, ne za transakciju: ako je kupac u međuvremenu
    // platio ponovo (druga transakcija), naplaćen je dvaput — zato red to loguje kao Warning.
    internal const string AlreadyRecordedMessage = "je već evidentirana uplata";

    private readonly ConfirmationQueue<PaymentConfirmation> _queue;
    private readonly TopPosServiceClient _serviceClient;

    public PaymentConfirmationSender(PaymentConfirmationSenderConfiguration configuration, TopPosServiceClient serviceClient, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _serviceClient = serviceClient ?? throw new ArgumentNullException(nameof(serviceClient));
        ArgumentNullException.ThrowIfNull(logger);

        _queue = new ConfirmationQueue<PaymentConfirmation>(nameof(PaymentConfirmationSender), "potvrda za placanje",
                                                            configuration.PaymentConfirmationFilename,
                                                            TimeSpan.FromMilliseconds(configuration.ProcessingDelayInMilliseconds ?? 1000),
                                                            Send, Describe, logger);
    }

    /// <summary>
    /// Dodaje potvrdu u listu za slanje i snima listu na disk. Vraća <c>true</c> samo ako je lista upisana na disk;
    /// <c>false</c> znači da potvrda (ako je dodata) postoji samo u memoriji — šalje se dok program radi, ali je
    /// restart pre uspešnog slanja gubi. Pozivalac tada mora da obavesti kupca i podršku.
    /// </summary>
    public Task<bool> AddPaymentConfirmation(PaymentConfirmation confirmation) => _queue.Add(confirmation);

    private async Task<ConfirmationSendResult> Send(PaymentConfirmation payment, CancellationToken cancellationToken)
    {
        var (paymentId, error, recoverableError) = await _serviceClient.PaymentConfirmation(payment.UnlockCode,
                                                                                            payment.TransactionNumber,
                                                                                            payment.Amount,
                                                                                            payment.TidNumber,
                                                                                            cancellationToken)
                                                                        .ConfigureAwait(false);
        return Classify(paymentId, error, recoverableError);
    }

    /// <summary>Ishod slanja iz odgovora <see cref="TopPosServiceClient.PaymentConfirmation"/>.</summary>
    public static ConfirmationSendResult Classify(int? paymentId, string? error, bool recoverableError)
    {
        if (paymentId.HasValue) {
            return ConfirmationSendResult.Delivered;
        }
        if (recoverableError) {
            return ConfirmationSendResult.Retry;
        }
        return error?.Contains(AlreadyRecordedMessage, StringComparison.Ordinal) == true
                ? ConfirmationSendResult.AlreadyDelivered
                : ConfirmationSendResult.Rejected;
    }

    private static string Describe(PaymentConfirmation c)
        => $"potvrda placanja [UnlockCode={c.UnlockCode}, TransactionId={c.TransactionNumber}, Amount={c.Amount}, CreatedUtc={c.CreatedUtc:O}]";

    public void Dispose() => _queue.Dispose();
}

using System.Globalization;

namespace UPSControlCenter.Web.Services;

/// <summary>
/// Converte gli istanti UTC nel fuso scelto per l'interfaccia, senza dipendere dal fuso
/// locale del container (che nelle immagini Linux minimali può restare UTC).
/// </summary>
public sealed class DisplayTimeFormatter
{
    public const string DefaultTimeZoneId = "Europe/Rome";

    private static readonly CultureInfo ItalianCulture = CultureInfo.GetCultureInfo("it-IT");
    private readonly TimeZoneInfo _timeZone;

    public DisplayTimeFormatter(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
            throw new InvalidOperationException("Il fuso orario dell'interfaccia non può essere vuoto.");

        try
        {
            _timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException ex)
        {
            throw new InvalidOperationException(
                $"Fuso orario dell'interfaccia non trovato: '{timeZoneId}'.", ex);
        }
        catch (InvalidTimeZoneException ex)
        {
            throw new InvalidOperationException(
                $"Fuso orario dell'interfaccia non valido: '{timeZoneId}'.", ex);
        }
    }

    public string TimeZoneId => _timeZone.Id;

    public DateTimeOffset Convert(DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, _timeZone);

    public string Format(DateTimeOffset? value, string format, string unavailable) =>
        value is { } instant
            ? Convert(instant).ToString(format, ItalianCulture)
            : unavailable;
}

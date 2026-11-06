namespace UPSControlCenter.Core.Nut;

/// <summary>
/// Lettura normalizzata di un UPS in un istante preciso.
/// <para>
/// <b>Ogni metrica è nullable per progetto.</b> <c>null</c> significa "NUT non ha fornito la
/// variabile" e non viene mai convertito in zero: §9.3 vieta esplicitamente di interpretare
/// valori mancanti come 0, perché "autonomia 0" e "autonomia sconosciuta" portano a decisioni opposte.
/// </para>
/// </summary>
public sealed record UpsSnapshot
{
    public required string UpsName { get; init; }

    /// <summary>Istante di acquisizione (UTC). Serve per valutare la freschezza del dato.</summary>
    public required DateTimeOffset AcquiredAt { get; init; }

    /// <summary>Valore grezzo di <c>ups.status</c>, conservato per diagnosi e audit.</summary>
    public string? StatusRaw { get; init; }

    public required UpsStatusFlags Status { get; init; }

    /// <summary>Esito della comunicazione con NUT. Se falso, le metriche non sono attendibili.</summary>
    public required bool CommunicationOk { get; init; }

    public string? CommunicationError { get; init; }

    /// <summary>Vero se il dato è più vecchio della soglia di obsolescenza configurata.</summary>
    public bool IsStale { get; init; }

    public decimal? BatteryChargePercent { get; init; }

    /// <summary>
    /// <c>battery.runtime</c> in secondi. È una <b>stima del firmware</b>, non una previsione
    /// garantita: le regole non ne dipendono in modo esclusivo (R4).
    /// </summary>
    public int? BatteryRuntimeSeconds { get; init; }

    public decimal? BatteryVoltage { get; init; }
    public decimal? InputVoltage { get; init; }
    public decimal? OutputVoltage { get; init; }
    public decimal? InputFrequency { get; init; }
    public decimal? OutputFrequency { get; init; }
    public decimal? LoadPercent { get; init; }
    public decimal? RealPowerWatts { get; init; }

    /// <summary>
    /// Corrente in ampere riportata direttamente dal driver (<c>output.current</c>,
    /// <c>input.current</c>). Non tutti i modelli la espongono: resta <c>null</c> in tal caso,
    /// non viene mai stimata al posto del dato reale (§9.3).
    /// </summary>
    public decimal? OutputCurrentAmps { get; init; }
    public decimal? InputCurrentAmps { get; init; }

    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    public string? Serial { get; init; }
    public string? FirmwareVersion { get; init; }

    /// <summary>Dati trifase, presenti solo su alcune configurazioni. Chiave = nome variabile NUT.</summary>
    public IReadOnlyDictionary<string, string> PhaseData { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Tutte le variabili grezze restituite da NUT, per la pagina di diagnostica.</summary>
    public IReadOnlyDictionary<string, string> RawVariables { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Dato utilizzabile per prendere decisioni: comunicazione ok, non obsoleto, stato noto.</summary>
    public bool IsUsableForDecisions =>
        CommunicationOk && !IsStale && !Status.IsUnknown();

    /// <summary>Snapshot che rappresenta l'impossibilità di leggere l'UPS. Nessuna metrica inventata.</summary>
    public static UpsSnapshot Unreachable(string upsName, DateTimeOffset at, string error) => new()
    {
        UpsName = upsName,
        AcquiredAt = at,
        Status = UpsStatusFlags.Unknown,
        CommunicationOk = false,
        CommunicationError = error,
        IsStale = true
    };
}

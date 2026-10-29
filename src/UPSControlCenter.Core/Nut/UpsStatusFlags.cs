namespace UPSControlCenter.Core.Nut;

/// <summary>
/// Flag di <c>ups.status</c> di NUT. È una bitmask perché NUT restituisce combinazioni
/// (es. <c>"OB LB"</c>, <c>"OL CHRG"</c>) e il sistema deve ragionare sulle combinazioni (§6).
/// </summary>
[Flags]
public enum UpsStatusFlags
{
    None = 0,
    /// <summary>Online: alimentazione di rete presente.</summary>
    OnLine = 1 << 0,
    /// <summary>On battery: alimentazione di rete assente.</summary>
    OnBattery = 1 << 1,
    /// <summary>Low battery: condizione terminale segnalata dall'UPS.</summary>
    LowBattery = 1 << 2,
    Charging = 1 << 3,
    Discharging = 1 << 4,
    Bypass = 1 << 5,
    ReplaceBattery = 1 << 6,
    Alarm = 1 << 7,
    Overload = 1 << 8,
    Trim = 1 << 9,
    Boost = 1 << 10,
    Calibration = 1 << 11,
    /// <summary>Forced shutdown segnalato dall'UPS. Il sistema lo osserva ma non lo emette mai (§15).</summary>
    ForcedShutdown = 1 << 12,
    Off = 1 << 13,
    /// <summary>Lo stato non è determinabile: NUT non raggiungibile o <c>ups.status</c> assente.</summary>
    Unknown = 1 << 14
}

public static class UpsStatusParser
{
    /// <summary>
    /// Converte il valore testuale di <c>ups.status</c> nella bitmask corrispondente.
    /// Token non riconosciuti vengono ignorati senza errori: driver diversi aggiungono token propri.
    /// Stringa vuota o nulla produce <see cref="UpsStatusFlags.Unknown"/>: non si assume "online".
    /// </summary>
    public static UpsStatusFlags Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return UpsStatusFlags.Unknown;

        var flags = UpsStatusFlags.None;
        foreach (var token in raw.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            flags |= token.Trim().ToUpperInvariant() switch
            {
                "OL" => UpsStatusFlags.OnLine,
                "OB" => UpsStatusFlags.OnBattery,
                "LB" => UpsStatusFlags.LowBattery,
                "CHRG" => UpsStatusFlags.Charging,
                "DISCHRG" => UpsStatusFlags.Discharging,
                "BYPASS" => UpsStatusFlags.Bypass,
                "RB" => UpsStatusFlags.ReplaceBattery,
                "ALARM" => UpsStatusFlags.Alarm,
                "OVER" => UpsStatusFlags.Overload,
                "TRIM" => UpsStatusFlags.Trim,
                "BOOST" => UpsStatusFlags.Boost,
                "CAL" => UpsStatusFlags.Calibration,
                "FSD" => UpsStatusFlags.ForcedShutdown,
                "OFF" => UpsStatusFlags.Off,
                _ => UpsStatusFlags.None
            };
        }

        // Nessun token riconosciuto: meglio dichiarare l'incertezza che inventare uno stato.
        return flags == UpsStatusFlags.None ? UpsStatusFlags.Unknown : flags;
    }

    /// <summary>
    /// Vero se l'UPS è su batteria. <c>OB</c> ha precedenza su <c>OL</c>: alcuni driver
    /// riportano entrambi durante le transizioni e il caso pericoloso è quello su batteria.
    /// </summary>
    public static bool IsOnBattery(this UpsStatusFlags flags) =>
        flags.HasFlag(UpsStatusFlags.OnBattery);

    /// <summary>Vero solo se è presente <c>OL</c> senza <c>OB</c>: alimentazione di rete affidabilmente presente.</summary>
    public static bool IsOnLine(this UpsStatusFlags flags) =>
        flags.HasFlag(UpsStatusFlags.OnLine) && !flags.HasFlag(UpsStatusFlags.OnBattery);

    public static bool IsLowBattery(this UpsStatusFlags flags) =>
        flags.HasFlag(UpsStatusFlags.LowBattery);

    public static bool IsUnknown(this UpsStatusFlags flags) =>
        flags == UpsStatusFlags.None || flags.HasFlag(UpsStatusFlags.Unknown);

    /// <summary>Rende la bitmask nella forma testuale NUT, per log e UI.</summary>
    public static string ToNutString(this UpsStatusFlags flags)
    {
        if (flags.IsUnknown()) return "UNKNOWN";
        var parts = new List<string>(4);
        if (flags.HasFlag(UpsStatusFlags.OnLine)) parts.Add("OL");
        if (flags.HasFlag(UpsStatusFlags.OnBattery)) parts.Add("OB");
        if (flags.HasFlag(UpsStatusFlags.LowBattery)) parts.Add("LB");
        if (flags.HasFlag(UpsStatusFlags.Charging)) parts.Add("CHRG");
        if (flags.HasFlag(UpsStatusFlags.Discharging)) parts.Add("DISCHRG");
        if (flags.HasFlag(UpsStatusFlags.Bypass)) parts.Add("BYPASS");
        if (flags.HasFlag(UpsStatusFlags.ReplaceBattery)) parts.Add("RB");
        if (flags.HasFlag(UpsStatusFlags.Alarm)) parts.Add("ALARM");
        if (flags.HasFlag(UpsStatusFlags.Overload)) parts.Add("OVER");
        if (flags.HasFlag(UpsStatusFlags.Trim)) parts.Add("TRIM");
        if (flags.HasFlag(UpsStatusFlags.Boost)) parts.Add("BOOST");
        if (flags.HasFlag(UpsStatusFlags.Calibration)) parts.Add("CAL");
        if (flags.HasFlag(UpsStatusFlags.ForcedShutdown)) parts.Add("FSD");
        if (flags.HasFlag(UpsStatusFlags.Off)) parts.Add("OFF");
        return parts.Count == 0 ? "UNKNOWN" : string.Join(' ', parts);
    }
}

using UPSControlCenter.Core.Nut;
using UPSControlCenter.Nut;

namespace UPSControlCenter.Tests.Nut;

/// <summary>
/// Parsing delle risposte di <c>upsd</c>. I valori usati riproducono l'output atteso dal driver
/// <c>riello_ser</c> sull'UPS installato (§9.2).
/// </summary>
public sealed class NutParsingTests
{
    private static readonly string[] SampleResponse =
    [
        "VAR riello battery.charge \"100\"",
        "VAR riello battery.runtime \"3540\"",
        "VAR riello battery.voltage \"54.6\"",
        "VAR riello input.voltage \"231.0\"",
        "VAR riello input.frequency \"50.0\"",
        "VAR riello output.voltage \"230.2\"",
        "VAR riello output.frequency \"50.0\"",
        "VAR riello ups.load \"23\"",
        "VAR riello ups.status \"OL\"",
        "VAR riello ups.mfr \"RPS S.p.a.\"",
        "VAR riello ups.model \"DS3MK10CS100RUA\"",
        "VAR riello ups.serial \"1234567890\""
    ];

    [Fact]
    public void Le_variabili_vengono_estratte_correttamente()
    {
        var vars = NutTcpClient.ParseVariables(SampleResponse, "riello");

        Assert.Equal("100", vars["battery.charge"]);
        Assert.Equal("3540", vars["battery.runtime"]);
        Assert.Equal("OL", vars["ups.status"]);
        // I valori con spazi devono restare integri: il nome del produttore ne contiene.
        Assert.Equal("RPS S.p.a.", vars["ups.mfr"]);
        Assert.Equal("DS3MK10CS100RUA", vars["ups.model"]);
    }

    [Fact]
    public void Lo_snapshot_normalizza_i_valori_numerici_con_cultura_invariante()
    {
        var vars = NutTcpClient.ParseVariables(SampleResponse, "riello");
        var snapshot = NutTcpClient.BuildSnapshot("riello", DateTimeOffset.UtcNow, vars);

        // Il separatore decimale è il punto anche se la cultura di sistema è italiana:
        // interpretare "54.6" come 546 falserebbe ogni soglia.
        Assert.Equal(54.6m, snapshot.BatteryVoltage);
        Assert.Equal(231.0m, snapshot.InputVoltage);
        Assert.Equal(100m, snapshot.BatteryChargePercent);
        Assert.Equal(3540, snapshot.BatteryRuntimeSeconds);
        Assert.Equal(23m, snapshot.LoadPercent);
        Assert.True(snapshot.Status.IsOnLine());
    }

    [Fact]
    public void Le_variabili_assenti_restano_null_e_non_diventano_zero()
    {
        // Risposta minimale: solo lo stato. Tutto il resto non è disponibile.
        var vars = NutTcpClient.ParseVariables(["VAR riello ups.status \"OB\""], "riello");
        var snapshot = NutTcpClient.BuildSnapshot("riello", DateTimeOffset.UtcNow, vars);

        Assert.Null(snapshot.BatteryChargePercent);
        Assert.Null(snapshot.BatteryRuntimeSeconds);
        Assert.Null(snapshot.LoadPercent);
        Assert.Null(snapshot.InputVoltage);
        Assert.Null(snapshot.Manufacturer);
        Assert.True(snapshot.Status.IsOnBattery());
    }

    [Fact]
    public void Un_valore_non_numerico_non_produce_un_numero_inventato()
    {
        var vars = NutTcpClient.ParseVariables(
            ["VAR riello battery.runtime \"unknown\"", "VAR riello ups.status \"OB\""], "riello");

        var snapshot = NutTcpClient.BuildSnapshot("riello", DateTimeOffset.UtcNow, vars);

        Assert.Null(snapshot.BatteryRuntimeSeconds);
    }

    [Fact]
    public void L_autonomia_decimale_viene_arrotondata_per_difetto()
    {
        // Arrotondare per eccesso sovrastimerebbe il tempo disponibile: nel dubbio si assume meno.
        var vars = NutTcpClient.ParseVariables(
            ["VAR riello battery.runtime \"1799.9\"", "VAR riello ups.status \"OB\""], "riello");

        var snapshot = NutTcpClient.BuildSnapshot("riello", DateTimeOffset.UtcNow, vars);

        Assert.Equal(1799, snapshot.BatteryRuntimeSeconds);
    }

    [Fact]
    public void I_dati_trifase_vengono_raccolti_quando_presenti()
    {
        string[] threePhase =
        [
            "VAR riello ups.status \"OL\"",
            "VAR riello input.phases \"3\"",
            "VAR riello input.L1-N.voltage \"230.1\"",
            "VAR riello input.L2-N.voltage \"231.4\"",
            "VAR riello input.L3-N.voltage \"229.8\""
        ];

        var vars = NutTcpClient.ParseVariables(threePhase, "riello");
        var snapshot = NutTcpClient.BuildSnapshot("riello", DateTimeOffset.UtcNow, vars);

        Assert.Equal(4, snapshot.PhaseData.Count);
        Assert.Equal("230.1", snapshot.PhaseData["input.L1-N.voltage"]);
        Assert.Equal("3", snapshot.PhaseData["input.phases"]);
    }

    [Fact]
    public void Le_righe_di_altri_UPS_vengono_ignorate()
    {
        // Su un server NUT con più UPS la risposta può contenere righe di altri dispositivi.
        string[] mixed =
        [
            "VAR riello ups.status \"OL\"",
            "VAR altroups ups.status \"OB\"",
            "VAR altroups battery.charge \"20\""
        ];

        var vars = NutTcpClient.ParseVariables(mixed, "riello");

        Assert.Single(vars);
        Assert.Equal("OL", vars["ups.status"]);
    }

    [Fact]
    public void Lo_snapshot_non_raggiungibile_non_contiene_metriche()
    {
        var snapshot = UpsSnapshot.Unreachable("riello", DateTimeOffset.UtcNow,
            "Connessione rifiutata");

        Assert.False(snapshot.CommunicationOk);
        Assert.True(snapshot.IsStale);
        Assert.True(snapshot.Status.IsUnknown());
        Assert.False(snapshot.IsUsableForDecisions);
        Assert.Null(snapshot.BatteryRuntimeSeconds);
        Assert.Null(snapshot.BatteryChargePercent);
    }

    [Fact]
    public void Un_valore_con_apici_interni_viene_decodificato()
    {
        var vars = NutTcpClient.ParseVariables(
            ["VAR riello ups.model \"Modello \\\"Speciale\\\"\""], "riello");

        Assert.Equal("Modello \"Speciale\"", vars["ups.model"]);
    }
}

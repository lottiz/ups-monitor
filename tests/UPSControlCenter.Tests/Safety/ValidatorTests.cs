using UPSControlCenter.Core.Enums;
using UPSControlCenter.Core.Safety;

namespace UPSControlCenter.Tests.Safety;

/// <summary>
/// Validazione della configurazione (§3.3). È l'ultima barriera prima che una configurazione
/// raggiunga il Safety Agent, quindi i test coprono i casi che produrrebbero comportamenti
/// errati durante un blackout reale.
/// </summary>
public sealed class ValidatorTests
{
    private readonly SafetyConfigurationValidator _validator = new();

    [Fact]
    public void Una_configurazione_minima_valida_viene_accettata()
    {
        var result = _validator.Validate(ConfigurationIntegrityTests.Minimal());

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Soglia_di_obsolescenza_minore_del_polling_viene_rifiutata()
    {
        // Con questa combinazione ogni lettura risulterebbe obsoleta e il sistema non
        // deciderebbe mai nulla.
        var config = ConfigurationIntegrityTests.Minimal() with
        {
            Ups = ConfigurationIntegrityTests.Minimal().Ups with
            {
                PollingIntervalSeconds = 60,
                StaleDataThresholdSeconds = 30
            }
        };

        var result = _validator.Validate(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("obsolescenza"));
    }

    [Fact]
    public void Modalita_LIVE_senza_adapter_abilitati_viene_rifiutata()
    {
        var config = ConfigurationIntegrityTests.Minimal() with
        {
            ExecutionMode = ExecutionMode.Live,
            LiveEnabledAdapters = []
        };

        var result = _validator.Validate(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("LIVE senza alcun adapter"));
    }

    [Fact]
    public void Modalita_DRY_RUN_con_adapter_LIVE_abilitati_viene_rifiutata()
    {
        // Combinazione contraddittoria: va bloccata, non interpretata.
        var config = ConfigurationIntegrityTests.Minimal() with
        {
            ExecutionMode = ExecutionMode.DryRun,
            LiveEnabledAdapters = ["Ssh"]
        };

        var result = _validator.Validate(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("DRY_RUN con adapter LIVE"));
    }

    [Fact]
    public void Modalita_LIVE_con_adapter_produce_un_avviso_esplicito()
    {
        var config = ConfigurationIntegrityTests.Minimal() with
        {
            ExecutionMode = ExecutionMode.Live,
            LiveEnabledAdapters = ["Ssh"]
        };

        var result = _validator.Validate(config);

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("LIVE"));
    }

    [Fact]
    public void Un_dispositivo_abilitato_senza_protocollo_viene_rifiutato()
    {
        var config = ConfigurationIntegrityTests.Minimal() with
        {
            Devices =
            [
                new SafetyDevice
                {
                    Id = Guid.CreateVersion7(),
                    Name = "srv-app",
                    Hostname = "srv-app.local",
                    ShutdownProtocol = ShutdownProtocol.None,
                    IsEnabled = true
                }
            ]
        };

        var result = _validator.Validate(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("senza protocollo di spegnimento"));
    }

    [Fact]
    public void Una_dipendenza_circolare_viene_rifiutata()
    {
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();

        var config = ConfigurationIntegrityTests.Minimal() with
        {
            Devices =
            [
                new SafetyDevice { Id = a, Name = "A", Hostname = "a", ShutdownProtocol = ShutdownProtocol.Ssh },
                new SafetyDevice { Id = b, Name = "B", Hostname = "b", ShutdownProtocol = ShutdownProtocol.Ssh }
            ],
            Dependencies =
            [
                new SafetyDependency { DependentDeviceId = a, DependsOnDeviceId = b },
                new SafetyDependency { DependentDeviceId = b, DependsOnDeviceId = a }
            ]
        };

        var result = _validator.Validate(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("circolare"));
    }

    [Fact]
    public void Un_dispositivo_che_dipende_da_se_stesso_viene_rifiutato()
    {
        var a = Guid.CreateVersion7();

        var config = ConfigurationIntegrityTests.Minimal() with
        {
            Devices =
            [
                new SafetyDevice { Id = a, Name = "A", Hostname = "a", ShutdownProtocol = ShutdownProtocol.Ssh }
            ],
            Dependencies = [new SafetyDependency { DependentDeviceId = a, DependsOnDeviceId = a }]
        };

        var result = _validator.Validate(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("se stesso"));
    }

    [Fact]
    public void Uno_step_verso_un_dispositivo_inesistente_viene_rifiutato()
    {
        var config = ConfigurationIntegrityTests.Minimal() with
        {
            ShutdownSteps =
            [
                new SafetyStep
                {
                    Id = Guid.CreateVersion7(),
                    OrderIndex = 10,
                    Name = "Arresto fantasma",
                    StepType = SequenceStepType.ShutdownDevice,
                    TargetDeviceId = Guid.CreateVersion7()
                }
            ]
        };

        var result = _validator.Validate(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("dispositivo inesistente"));
    }

    [Fact]
    public void Lo_step_di_arresto_dell_host_NUT_deve_essere_l_ultimo()
    {
        var device = Guid.CreateVersion7();

        var config = ConfigurationIntegrityTests.Minimal() with
        {
            Devices =
            [
                new SafetyDevice { Id = device, Name = "srv", Hostname = "srv", ShutdownProtocol = ShutdownProtocol.Ssh }
            ],
            ShutdownSteps =
            [
                // Arrestare il mini PC a metà sequenza renderebbe impossibile ogni azione
                // successiva: va bloccato in validazione (§13).
                new SafetyStep
                {
                    Id = Guid.CreateVersion7(), OrderIndex = 10, Name = "Arresto mini PC",
                    StepType = SequenceStepType.ShutdownNutHost
                },
                new SafetyStep
                {
                    Id = Guid.CreateVersion7(), OrderIndex = 20, Name = "Arresto server",
                    StepType = SequenceStepType.ShutdownDevice, TargetDeviceId = device
                }
            ]
        };

        var result = _validator.Validate(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("host NUT"));
    }

    [Fact]
    public void Lo_step_di_arresto_dell_host_NUT_in_ultima_posizione_produce_un_avviso()
    {
        var config = ConfigurationIntegrityTests.Minimal() with
        {
            ShutdownSteps =
            [
                new SafetyStep
                {
                    Id = Guid.CreateVersion7(), OrderIndex = 99, Name = "Arresto mini PC",
                    StepType = SequenceStepType.ShutdownNutHost
                }
            ]
        };

        var result = _validator.Validate(config);

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("host NUT"));
    }

    [Fact]
    public void Indici_di_ordinamento_duplicati_vengono_rifiutati()
    {
        var config = ConfigurationIntegrityTests.Minimal() with
        {
            ShutdownSteps =
            [
                new SafetyStep
                {
                    Id = Guid.CreateVersion7(), OrderIndex = 10, Name = "Primo",
                    StepType = SequenceStepType.Notify
                },
                new SafetyStep
                {
                    Id = Guid.CreateVersion7(), OrderIndex = 10, Name = "Secondo",
                    StepType = SequenceStepType.Notify
                }
            ]
        };

        var result = _validator.Validate(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Indice di ordinamento duplicato"));
    }

    [Fact]
    public void Una_soglia_di_autonomia_molto_bassa_produce_un_avviso()
    {
        var config = ConfigurationIntegrityTests.Minimal() with
        {
            Thresholds = new SafetyThresholds { MinRemainingRuntimeSeconds = 120 }
        };

        var result = _validator.Validate(config);

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("sufficiente a completare"));
    }
}

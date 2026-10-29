using System.ComponentModel.DataAnnotations;
using UPSControlCenter.Core.Enums;

namespace UPSControlCenter.Web.ViewModels;

/// <summary>Form di creazione e modifica di un dispositivo gestito (§7).</summary>
public sealed class DeviceFormViewModel
{
    public Guid? Id { get; set; }

    [Required(ErrorMessage = "Il nome è obbligatorio.")]
    [StringLength(128)]
    [Display(Name = "Nome")]
    public string Name { get; set; } = string.Empty;

    [StringLength(512)]
    [Display(Name = "Descrizione")]
    public string? Description { get; set; }

    [Required(ErrorMessage = "L'hostname è obbligatorio.")]
    [StringLength(255)]
    [Display(Name = "Hostname")]
    public string Hostname { get; set; } = string.Empty;

    [Display(Name = "Indirizzo IP")]
    [RegularExpression(@"^(\d{1,3}(\.\d{1,3}){3}|[0-9a-fA-F:]+)$",
        ErrorMessage = "Indirizzo IP non valido.")]
    public string? IpAddress { get; set; }

    [StringLength(128)]
    [Display(Name = "Sistema operativo")]
    public string? OperatingSystem { get; set; }

    [Display(Name = "Tipologia")]
    public DeviceType DeviceType { get; set; } = DeviceType.Generic;

    [Display(Name = "Gruppo")]
    public Guid? DeviceGroupId { get; set; }

    [Range(1, 1000, ErrorMessage = "La priorità deve essere compresa fra 1 e 1000.")]
    [Display(Name = "Priorità")]
    public int Priority { get; set; } = 500;

    [Display(Name = "Protocollo di spegnimento")]
    public ShutdownProtocol ShutdownProtocol { get; set; } = ShutdownProtocol.None;

    [Display(Name = "Protocollo di accensione")]
    public StartupProtocol StartupProtocol { get; set; } = StartupProtocol.None;

    [Range(1, 65535)]
    [Display(Name = "Porta di gestione")]
    public int? ManagementPort { get; set; }

    [Display(Name = "Profilo credenziali")]
    public Guid? ConnectionProfileId { get; set; }

    [RegularExpression(@"^([0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}$",
        ErrorMessage = "Indirizzo MAC non valido (formato atteso AA:BB:CC:DD:EE:FF).")]
    [Display(Name = "Indirizzo MAC (Wake-on-LAN)")]
    public string? MacAddress { get; set; }

    [Range(10, 3600)]
    [Display(Name = "Timeout spegnimento (s)")]
    public int ShutdownTimeoutSeconds { get; set; } = 300;

    [Range(10, 7200)]
    [Display(Name = "Timeout accensione (s)")]
    public int StartupTimeoutSeconds { get; set; } = 600;

    [Range(0, 3600)]
    [Display(Name = "Ritardo successivo (s)")]
    public int PostActionDelaySeconds { get; set; } = 10;

    [Display(Name = "Metodo di verifica stato")]
    public StatusCheckMethod StatusCheckMethod { get; set; } = StatusCheckMethod.None;

    [StringLength(255)]
    [Display(Name = "Bersaglio della verifica")]
    public string? StatusCheckTarget { get; set; }

    [Display(Name = "Abilitato")]
    public bool IsEnabled { get; set; } = true;

    [StringLength(2048)]
    [Display(Name = "Note")]
    public string? Notes { get; set; }

    /// <summary>
    /// UPS che alimentano il dispositivo. Più di uno è ammesso: la perdita di una sola sorgente
    /// non implica assenza di alimentazione (§6).
    /// </summary>
    [Display(Name = "UPS di alimentazione")]
    public List<Guid> PowerSourceUpsIds { get; set; } = [];

    [Display(Name = "UPS primario")]
    public Guid? PrimaryUpsId { get; set; }
}

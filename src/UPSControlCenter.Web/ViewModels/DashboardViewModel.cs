using UPSControlCenter.Contracts.Dto;

namespace UPSControlCenter.Web.ViewModels;

public sealed record DashboardViewModel
{
    public required IReadOnlyList<UpsStatusDto> UpsList { get; init; }
    public required InfrastructureStatusDto Infrastructure { get; init; }
    public required AutomationStatusDto Automation { get; init; }
}

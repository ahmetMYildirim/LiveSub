using PsGameTranslator.App.ViewModels;

namespace PsGameTranslator.App.ViewModels.User;

public sealed class CapturePageViewModel : ObservableObject
{
    public CapturePageViewModel(CaptureViewModel capture, MonitoringViewModel monitoring, RegionViewModel region)
    {
        Capture = capture;
        Monitoring = monitoring;
        Region = region;
    }

    public CaptureViewModel Capture { get; }
    public MonitoringViewModel Monitoring { get; }
    public RegionViewModel Region { get; }
}

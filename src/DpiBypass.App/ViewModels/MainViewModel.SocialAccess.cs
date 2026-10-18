using DpiBypass.App.Infrastructure;

namespace DpiBypass.App.ViewModels;

public sealed partial class MainViewModel
{
    private RelayCommand? _socialAccessCommand;
    private SocialAccessWindow? _socialAccessWindow;
    public RelayCommand SocialAccessCommand => _socialAccessCommand ??= new(() =>
    {
        if (_socialAccessWindow is { IsLoaded: true }) { _socialAccessWindow.Activate(); return; }
        _socialAccessWindow = new SocialAccessWindow(_service) { Owner = Application.Current.MainWindow };
        _socialAccessWindow.Show();
    });
}

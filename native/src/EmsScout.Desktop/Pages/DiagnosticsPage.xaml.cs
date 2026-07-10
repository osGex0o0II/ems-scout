using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using EmsScout.Desktop.ViewModels;

namespace EmsScout.Desktop.Pages;

public sealed partial class DiagnosticsPage : Page
{
    public DiagnosticsViewModel ViewModel { get; }

    public DiagnosticsPage()
    {
        ViewModel = App.Services.GetRequiredService<DiagnosticsViewModel>();
        InitializeComponent();
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        ViewModel.Load();
    }

    private void OpenRecentExport_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DiagnosticFileRow row })
        {
            ViewModel.OpenRecentExport(row);
        }
    }
}

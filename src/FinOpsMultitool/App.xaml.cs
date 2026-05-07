using System.Windows;

namespace FinOpsMultitool;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (s, ex) =>
        {
            MessageBox.Show(
                $"An unhandled error occurred:\n\n{ex.Exception.Message}",
                "Azure FinOps Multitool – Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            ex.Handled = true;
        };
    }
}

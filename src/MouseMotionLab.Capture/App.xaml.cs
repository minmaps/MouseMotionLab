using System.Windows;
using System.Windows.Threading;

namespace MouseMotionLab.Capture;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        base.OnStartup(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Services.CursorConfinementLease.ForceRelease();
        MessageBox.Show(
            e.Exception.ToString(),
            "MouseMotionLab — erreur non gérée",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Services.CursorConfinementLease.ForceRelease();
    }
}


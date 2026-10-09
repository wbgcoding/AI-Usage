using System.Reflection;
using System.Threading;
using System.Windows;

namespace AiUsage.Tests;

/// <summary>Runs WPF work on its own STA thread with a fresh application object, the way the other
/// window tests do, and hands the result (or the failure) back to the calling test.</summary>
internal static class StaTestRunner
{
    public static T Run<T>(Func<T> work)
    {
        T? result = default;
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                result = RunWithApp(work);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        if (!worker.Join(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("STA test did not finish");
        if (failure is not null)
            throw new InvalidOperationException("STA test failed.", failure);
        return result!;
    }

    private static T RunWithApp<T>(Func<T> work)
    {
        typeof(Application).GetField("_appCreatedInThisAppDomain", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, false);
        var app = new AiUsage.App();
        app.InitializeComponent();
        try
        {
            return work();
        }
        finally
        {
            Application.Current?.Shutdown();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            typeof(Application).GetField("_appInstance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, null);
        }
    }
}

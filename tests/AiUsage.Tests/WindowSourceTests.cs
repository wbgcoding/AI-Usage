using System.Windows;
using System.Windows.Interop;
using AiUsage.Services;
using Xunit;

namespace AiUsage.Tests;

/// <summary>A tray start creates the widget's handle before the first Show, and SourceInitialized
/// then runs while the window is not yet the source's root visual: the window's source must still be
/// found there, or the start-up wiring in that handler fails.</summary>
public class WindowSourceTests
{
    [Fact]
    public void The_source_is_found_while_SourceInitialized_runs_for_a_handle_created_before_Show()
    {
        IntPtr seen = IntPtr.Zero;
        IntPtr handle = IntPtr.Zero;
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                var window = new Window();
                window.SourceInitialized += (_, _) => seen = WindowChromeNative.SourceOf(window)?.Handle ?? IntPtr.Zero;
                handle = new WindowInteropHelper(window).EnsureHandle();
                window.Close();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        worker.Join();

        Assert.Null(failure);
        Assert.NotEqual(IntPtr.Zero, handle);
        Assert.Equal(handle, seen);
    }

    [Fact]
    public void A_window_without_a_handle_has_no_source()
    {
        HwndSource? seen = null;
        var worker = new Thread(() => seen = WindowChromeNative.SourceOf(new Window()));
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        worker.Join();

        Assert.Null(seen);
    }
}

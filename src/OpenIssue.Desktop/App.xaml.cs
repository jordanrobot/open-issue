using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace OpenIssue.Desktop;

public partial class App : Application
{
    public static Window Window { get; private set; } = null!;

    public static DispatcherQueue DispatcherQueue { get; private set; } = null!;

    public static nint WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(Window);

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        Window = new MainWindow(args.Arguments);
        Window.Activate();
    }
}

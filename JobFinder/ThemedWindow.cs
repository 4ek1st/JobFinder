using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shell;
using System.Windows.Interop;

namespace JobFinder;

// Custom visuals, with WindowChrome retaining native resizing, dragging and taskbar behavior.
public class ThemedWindow : Window
{
    public static readonly DependencyProperty IsDialogProperty = DependencyProperty.Register(nameof(IsDialog), typeof(bool), typeof(ThemedWindow), new PropertyMetadata(false));
    public bool IsDialog { get => (bool)GetValue(IsDialogProperty); set => SetValue(IsDialogProperty, value); }
    public ThemedWindow()
    {
        SetResourceReference(StyleProperty, "AppWindowStyle");
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 38, ResizeBorderThickness = new(6), GlassFrameThickness = new(0), CornerRadius = new(0), UseAeroCaptionButtons = false });
        CommandBindings.Add(new(SystemCommands.CloseWindowCommand, (_, _) => Close()));
        CommandBindings.Add(new(SystemCommands.MinimizeWindowCommand, (_, _) => SystemCommands.MinimizeWindow(this)));
        CommandBindings.Add(new(SystemCommands.MaximizeWindowCommand, (_, _) => { if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this); else SystemCommands.MaximizeWindow(this); }));
    }
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook((nint hwnd, int message, nint wParam, nint lParam, ref bool handled) =>
        {
            if (message == 0x00A5 && wParam == 2 || message == 0x0112 && ((long)wParam & 0xFFF0) == 0xF100 && lParam == 32)
            { ShowWindowMenu(); handled = true; }
            return 0;
        });
    }
    private void ShowWindowMenu()
    {
        var ui = (DataContext as ViewModel)?.Ui ?? new Ui();
        var menu = new ContextMenu();
        void Add(string name, Action action) { var item = new MenuItem { Header = ui[name] }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        if (!IsDialog && ResizeMode != ResizeMode.NoResize) Add(WindowState == WindowState.Maximized ? "RestoreWindow" : "MaximizeWindow", () => { if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this); else SystemCommands.MaximizeWindow(this); });
        if (!IsDialog) Add("MinimizeWindow", () => SystemCommands.MinimizeWindow(this));
        menu.Items.Add(new Separator()); Add("Close", Close);
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Relative; menu.PlacementTarget = this; menu.HorizontalOffset = 10; menu.VerticalOffset = 38; menu.IsOpen = true;
    }
}

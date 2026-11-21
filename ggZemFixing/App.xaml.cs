using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Composition.SystemBackdrops;
using System.Runtime.InteropServices;

namespace ggZemFixing
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        /// <summary>The main application window — used by pages to obtain the HWND.</summary>
        public static Window MainWindow { get; private set; } = null!;

        private Window window = Window.Current;

        /// <summary>
        /// Initializes the singleton application object.
        /// </summary>
        public App()
        {
            // Required for Windows App SDK Single File Publish
            Environment.SetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY", AppContext.BaseDirectory);
            this.UnhandledException += (s, e) => {
                try { System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "crash.log"), e.Exception.ToString()); } catch {}
            };
            this.InitializeComponent();
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            window ??= new Window();
            MainWindow = window;

            // Apply Mica backdrop for a modern, translucent look
            if (MicaController.IsSupported())
            {
                window.SystemBackdrop = new MicaBackdrop()
                {
                    Kind = MicaKind.Base
                };
            }

            window.ExtendsContentIntoTitleBar = false;
            window.Title = "ggZemFixing";

            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (System.IO.File.Exists(iconPath))
            {
                window.AppWindow.SetIcon(iconPath);
            }

            if (window.AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable  = false;
                presenter.IsMaximizable = false;
            }

            // Retrieve the window handle to query the current DPI scaling
            IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            const int staticLogicalW = 660;
            const int staticLogicalH = 550;

            uint dpi = 0;
            if (hWnd != IntPtr.Zero)
            {
                EnforceStaticWindowSize(hWnd, staticLogicalW, staticLogicalH);
                dpi = GetDpiForWindow(hWnd);
            }
            if (dpi == 0)
            {
                dpi = GetDpiForSystem();
            }
            double scale = dpi > 0 ? (dpi / 96.0) : 1.0;

            int targetW = (int)(staticLogicalW * scale);
            int targetH = (int)(staticLogicalH * scale);

            // Centering on the primary display
            var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                window.AppWindow.Id,
                Microsoft.UI.Windowing.DisplayAreaFallback.Primary);

            int cx = 100;
            int cy = 100;

            if (displayArea != null && displayArea.WorkArea.Width > 100 && displayArea.WorkArea.Height > 100)
            {
                targetW = Math.Min(targetW, displayArea.WorkArea.Width - 40);
                targetH = Math.Min(targetH, displayArea.WorkArea.Height - 40);
                cx = displayArea.WorkArea.X + (displayArea.WorkArea.Width - targetW) / 2;
                cy = displayArea.WorkArea.Y + (displayArea.WorkArea.Height - targetH) / 2;
            }
            window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(cx, cy, targetW, targetH));

            if (window.Content is not Frame rootFrame)
            {
                rootFrame = new Frame();
                rootFrame.NavigationFailed += OnNavigationFailed;
                window.Content = rootFrame;
            }

            _ = rootFrame.Navigate(typeof(Views.MainPage), e.Arguments);
            window.Activate();
        }

        /// <summary>
        /// Invoked when Navigation to a certain page fails.
        /// </summary>
        void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetDpiForSystem();

        // ── Window Min Size (WM_GETMINMAXINFO) Enforcement ────────────
        private const uint WM_GETMINMAXINFO = 0x0024;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        private delegate nint SubclassProc(IntPtr hWnd, uint uMsg, nuint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, nuint uIdSubclass, nuint dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern nint DefSubclassProc(IntPtr hWnd, uint uMsg, nuint wParam, nint lParam);

        private static SubclassProc? _staticSizeSubclassProc;
        private static int _staticWidthDips = 660;
        private static int _staticHeightDips = 530;

        private static void EnforceStaticWindowSize(IntPtr hWnd, int widthDips, int heightDips)
        {
            _staticWidthDips = widthDips;
            _staticHeightDips = heightDips;

            // Retain reference to delegate to prevent garbage collection
            _staticSizeSubclassProc = (hWndSub, uMsg, wParam, lParam, uIdSubclass, dwRefData) =>
            {
                if (uMsg == WM_GETMINMAXINFO && lParam != 0)
                {
                    var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                    uint dpi = GetDpiForWindow(hWndSub);
                    double scale = dpi > 0 ? (dpi / 96.0) : 1.0;

                    int w = (int)(_staticWidthDips * scale);
                    int h = (int)(_staticHeightDips * scale);

                    mmi.ptMinTrackSize.X = w;
                    mmi.ptMinTrackSize.Y = h;
                    mmi.ptMaxTrackSize.X = w;
                    mmi.ptMaxTrackSize.Y = h;

                    Marshal.StructureToPtr(mmi, lParam, true);
                }
                return DefSubclassProc(hWndSub, uMsg, wParam, lParam);
            };

            SetWindowSubclass(hWnd, _staticSizeSubclassProc, 101, 0);
        }
    }
}

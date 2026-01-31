using System.Runtime.InteropServices;
using Hexa.NET.SDL2;
using YodaStoriesNG.Engine.Game;

namespace YodaStoriesNG.Engine.UI;

/// <summary>
/// Native Windows menu bar implementation.
/// Uses Win32 API for proper DPI/scaling support.
/// </summary>
public unsafe class NativeMenuBar
{
    // Win32 API imports
    [DllImport("user32.dll")]
    private static extern IntPtr CreateMenu();

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, [MarshalAs(UnmanagedType.LPWStr)] string lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool SetMenu(IntPtr hWnd, IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool DrawMenuBar(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private const int GWLP_WNDPROC = -4;
    private const uint WM_COMMAND = 0x0111;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private WndProcDelegate? _wndProcDelegate;
    private IntPtr _originalWndProc;

    // Menu flags
    private const uint MF_STRING = 0x00000000;
    private const uint MF_POPUP = 0x00000010;
    private const uint MF_SEPARATOR = 0x00000800;

    // Menu command IDs
    private const int ID_FILE_NEW_SMALL = 1001;
    private const int ID_FILE_NEW_MEDIUM = 1002;
    private const int ID_FILE_NEW_LARGE = 1003;
    private const int ID_FILE_NEW_XLARGE = 1004;
    private const int ID_FILE_SAVE = 1005;
    private const int ID_FILE_SAVE_AS = 1006;
    private const int ID_FILE_LOAD = 1007;
    private const int ID_FILE_EXIT = 1008;

    private const int ID_DEBUG_ASSET_VIEWER = 2001;
    private const int ID_DEBUG_SCRIPT_EDITOR = 2002;
    private const int ID_DEBUG_MAP_VIEWER = 2003;
    private const int ID_DEBUG_ENABLE_BOT = 2004;
    private const int ID_DEBUG_DISABLE_BOT = 2005;

    private const int ID_CONFIG_SCALE_1X = 3001;
    private const int ID_CONFIG_SCALE_2X = 3002;
    private const int ID_CONFIG_SCALE_4X = 3003;
    private const int ID_CONFIG_KEYBOARD = 3004;
    private const int ID_CONFIG_CONTROLLER = 3005;
    private const int ID_CONFIG_SELECT_DATA = 3006;

    private const int ID_ABOUT_ABOUT = 4001;
    private const int ID_ABOUT_HIGHSCORES = 4002;

    private IntPtr _hMenu;
    private IntPtr _hWnd;

    // Events (same as custom MenuBar)
    public event Action<WorldSize>? OnNewGame;
    public event Action? OnSaveGame;
    public event Action? OnSaveGameAs;
    public event Action? OnLoadGame;
    public event Action? OnExit;
    public event Action? OnAssetViewer;
    public event Action? OnScriptEditor;
    public event Action? OnMapViewer;
    public event Action? OnEnableBot;
    public event Action? OnDisableBot;
    public event Action<int>? OnSetScale;
    public event Action? OnShowKeyboardControls;
    public event Action? OnShowControllerControls;
    public event Action? OnSelectDataFile;
    public event Action? OnShowAbout;
    public event Action? OnShowHighScores;

    public bool IsMenuOpen => false; // Native menus handle this themselves

    public NativeMenuBar()
    {
    }

    /// <summary>
    /// Initialize the native menu bar for the given SDL window.
    /// </summary>
    public void Initialize(SDLWindow* window)
    {
        // Get the native window handle from SDL
        SDLSysWMInfo wmInfo = default;
        SDL.GetVersion(&wmInfo.Version);

        if (SDL.GetWindowWMInfo(window, &wmInfo) == SDLBool.False)
        {
            Console.WriteLine("Failed to get window WM info for native menu");
            return;
        }

        _hWnd = wmInfo.Info.Win.Window;

        if (_hWnd == IntPtr.Zero)
        {
            Console.WriteLine("Failed to get HWND for native menu");
            return;
        }

        CreateMenuBar();
    }

    private void CreateMenuBar()
    {
        _hMenu = CreateMenu();

        // File menu
        var fileMenu = CreatePopupMenu();
        AppendMenuW(fileMenu, MF_STRING, (UIntPtr)ID_FILE_NEW_SMALL, "New Game: Small");
        AppendMenuW(fileMenu, MF_STRING, (UIntPtr)ID_FILE_NEW_MEDIUM, "New Game: Medium");
        AppendMenuW(fileMenu, MF_STRING, (UIntPtr)ID_FILE_NEW_LARGE, "New Game: Large");
        AppendMenuW(fileMenu, MF_STRING, (UIntPtr)ID_FILE_NEW_XLARGE, "New Game: X-tra Large");
        AppendMenuW(fileMenu, MF_SEPARATOR, UIntPtr.Zero, "");
        AppendMenuW(fileMenu, MF_STRING, (UIntPtr)ID_FILE_SAVE, "Save Game");
        AppendMenuW(fileMenu, MF_STRING, (UIntPtr)ID_FILE_SAVE_AS, "Save As...");
        AppendMenuW(fileMenu, MF_STRING, (UIntPtr)ID_FILE_LOAD, "Load Game");
        AppendMenuW(fileMenu, MF_SEPARATOR, UIntPtr.Zero, "");
        AppendMenuW(fileMenu, MF_STRING, (UIntPtr)ID_FILE_EXIT, "Exit");
        AppendMenuW(_hMenu, MF_POPUP, (UIntPtr)fileMenu, "File");

        // Debug menu
        var debugMenu = CreatePopupMenu();
        AppendMenuW(debugMenu, MF_STRING, (UIntPtr)ID_DEBUG_ASSET_VIEWER, "Asset Viewer (F2)");
        AppendMenuW(debugMenu, MF_STRING, (UIntPtr)ID_DEBUG_SCRIPT_EDITOR, "Script Editor (F3)");
        AppendMenuW(debugMenu, MF_STRING, (UIntPtr)ID_DEBUG_MAP_VIEWER, "Map Viewer (F4)");
        AppendMenuW(debugMenu, MF_SEPARATOR, UIntPtr.Zero, "");
        AppendMenuW(debugMenu, MF_STRING, (UIntPtr)ID_DEBUG_ENABLE_BOT, "Enable Bot");
        AppendMenuW(debugMenu, MF_STRING, (UIntPtr)ID_DEBUG_DISABLE_BOT, "Disable Bot");
        AppendMenuW(_hMenu, MF_POPUP, (UIntPtr)debugMenu, "Debug");

        // Config menu
        var configMenu = CreatePopupMenu();
        AppendMenuW(configMenu, MF_STRING, (UIntPtr)ID_CONFIG_SCALE_1X, "Graphics: 1x Scale (F5)");
        AppendMenuW(configMenu, MF_STRING, (UIntPtr)ID_CONFIG_SCALE_2X, "Graphics: 2x Scale (F6)");
        AppendMenuW(configMenu, MF_STRING, (UIntPtr)ID_CONFIG_SCALE_4X, "Graphics: 4x Scale (F7)");
        AppendMenuW(configMenu, MF_SEPARATOR, UIntPtr.Zero, "");
        AppendMenuW(configMenu, MF_STRING, (UIntPtr)ID_CONFIG_KEYBOARD, "Keyboard Controls");
        AppendMenuW(configMenu, MF_STRING, (UIntPtr)ID_CONFIG_CONTROLLER, "Controller Controls");
        AppendMenuW(configMenu, MF_SEPARATOR, UIntPtr.Zero, "");
        AppendMenuW(configMenu, MF_STRING, (UIntPtr)ID_CONFIG_SELECT_DATA, "Select Data File...");
        AppendMenuW(_hMenu, MF_POPUP, (UIntPtr)configMenu, "Config");

        // About menu
        var aboutMenu = CreatePopupMenu();
        AppendMenuW(aboutMenu, MF_STRING, (UIntPtr)ID_ABOUT_ABOUT, "About Desktop Adventures NG");
        AppendMenuW(aboutMenu, MF_STRING, (UIntPtr)ID_ABOUT_HIGHSCORES, "High Scores");
        AppendMenuW(_hMenu, MF_POPUP, (UIntPtr)aboutMenu, "About");

        // Attach menu to window
        SetMenu(_hWnd, _hMenu);
        DrawMenuBar(_hWnd);

        // Subclass the window to intercept WM_COMMAND
        _wndProcDelegate = new WndProcDelegate(WndProc);
        _originalWndProc = SetWindowLongPtr(_hWnd, GWLP_WNDPROC,
            Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));

        Console.WriteLine("Native menu bar created successfully");
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_COMMAND)
        {
            int commandId = (int)(wParam.ToInt64() & 0xFFFF);
            if (HandleMenuCommand(commandId))
            {
                return IntPtr.Zero; // Message handled
            }
        }
        return CallWindowProc(_originalWndProc, hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// Handle SDL events - native menus handle events via WndProc hook.
    /// </summary>
    public bool HandleEvent(SDLEvent* evt)
    {
        // Native menus are handled via Windows message hook, not SDL events
        return false;
    }

    private bool HandleMenuCommand(int commandId)
    {
        switch (commandId)
        {
            // File menu
            case ID_FILE_NEW_SMALL:
                OnNewGame?.Invoke(WorldSize.Small);
                return true;
            case ID_FILE_NEW_MEDIUM:
                OnNewGame?.Invoke(WorldSize.Medium);
                return true;
            case ID_FILE_NEW_LARGE:
                OnNewGame?.Invoke(WorldSize.Large);
                return true;
            case ID_FILE_NEW_XLARGE:
                OnNewGame?.Invoke(WorldSize.XtraLarge);
                return true;
            case ID_FILE_SAVE:
                OnSaveGame?.Invoke();
                return true;
            case ID_FILE_SAVE_AS:
                OnSaveGameAs?.Invoke();
                return true;
            case ID_FILE_LOAD:
                OnLoadGame?.Invoke();
                return true;
            case ID_FILE_EXIT:
                OnExit?.Invoke();
                return true;

            // Debug menu
            case ID_DEBUG_ASSET_VIEWER:
                OnAssetViewer?.Invoke();
                return true;
            case ID_DEBUG_SCRIPT_EDITOR:
                OnScriptEditor?.Invoke();
                return true;
            case ID_DEBUG_MAP_VIEWER:
                OnMapViewer?.Invoke();
                return true;
            case ID_DEBUG_ENABLE_BOT:
                OnEnableBot?.Invoke();
                return true;
            case ID_DEBUG_DISABLE_BOT:
                OnDisableBot?.Invoke();
                return true;

            // Config menu
            case ID_CONFIG_SCALE_1X:
                OnSetScale?.Invoke(1);
                return true;
            case ID_CONFIG_SCALE_2X:
                OnSetScale?.Invoke(2);
                return true;
            case ID_CONFIG_SCALE_4X:
                OnSetScale?.Invoke(4);
                return true;
            case ID_CONFIG_KEYBOARD:
                OnShowKeyboardControls?.Invoke();
                return true;
            case ID_CONFIG_CONTROLLER:
                OnShowControllerControls?.Invoke();
                return true;
            case ID_CONFIG_SELECT_DATA:
                OnSelectDataFile?.Invoke();
                return true;

            // About menu
            case ID_ABOUT_ABOUT:
                OnShowAbout?.Invoke();
                return true;
            case ID_ABOUT_HIGHSCORES:
                OnShowHighScores?.Invoke();
                return true;
        }
        return false;
    }

    public void Render()
    {
        // Native menus don't need manual rendering
    }

    public void Close()
    {
        // Native menus don't need close handling
    }

    public void Dispose()
    {
        // Restore original WndProc
        if (_hWnd != IntPtr.Zero && _originalWndProc != IntPtr.Zero)
        {
            SetWindowLongPtr(_hWnd, GWLP_WNDPROC, _originalWndProc);
            _originalWndProc = IntPtr.Zero;
        }

        if (_hMenu != IntPtr.Zero)
        {
            DestroyMenu(_hMenu);
            _hMenu = IntPtr.Zero;
        }
    }
}

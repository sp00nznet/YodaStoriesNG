using System.Runtime.InteropServices;

namespace YodaStoriesNG.Engine.UI;

/// <summary>
/// Helper class for Windows file dialogs using native APIs.
/// </summary>
public static class FileDialogHelper
{
    // Windows API constants
    private const int OFN_FILEMUSTEXIST = 0x1000;
    private const int OFN_PATHMUSTEXIST = 0x800;
    private const int OFN_OVERWRITEPROMPT = 0x2;
    private const int OFN_NOCHANGEDIR = 0x8;
    private const int MAX_PATH = 260;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENFILENAME
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string lpstrFilter;
        public string lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public string lpstrFileTitle;
        public int nMaxFileTitle;
        public string lpstrInitialDir;
        public string lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public string lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetOpenFileName(ref OPENFILENAME lpofn);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetSaveFileName(ref OPENFILENAME lpofn);

    /// <summary>
    /// Shows an Open File dialog and returns the selected file path, or null if cancelled.
    /// </summary>
    /// <param name="title">Dialog title</param>
    /// <param name="filter">Filter string (e.g., "Save Files (*.ysng)|*.ysng|All Files (*.*)|*.*")</param>
    /// <param name="initialDir">Initial directory</param>
    /// <param name="defaultExt">Default extension (without dot)</param>
    public static string? ShowOpenDialog(string title, string filter, string? initialDir = null, string? defaultExt = null)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Console.WriteLine("File dialogs only supported on Windows. Using console input...");
            Console.Write($"{title}: ");
            return Console.ReadLine();
        }

        // Convert filter format from "Display|*.ext" to "Display\0*.ext\0"
        var nativeFilter = filter.Replace("|", "\0") + "\0\0";

        var fileBuffer = new char[MAX_PATH * 2];
        var fileHandle = GCHandle.Alloc(fileBuffer, GCHandleType.Pinned);

        try
        {
            var ofn = new OPENFILENAME
            {
                lStructSize = Marshal.SizeOf<OPENFILENAME>(),
                hwndOwner = IntPtr.Zero,
                lpstrFilter = nativeFilter,
                nFilterIndex = 1,
                lpstrFile = fileHandle.AddrOfPinnedObject(),
                nMaxFile = MAX_PATH * 2,
                lpstrTitle = title,
                lpstrInitialDir = initialDir ?? "",
                lpstrDefExt = defaultExt ?? "",
                Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR
            };

            if (GetOpenFileName(ref ofn))
            {
                return new string(fileBuffer).TrimEnd('\0');
            }

            return null;
        }
        finally
        {
            fileHandle.Free();
        }
    }

    /// <summary>
    /// Shows a Save File dialog and returns the selected file path, or null if cancelled.
    /// </summary>
    /// <param name="title">Dialog title</param>
    /// <param name="filter">Filter string (e.g., "Save Files (*.ysng)|*.ysng|All Files (*.*)|*.*")</param>
    /// <param name="initialDir">Initial directory</param>
    /// <param name="defaultExt">Default extension (without dot)</param>
    /// <param name="defaultFileName">Default file name</param>
    public static string? ShowSaveDialog(string title, string filter, string? initialDir = null, string? defaultExt = null, string? defaultFileName = null)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Console.WriteLine("File dialogs only supported on Windows. Using console input...");
            Console.Write($"{title}: ");
            return Console.ReadLine();
        }

        // Convert filter format from "Display|*.ext" to "Display\0*.ext\0"
        var nativeFilter = filter.Replace("|", "\0") + "\0\0";

        var fileBuffer = new char[MAX_PATH * 2];

        // Pre-fill with default filename if provided
        if (!string.IsNullOrEmpty(defaultFileName))
        {
            defaultFileName.CopyTo(0, fileBuffer, 0, Math.Min(defaultFileName.Length, MAX_PATH - 1));
        }

        var fileHandle = GCHandle.Alloc(fileBuffer, GCHandleType.Pinned);

        try
        {
            var ofn = new OPENFILENAME
            {
                lStructSize = Marshal.SizeOf<OPENFILENAME>(),
                hwndOwner = IntPtr.Zero,
                lpstrFilter = nativeFilter,
                nFilterIndex = 1,
                lpstrFile = fileHandle.AddrOfPinnedObject(),
                nMaxFile = MAX_PATH * 2,
                lpstrTitle = title,
                lpstrInitialDir = initialDir ?? "",
                lpstrDefExt = defaultExt ?? "",
                Flags = OFN_OVERWRITEPROMPT | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR
            };

            if (GetSaveFileName(ref ofn))
            {
                return new string(fileBuffer).TrimEnd('\0');
            }

            return null;
        }
        finally
        {
            fileHandle.Free();
        }
    }

    /// <summary>
    /// Shows an Open File dialog for Desktop Adventures game data files (.dta or .daw).
    /// </summary>
    public static string? ShowOpenDataFileDialog(string? initialDir = null)
    {
        return ShowOpenDialog(
            "Select Game Data File",
            "Desktop Adventures Data (*.dta;*.daw)|*.dta;*.daw|Yoda Stories (*.dta)|*.dta|Indiana Jones (*.daw)|*.daw|All Files (*.*)|*.*",
            initialDir,
            "dta"
        );
    }

    /// <summary>
    /// Shows an Open File dialog for save game files.
    /// </summary>
    public static string? ShowOpenSaveGameDialog()
    {
        var saveDir = Game.SaveGameManager.GetSaveDirectory();
        return ShowOpenDialog(
            "Load Game",
            "Yoda Stories NG Save (*.ysng)|*.ysng|All Files (*.*)|*.*",
            saveDir,
            "ysng"
        );
    }

    /// <summary>
    /// Shows a Save File dialog for save game files.
    /// </summary>
    public static string? ShowSaveSaveGameDialog(string? defaultName = null)
    {
        var saveDir = Game.SaveGameManager.GetSaveDirectory();
        return ShowSaveDialog(
            "Save Game As",
            "Yoda Stories NG Save (*.ysng)|*.ysng|All Files (*.*)|*.*",
            saveDir,
            "ysng",
            defaultName ?? "savegame.ysng"
        );
    }
}

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ValoReplay.Import
{
    /// <summary>
    /// Native "open file" dialog for .vrp replays: EditorUtility in the editor, comdlg32 in Windows players.
    /// Returns null when cancelled or when the platform has no dialog (type the path instead).
    /// </summary>
    public static class FileDialog
    {
        public static bool IsSupported =>
#if UNITY_EDITOR || UNITY_STANDALONE_WIN
            true;
#else
            false;
#endif

        public static string OpenVrp(string initialDir)
        {
            if (string.IsNullOrEmpty(initialDir) || !Directory.Exists(initialDir)) initialDir = "";
#if UNITY_EDITOR
            string path = UnityEditor.EditorUtility.OpenFilePanelWithFilters("Select a replay", initialDir,
                new[] { "Replay (.vrp)", "vrp", "All files", "*" });
            return string.IsNullOrEmpty(path) ? null : Path.GetFullPath(path);
#elif UNITY_STANDALONE_WIN
            return Win32Open("Select a replay", initialDir, "Replay (*.vrp)\0*.vrp\0All files (*.*)\0*.*\0\0");
#else
            return null;
#endif
        }

#if !UNITY_EDITOR && UNITY_STANDALONE_WIN
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OpenFileName
        {
            public int lStructSize;
            public IntPtr hwndOwner, hInstance;
            public string lpstrFilter;
            public IntPtr lpstrCustomFilter;
            public int nMaxCustFilter, nFilterIndex;
            public IntPtr lpstrFile;
            public int nMaxFile;
            public IntPtr lpstrFileTitle;
            public int nMaxFileTitle;
            public string lpstrInitialDir, lpstrTitle;
            public int Flags;
            public short nFileOffset, nFileExtension;
            public string lpstrDefExt;
            public IntPtr lCustData, lpfnHook;
            public string lpTemplateName;
            public IntPtr pvReserved;
            public int dwReserved, FlagsEx;
        }

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetOpenFileNameW(ref OpenFileName ofn);

        [DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();

        private const int OFN_NOCHANGEDIR = 0x8, OFN_PATHMUSTEXIST = 0x800, OFN_FILEMUSTEXIST = 0x1000, OFN_EXPLORER = 0x80000;
        private const int MaxPath = 4096;

        private static string Win32Open(string title, string initialDir, string filter)
        {
            IntPtr buffer = Marshal.AllocHGlobal(MaxPath * 2);
            try
            {
                Marshal.WriteInt16(buffer, 0);
                var ofn = new OpenFileName
                {
                    lStructSize = Marshal.SizeOf<OpenFileName>(),
                    hwndOwner = GetActiveWindow(),
                    lpstrFilter = filter,
                    nFilterIndex = 1,
                    lpstrFile = buffer,
                    nMaxFile = MaxPath,
                    lpstrInitialDir = initialDir,
                    lpstrTitle = title,
                    Flags = OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR,
                };
                return GetOpenFileNameW(ref ofn) ? Marshal.PtrToStringUni(buffer) : null;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
#endif
    }
}

using System.Runtime.InteropServices;
using System.Text;

namespace NovelGm.Desktop.Api;

/// <summary>Native folder picker (no WinForms reference).</summary>
internal static class FolderPicker
{
    public static string? Pick(string title, System.Windows.Window? owner = null)
    {
        var bi = new BROWSEINFO
        {
            hwndOwner = owner is null ? IntPtr.Zero : new System.Windows.Interop.WindowInteropHelper(owner).Handle,
            lpszTitle = title,
            ulFlags = BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE,
        };

        var pidl = SHBrowseForFolder(ref bi);
        if (pidl == IntPtr.Zero)
            return null;

        try
        {
            var sb = new StringBuilder(260);
            if (!SHGetPathFromIDList(pidl, sb))
                return null;
            var path = sb.ToString();
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }
    }

    private const uint BIF_RETURNONLYFSDIRS = 0x0001;
    private const uint BIF_NEWDIALOGSTYLE = 0x0040;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BROWSEINFO
    {
        public IntPtr hwndOwner;
        public IntPtr pidlRoot;
        public IntPtr pszDisplayName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszTitle;
        public uint ulFlags;
        public IntPtr lpfn;
        public IntPtr lParam;
        public int iImage;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHBrowseForFolder(ref BROWSEINFO lpbi);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SHGetPathFromIDList(IntPtr pidl, StringBuilder pszPath);
}

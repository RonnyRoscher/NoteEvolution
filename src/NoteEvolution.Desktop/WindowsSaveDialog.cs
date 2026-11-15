using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NoteEvolution.Desktop;

/// <summary>
/// The system's "Save as" dialog on Windows (comdlg32). Photino's own save dialog takes only a default path and
/// returns at once, without opening, when that path names a file that does not exist yet — which a suggested file
/// name always does.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsSaveDialog
{
    private const int MaxPath = 32768;
    private const int OverwritePrompt = 0x2;
    private const int HideReadOnly = 0x4;
    private const int NoChangeDir = 0x8;
    private const int PathMustExist = 0x800;
    private const int Explorer = 0x80000;

    /// <summary>
    /// Shows the dialog owned by <paramref name="owner"/>, opened in <paramref name="folder"/> with
    /// <paramref name="suggestedName"/> filled in; <c>null</c> if the user cancels. <paramref name="extension"/>
    /// (".pdf", or empty) is the only file type offered and is added to a name typed without one.
    /// </summary>
    public static string? Show(IntPtr owner, string title, string folder, string suggestedName, string extension)
    {
        var bare = extension.TrimStart('.');
        // "PDF\0*.pdf\0\0": pairs of label and pattern, ended by an empty string.
        var filter = bare.Length == 0 ? "*\0*.*\0\0" : $"{bare.ToUpperInvariant()}\0*.{bare}\0\0";
        var file = Marshal.AllocHGlobal(MaxPath * sizeof(char));
        var filterPtr = Marshal.StringToHGlobalUni(filter);
        try
        {
            var name = suggestedName.Length < MaxPath ? suggestedName : "";
            Marshal.Copy((name + '\0').ToCharArray(), 0, file, name.Length + 1);
            var dialog = new OpenFileName
            {
                StructSize = Marshal.SizeOf<OpenFileName>(),
                Owner = owner,
                Filter = filterPtr,
                FilterIndex = 1,
                File = file,
                MaxFile = MaxPath,
                InitialDir = folder,
                Title = title,
                Flags = OverwritePrompt | HideReadOnly | NoChangeDir | PathMustExist | Explorer,
                DefaultExtension = bare.Length == 0 ? null : bare,
            };
            return GetSaveFileNameW(ref dialog) ? Marshal.PtrToStringUni(file) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(filterPtr);
            Marshal.FreeHGlobal(file);
        }
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetSaveFileNameW(ref OpenFileName dialog);

    /// <summary>OPENFILENAMEW.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int StructSize;
        public IntPtr Owner;
        public IntPtr Instance;
        public IntPtr Filter;
        public IntPtr CustomFilter;
        public int MaxCustomFilter;
        public int FilterIndex;
        public IntPtr File;
        public int MaxFile;
        public IntPtr FileTitle;
        public int MaxFileTitle;
        [MarshalAs(UnmanagedType.LPWStr)] public string? InitialDir;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Title;
        public int Flags;
        public short FileOffset;
        public short FileExtension;
        [MarshalAs(UnmanagedType.LPWStr)] public string? DefaultExtension;
        public IntPtr CustomData;
        public IntPtr Hook;
        public IntPtr TemplateName;
        public IntPtr Reserved;
        public int ReservedFlags;
        public int FlagsEx;
    }
}

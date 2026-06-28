using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace LocalMcp.Agent.Windows.AppLaunch;

internal static class ShellLinkResolver
{
    [SupportedOSPlatform("windows")]
    public static string? TryResolveTarget(string shortcutPath)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        IShellLinkW? shellLink = null;
        try
        {
            var shellLinkType = Type.GetTypeFromCLSID(
                new Guid("00021401-0000-0000-C000-000000000046"),
                throwOnError: true)!;
            shellLink = (IShellLinkW)Activator.CreateInstance(shellLinkType)!;
            ((IPersistFile)shellLink).Load(shortcutPath, 0);
            var target = new StringBuilder(32_768);
            var result = shellLink.GetPath(
                target,
                target.Capacity,
                out _,
                0);
            if (result != 0 || target.Length == 0)
                return null;

            return Environment.ExpandEnvironmentVariables(target.ToString());
        }
        catch
        {
            return null;
        }
        finally
        {
            if (shellLink is not null && Marshal.IsComObject(shellLink))
                Marshal.FinalReleaseComObject(shellLink);
        }
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        [PreserveSig]
        int GetPath(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file,
            int maxPath,
            out Win32FindData findData,
            uint flags);

        void GetIdList(out IntPtr itemIdList);
        void SetIdList(IntPtr itemIdList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCommand(out int showCommand);
        void SetShowCommand(int showCommand);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int iconPathLength, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr windowHandle, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassId(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        void GetCurrentFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindData
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint Reserved0;
        public uint Reserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string FileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string AlternateFileName;
    }
}

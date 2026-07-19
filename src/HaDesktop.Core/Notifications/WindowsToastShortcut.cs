using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace HaDesktop.Core.Notifications;

/// <summary>
/// Creates (once) a Start Menu shortcut carrying an AppUserModelID matching the identifier
/// WindowsNativeNotifier passes to CreateToastNotifier. Without a shortcut like this
/// registering that identity, Windows has nowhere to look up an icon for the toast and falls
/// back to a generic one — this exists purely to fix that, not for toast activation/COM
/// wiring (button clicks are still handled via the "hadesktop-notify-action:" protocol).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsToastShortcut
{
    // Must match the string WindowsNativeNotifier passes to CreateToastNotifier.
    public const string AppId = "HA Desktop";

    private static readonly Guid ShellLinkClsid = new("00021401-0000-0000-C000-000000000046");
    private static readonly PROPERTYKEY AppUserModelIdKey = new() { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D91FA9E0DF"), pid = 5 };

    public static void EnsureRegistered()
    {
        var exePath = Environment.ProcessPath;
        if (exePath is null) return;

        var shortcutPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            "HA Desktop.lnk");

        // Only touch disk if the shortcut is missing or stale (app moved/updated) — not on
        // every single notification.
        if (File.Exists(shortcutPath) && TargetMatches(shortcutPath, exePath))
            return;

        try
        {
            CreateShortcutWithAppId(shortcutPath, exePath);
        }
        catch
        {
            // best effort — worst case the toast keeps showing a generic icon
        }
    }

    private static void CreateShortcutWithAppId(string shortcutPath, string exePath)
    {
        var shellLinkObj = Activator.CreateInstance(Type.GetTypeFromCLSID(ShellLinkClsid)!)!;
        try
        {
            var shellLink = (IShellLinkW)shellLinkObj;
            shellLink.SetPath(exePath);
            shellLink.SetIconLocation(exePath, 0);

            var propertyStore = (IPropertyStore)shellLinkObj;
            var key = AppUserModelIdKey;
            var propVariant = PropVariant.FromString(AppId);
            try
            {
                propertyStore.SetValue(ref key, ref propVariant);
                propertyStore.Commit();
            }
            finally
            {
                propVariant.Clear();
            }

            ((IPersistFile)shellLinkObj).Save(shortcutPath, true);
        }
        finally
        {
            Marshal.ReleaseComObject(shellLinkObj);
        }
    }

    private static bool TargetMatches(string shortcutPath, string exePath)
    {
        try
        {
            var shellLinkObj = Activator.CreateInstance(Type.GetTypeFromCLSID(ShellLinkClsid)!)!;
            try
            {
                ((IPersistFile)shellLinkObj).Load(shortcutPath, 0);
                var buffer = new StringBuilder(260);
                ((IShellLinkW)shellLinkObj).GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0);
                return string.Equals(buffer.ToString(), exePath, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                Marshal.ReleaseComObject(shellLinkObj);
            }
        }
        catch
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public int pid;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointerValue;

        public static PropVariant FromString(string value) => new()
        {
            vt = 31, // VT_LPWSTR
            pointerValue = Marshal.StringToCoTaskMemUni(value),
        };

        public void Clear() => PropVariantClear(ref this);

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant pvar);
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, int dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PROPERTYKEY pkey);
        void GetValue(ref PROPERTYKEY key, out PropVariant pv);
        void SetValue(ref PROPERTYKEY key, ref PropVariant pv);
        void Commit();
    }
}

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using HaDesktop.Core.Diagnostics;
using HaDesktop.Core.Interop;

namespace HaDesktop.Core.Notifications;

/// <summary>
/// Creates (once) a Start Menu shortcut carrying an AppUserModelID matching the identifier
/// WindowsNativeNotifier passes to CreateToastNotifier. Without a shortcut like this
/// registering that identity, Windows has nowhere to look up an icon for the toast and falls
/// back to a generic one — this exists purely to fix that, not for toast activation/COM
/// wiring (button clicks are still handled via the "hadesktop-notify-action:" protocol).
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsToastShortcut
{
    // Must match the string WindowsNativeNotifier passes to CreateToastNotifier.
    public const string AppId = "HA Desktop";

    private static readonly Guid ShellLinkClsid = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid ShellLinkId = new("000214F9-0000-0000-C000-000000000046");
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
        catch (Exception ex)
        {
            Log.Swallowed(ex);
            // best effort — worst case the toast keeps showing a generic icon
        }
    }

    private static void CreateShortcutWithAppId(string shortcutPath, string exePath)
    {
        var shellLink = ComActivation.Create<IShellLinkW>(ShellLinkClsid, ShellLinkId);
        try
        {
            Marshal.ThrowExceptionForHR(shellLink.SetPath(exePath));
            Marshal.ThrowExceptionForHR(shellLink.SetIconLocation(exePath, 0));

            // The one underlying object implements all three interfaces; each cast is a QueryInterface.
            var propertyStore = (IPropertyStore)shellLink;
            var key = AppUserModelIdKey;
            var propVariant = PropVariant.FromString(AppId);
            try
            {
                Marshal.ThrowExceptionForHR(propertyStore.SetValue(in key, in propVariant));
                Marshal.ThrowExceptionForHR(propertyStore.Commit());
            }
            finally
            {
                propVariant.Clear();
            }

            Marshal.ThrowExceptionForHR(((IPersistFile)shellLink).Save(shortcutPath, 1));
        }
        finally
        {
            ComActivation.Release(shellLink);
        }
    }

    private static bool TargetMatches(string shortcutPath, string exePath)
    {
        const int maxPath = 260;
        var buffer = IntPtr.Zero;
        IShellLinkW? shellLink = null;
        try
        {
            shellLink = ComActivation.Create<IShellLinkW>(ShellLinkClsid, ShellLinkId);
            Marshal.ThrowExceptionForHR(((IPersistFile)shellLink).Load(shortcutPath, 0));

            buffer = Marshal.AllocCoTaskMem(maxPath * sizeof(char));
            Marshal.ThrowExceptionForHR(shellLink.GetPath(buffer, maxPath, IntPtr.Zero, 0));
            return string.Equals(Marshal.PtrToStringUni(buffer), exePath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Swallowed(ex);
            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeCoTaskMem(buffer);
            ComActivation.Release(shellLink);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROPERTYKEY
    {
        public Guid fmtid;
        public int pid;
    }

    // A PROPVARIANT is 24 bytes on 64-bit (16 on 32-bit); only the type tag and a pointer-sized payload are used.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct PropVariant
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

    // Every method keeps its real HRESULT return ([PreserveSig]); methods this app never calls are
    // declared (with placeholder signatures) only to keep the ones it does call at the right vtable slot.

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16), Guid("000214F9-0000-0000-C000-000000000046")]
    internal partial interface IShellLinkW
    {
        [PreserveSig] int GetPath(IntPtr pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
        [PreserveSig] int GetIDList(out IntPtr ppidl);
        [PreserveSig] int SetIDList(IntPtr pidl);
        [PreserveSig] int GetDescription(IntPtr pszName, int cchMaxName);
        [PreserveSig] int SetDescription(string pszName);
        [PreserveSig] int GetWorkingDirectory(IntPtr pszDir, int cchMaxPath);
        [PreserveSig] int SetWorkingDirectory(string pszDir);
        [PreserveSig] int GetArguments(IntPtr pszArgs, int cchMaxPath);
        [PreserveSig] int SetArguments(string pszArgs);
        [PreserveSig] int GetHotkey(out short pwHotkey);
        [PreserveSig] int SetHotkey(short wHotkey);
        [PreserveSig] int GetShowCmd(out int piShowCmd);
        [PreserveSig] int SetShowCmd(int iShowCmd);
        [PreserveSig] int GetIconLocation(IntPtr pszIconPath, int cchIconPath, out int piIcon);
        [PreserveSig] int SetIconLocation(string pszIconPath, int iIcon);
        [PreserveSig] int SetRelativePath(string pszPathRel, uint dwReserved);
        [PreserveSig] int Resolve(IntPtr hwnd, uint fFlags);
        [PreserveSig] int SetPath(string pszFile);
    }

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16), Guid("0000010b-0000-0000-C000-000000000046")]
    internal partial interface IPersistFile
    {
        [PreserveSig] int GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        [PreserveSig] int Load(string pszFileName, int dwMode);
        [PreserveSig] int Save(string pszFileName, int fRemember);
        [PreserveSig] int SaveCompleted(string pszFileName);
        [PreserveSig] int GetCurFile(out IntPtr ppszFileName);
    }

    [GeneratedComInterface, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    internal partial interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PROPERTYKEY pkey);
        [PreserveSig] int GetValue(in PROPERTYKEY key, out PropVariant pv);
        [PreserveSig] int SetValue(in PROPERTYKEY key, in PropVariant pv);
        [PreserveSig] int Commit();
    }
}

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace AiUsage.Services;

/// <summary>
/// Writes the desktop .lnk for <c>--create-desktop-shortcut</c> via a hand-declared, minimal
/// <see cref="IShellLinkW"/> plus the framework's own <see cref="IPersistFile"/> (already shipped
/// in <c>System.Runtime.InteropServices.ComTypes</c>, so only the shell interface needed writing
/// here). The project carries no COM interop library, and one shortcut does not earn it one.
/// </summary>
public static class DesktopShortcutService
{
    private const string FileName = "AI-Usage.lnk";
    private const string SwitchName = "--create-desktop-shortcut";
    private static readonly Guid ShellLinkClsid = new("00021401-0000-0000-C000-000000000046");

    /// <summary>Pure: true when the startup args carry the switch, matched case-insensitively
    /// like every other startup switch in this app.</summary>
    public static bool IsRequested(IEnumerable<string> args) =>
        args.Contains(SwitchName, StringComparer.OrdinalIgnoreCase);

    /// <summary>Pure: where the shortcut is written, so a test can check it without creating one,
    /// and <see cref="Create"/> has exactly one place that decides it.</summary>
    public static string ResolveTargetPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), FileName);

    /// <summary>False when creating the shortcut failed for any reason (locked-down desktop
    /// folder, COM unavailable) - never throws, so a failed shortcut never takes the whole app
    /// down with it.</summary>
    public static bool Create(string exePath)
    {
        object? link = null;
        try
        {
            link = Activator.CreateInstance(Type.GetTypeFromCLSID(ShellLinkClsid)!);
            var shellLink = (IShellLinkW)link!;
            shellLink.SetDescription("AI-Usage");
            shellLink.SetPath(exePath);
            ((IPersistFile)shellLink).Save(ResolveTargetPath(), false);
            return true;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
        finally
        {
            if (link is not null)
                Marshal.ReleaseComObject(link);
        }
    }

    /// <summary>The CLSID for <c>ShellLink</c>, activated through <see cref="Type.GetTypeFromCLSID"/>
    /// rather than a generated interop assembly - the project has no COM reference to add one to.</summary>
    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        // Only SetDescription and SetPath are ever called. COM interop resolves a method by its
        // position in the vtable, so every method the real IShellLinkW declares before them stays
        // here too, as a placeholder with an arbitrary signature - skipping one would silently
        // route a call meant for SetDescription or SetPath into the wrong native method instead.
        void GetPath(IntPtr pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription(IntPtr pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory(IntPtr pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments(IntPtr pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation(IntPtr pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}

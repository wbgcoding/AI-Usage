using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using AiUsage.Storage;
using Microsoft.Win32;

namespace AiUsage.Services;

/// <summary>Builds the toast XML. Kept apart from the Windows calls so the escaping and the button
/// can be checked without a notification platform.</summary>
internal static class ToastXml
{
    /// <summary>The activation argument of both the toast body and its button.</summary>
    internal const string ShowArgument = "show";

    internal static string Build(string title, string text, string buttonLabel) =>
        "<toast launch=\"" + ShowArgument + "\" activationType=\"foreground\">"
        + "<visual><binding template=\"ToastGeneric\">"
        + "<text>" + Escape(title) + "</text><text>" + Escape(text) + "</text>"
        + "</binding></visual>"
        + "<actions><action content=\"" + Escape(buttonLabel) + "\" arguments=\"" + ShowArgument + "\" activationType=\"foreground\"/></actions>"
        + "</toast>";

    // Provider and account names are typed by the user: markup characters are escaped, and control
    // characters XML cannot carry at all are dropped.
    private static string Escape(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c >= ' ' || c is '\t' or '\n' or '\r')
                builder.Append(c);
        }

        return SecurityElement.Escape(builder.ToString()) ?? "";
    }
}

/// <summary>The Windows side of a toast: registration of the app identity and the actual show call.</summary>
internal interface IToastBackend
{
    /// <summary>Makes the app identity known to Windows. Called once before the first <see cref="Show"/>.</summary>
    void Register();

    /// <summary>Shows one toast. <paramref name="activated"/> runs, on any thread, when the person clicks the
    /// toast or its button while this process is still running.</summary>
    void Show(string xml, Action activated);
}

/// <summary>
/// Shows alerts as real Windows toasts with a "Show widget" button. Any failure while registering or
/// showing is logged once and from then on <see cref="TryShow"/> answers false, so the caller falls
/// back to the tray balloon for that alert and every later one of this run.
/// </summary>
internal sealed class ToastNotifier(IToastBackend backend, Action<string> log, Func<string> buttonLabel)
{
    private readonly object _gate = new();
    private bool _registered;

    /// <summary>True once a failure switched toasts off for this run.</summary>
    public bool IsDisabled { get; private set; }

    /// <summary>Raised when the person clicks a toast or its button. May arrive on any thread.</summary>
    public event Action? Activated;

    /// <returns>False when no toast was shown and the caller must use the balloon instead.</returns>
    public bool TryShow(string title, string text)
    {
        lock (_gate)
        {
            if (IsDisabled)
                return false;

            try
            {
                if (!_registered)
                {
                    backend.Register();
                    _registered = true;
                }

                backend.Show(ToastXml.Build(title, text, buttonLabel()), OnActivated);
                return true;
            }
            catch (Exception ex)
            {
                IsDisabled = true;
                log($"Windows notifications are not available, using tray balloons instead: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }
    }

    private void OnActivated() => Activated?.Invoke();
}

/// <summary>The app identity Windows files toasts under: one id per user, registered under HKCU for the
/// installed and the portable copy alike, and set as the process identity at start.</summary>
internal static partial class ToastRegistration
{
    internal const string AppUserModelId = "BGCoding.AI-Usage";
    internal const string DisplayName = "AI-Usage";
    internal const string KeyPath = @"Software\Classes\AppUserModelId\" + AppUserModelId;
    private const string IconFileName = "toast-icon.png";

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetCurrentProcessExplicitAppUserModelID(string appId);

    /// <summary>Must run before the first window exists, or the taskbar groups by the old identity.</summary>
    internal static void SetProcessId() => _ = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);

    /// <summary>Writes the id's name and icon below <paramref name="subKeyPath"/> of the current user's hive.
    /// Written again on every run, so a moved portable copy never leaves a stale icon path.</summary>
    internal static void Write(string subKeyPath, string? iconPath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(subKeyPath, writable: true);
        key.SetValue("DisplayName", DisplayName, RegistryValueKind.String);
        if (iconPath is not null)
            key.SetValue("IconUri", iconPath, RegistryValueKind.String);
        else
            key.DeleteValue("IconUri", throwOnMissingValue: false);
    }

    /// <summary>Copies the app mark out of the program into the data folder as a PNG, the only form the
    /// notification platform reads an icon from. Null when that did not work; the toast then has no icon.</summary>
    internal static string? EnsureIconFile(string dataDirectory)
    {
        try
        {
            var info = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/AI-Usage;component/Assets/app-mark.png", UriKind.Absolute));
            if (info is null)
                return null;

            Directory.CreateDirectory(dataDirectory);
            var path = Path.Combine(dataDirectory, IconFileName);
            using var source = info.Stream;
            using var target = File.Create(path);
            source.CopyTo(target);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>
/// Windows.UI.Notifications through hand-written COM calls. The target framework carries no Windows
/// version, so no WinRT projection exists; the few calls needed are made through the interface vtables
/// directly. Every interface id and slot number below comes from the Windows SDK headers.
/// </summary>
internal sealed unsafe partial class WinRtToastBackend : IToastBackend
{
    private static readonly Guid IidToastNotificationManagerStatics = new("50ac103f-d235-4598-bbef-98fe4d1a3ad4");
    private static readonly Guid IidToastNotificationFactory = new("04124b20-82c6-4229-b109-fd9ed4662b53");
    private static readonly Guid IidXmlDocumentIO = new("6cd0e74e-ee65-4489-9ebf-ca43e87ba637");
    private static readonly Guid IidXmlDocument = new("f7f3a506-1e87-42d6-bcfb-b8c809fa5494");
    private static readonly Guid IidAgileObject = new("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90");

    // ITypedEventHandler<ToastNotification, IInspectable>, derived from its parameterized signature.
    internal static readonly Guid IidActivatedHandler = new("ab54de2d-97d9-5528-b6ad-105afe156530");

    // Toasts already shown stay referenced for a while: the platform raises the click event on the
    // notification object, which must still be alive then.
    private const int KeptToasts = 16;
    private readonly Queue<nint> _shown = new();

    public void Register()
    {
        var icon = ToastRegistration.EnsureIconFile(AppPaths.DataDirectory);
        ToastRegistration.Write(ToastRegistration.KeyPath, icon);
    }

    public void Show(string xml, Action activated)
    {
        nint document = 0, documentIo = 0, documentIface = 0, statics = 0, notifier = 0, factory = 0, toast = 0, handler = 0;
        try
        {
            document = Activate("Windows.Data.Xml.Dom.XmlDocument");
            documentIo = QueryInterface(document, IidXmlDocumentIO);
            var xmlString = CreateHString(xml);
            try
            {
                Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(documentIo, 6))(documentIo, xmlString));
            }
            finally
            {
                DeleteHString(xmlString);
            }

            documentIface = QueryInterface(document, IidXmlDocument);

            statics = GetFactory("Windows.UI.Notifications.ToastNotificationManager", IidToastNotificationManagerStatics);
            var id = CreateHString(ToastRegistration.AppUserModelId);
            try
            {
                nint result;
                Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(statics, 7))(statics, id, &result));
                notifier = result;
            }
            finally
            {
                DeleteHString(id);
            }

            factory = GetFactory("Windows.UI.Notifications.ToastNotification", IidToastNotificationFactory);
            {
                nint result;
                Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(factory, 6))(factory, documentIface, &result));
                toast = result;
            }

            handler = ActivatedHandler.Create(activated);
            {
                long token;
                Check(((delegate* unmanaged[Stdcall]<nint, nint, long*, int>)Slot(toast, 11))(toast, handler, &token));
            }

            Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(notifier, 6))(notifier, toast));

            _shown.Enqueue(toast);
            toast = 0; // owned by the queue now
            while (_shown.Count > KeptToasts)
                Release(_shown.Dequeue());
        }
        finally
        {
            // The toast holds its own reference to the handler once it is registered.
            Release(handler);
            Release(toast);
            Release(factory);
            Release(notifier);
            Release(statics);
            Release(documentIface);
            Release(documentIo);
            Release(document);
        }
    }

    // ---- raw COM helpers ----

    private static nint Slot(nint instance, int index) => ((nint*)*(nint*)instance)[index];

    private static void Check(int hresult)
    {
        if (hresult < 0)
            Marshal.ThrowExceptionForHR(hresult);
    }

    private static nint QueryInterface(nint instance, Guid iid)
    {
        nint result;
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(instance, 0))(instance, &iid, &result));
        return result;
    }

    internal static void Release(nint instance)
    {
        if (instance != 0)
            _ = ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(instance, 2))(instance);
    }

    private static nint Activate(string className)
    {
        var name = CreateHString(className);
        try
        {
            nint result;
            Check(RoActivateInstance(name, &result));
            return result;
        }
        finally
        {
            DeleteHString(name);
        }
    }

    private static nint GetFactory(string className, Guid iid)
    {
        var name = CreateHString(className);
        try
        {
            nint result;
            Check(RoGetActivationFactory(name, &iid, &result));
            return result;
        }
        finally
        {
            DeleteHString(name);
        }
    }

    private static nint CreateHString(string value)
    {
        nint result;
        fixed (char* chars = value)
            Check(WindowsCreateString(chars, (uint)value.Length, &result));
        return result;
    }

    private static void DeleteHString(nint value) => _ = WindowsDeleteString(value);

    [LibraryImport("combase.dll")]
    private static partial int WindowsCreateString(char* source, uint length, nint* hstring);

    [LibraryImport("combase.dll")]
    private static partial int WindowsDeleteString(nint hstring);

    [LibraryImport("combase.dll")]
    private static partial int RoActivateInstance(nint classId, nint* instance);

    [LibraryImport("combase.dll")]
    private static partial int RoGetActivationFactory(nint classId, Guid* iid, nint* factory);

    /// <summary>A minimal native COM object for the toast click event: IUnknown plus Invoke, answering
    /// the agile-object query so the platform may call it from any thread without marshaling.</summary>
    private static class ActivatedHandler
    {
        private struct Instance
        {
            public nint* Vtable;
            public int References;
            public nint State; // GCHandle to the managed callback
        }

        private static nint* _vtable;

        internal static nint Create(Action activated)
        {
            if (_vtable == null)
            {
                var table = (nint*)NativeMemory.Alloc((nuint)(4 * sizeof(nint)));
                table[0] = (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&QueryInterfaceImpl;
                table[1] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&AddRefImpl;
                table[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&ReleaseImpl;
                table[3] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, int>)&InvokeImpl;
                _vtable = table;
            }

            var instance = (Instance*)NativeMemory.Alloc((nuint)sizeof(Instance));
            instance->Vtable = _vtable;
            instance->References = 1;
            instance->State = GCHandle.ToIntPtr(GCHandle.Alloc(activated));
            return (nint)instance;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int QueryInterfaceImpl(nint self, Guid* iid, nint* result)
        {
            if (*iid == IidActivatedHandler || *iid == IidAgileObject || *iid == new Guid("00000000-0000-0000-c000-000000000046"))
            {
                *result = self;
                Interlocked.Increment(ref ((Instance*)self)->References);
                return 0;
            }

            *result = 0;
            return unchecked((int)0x80004002); // E_NOINTERFACE
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint AddRefImpl(nint self) => (uint)Interlocked.Increment(ref ((Instance*)self)->References);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint ReleaseImpl(nint self)
        {
            var instance = (Instance*)self;
            var left = Interlocked.Decrement(ref instance->References);
            if (left == 0)
            {
                GCHandle.FromIntPtr(instance->State).Free();
                NativeMemory.Free(instance);
            }

            return (uint)left;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int InvokeImpl(nint self, nint sender, nint args)
        {
            try
            {
                ((Action?)GCHandle.FromIntPtr(((Instance*)self)->State).Target)?.Invoke();
            }
            catch
            {
                // A click must never take the process down through a native callback.
            }

            return 0;
        }
    }
}

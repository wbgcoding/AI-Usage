using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using AiUsage.Services;
using Microsoft.Win32;
using Xunit;

namespace AiUsage.Tests;

public class ToastNotifierTests
{
    private sealed class FakeBackend : IToastBackend
    {
        public int Registered;
        public List<string> Shown { get; } = [];
        public Exception? FailRegister;
        public Exception? FailShow;
        public Action? LastActivated;

        public void Register()
        {
            Registered++;
            if (FailRegister is not null)
                throw FailRegister;
        }

        public void Show(string xml, Action activated)
        {
            if (FailShow is not null)
                throw FailShow;
            Shown.Add(xml);
            LastActivated = activated;
        }
    }

    [Fact]
    public void TheXmlHasTitleTextAndOneShowButtonAndParses()
    {
        var xml = ToastXml.Build("Codex", "Codex: 5h at 90%", "Show widget");

        var root = XDocument.Parse(xml).Root!;
        Assert.Equal("toast", root.Name.LocalName);
        Assert.Equal("show", root.Attribute("launch")!.Value);
        Assert.Equal(["Codex", "Codex: 5h at 90%"], root.Descendants("text").Select(t => t.Value));
        var button = Assert.Single(root.Descendants("action"));
        Assert.Equal("Show widget", button.Attribute("content")!.Value);
        Assert.Equal("show", button.Attribute("arguments")!.Value);
        Assert.Equal("foreground", button.Attribute("activationType")!.Value);
    }

    [Fact]
    public void MarkupInNamesIsEscapedAndControlCharactersAreDropped()
    {
        var xml = ToastXml.Build("A <b>&\"'", "x\u0001y<z>", "Zeig \"es\"");

        var root = XDocument.Parse(xml).Root!; // must still be well-formed
        Assert.Equal("A <b>&\"'", root.Descendants("text").First().Value);
        Assert.Equal("xy<z>", root.Descendants("text").Last().Value);
        Assert.Equal("Zeig \"es\"", root.Descendants("action").Single().Attribute("content")!.Value);
        Assert.DoesNotContain("<b>", xml);
    }

    [Fact]
    public void AShownToastRegistersOnceAndPassesTheXmlWithTheLocalizedButton()
    {
        var backend = new FakeBackend();
        var notifier = new ToastNotifier(backend, _ => { }, () => "Widget zeigen");

        Assert.True(notifier.TryShow("Codex", "one"));
        Assert.True(notifier.TryShow("Codex", "two"));

        Assert.Equal(1, backend.Registered);
        Assert.Equal(2, backend.Shown.Count);
        Assert.Contains("Widget zeigen", backend.Shown[0]);
        Assert.False(notifier.IsDisabled);
    }

    [Fact]
    public void AFailingShowLogsOnceAndSwitchesToTheBalloonForThatAndLaterAlerts()
    {
        var backend = new FakeBackend { FailShow = new InvalidOperationException("no platform") };
        var log = new List<string>();
        var notifier = new ToastNotifier(backend, log.Add, () => "Show widget");

        Assert.False(notifier.TryShow("Codex", "one"));
        backend.FailShow = null; // even a platform that recovers is not asked again this run
        Assert.False(notifier.TryShow("Codex", "two"));
        Assert.False(notifier.TryShow("Codex", "three"));

        Assert.True(notifier.IsDisabled);
        Assert.Empty(backend.Shown);
        var line = Assert.Single(log);
        Assert.Contains("no platform", line);
    }

    [Fact]
    public void AFailingRegistrationFallsBackTheSameWay()
    {
        var backend = new FakeBackend { FailRegister = new UnauthorizedAccessException("registry") };
        var log = new List<string>();
        var notifier = new ToastNotifier(backend, log.Add, () => "Show widget");

        Assert.False(notifier.TryShow("Codex", "one"));
        Assert.False(notifier.TryShow("Codex", "two"));

        Assert.Single(log);
        Assert.Empty(backend.Shown);
    }

    [Fact]
    public void AClickOnTheToastRaisesActivated()
    {
        var backend = new FakeBackend();
        var notifier = new ToastNotifier(backend, _ => { }, () => "Show widget");
        var raised = 0;
        notifier.Activated += () => raised++;

        notifier.TryShow("Codex", "one");
        backend.LastActivated!();

        Assert.Equal(1, raised);
    }

    [Fact]
    public void TheIdentityIsRegisteredWithNameAndIconAndTheIconCanBeDropped()
    {
        var subKey = $@"Software\AI-Usage-Tests\{Guid.NewGuid():N}";
        try
        {
            ToastRegistration.Write(subKey, @"C:\data\toast-icon.png");
            using (var key = Registry.CurrentUser.OpenSubKey(subKey)!)
            {
                Assert.Equal("AI-Usage", key.GetValue("DisplayName"));
                Assert.Equal(@"C:\data\toast-icon.png", key.GetValue("IconUri"));
            }

            ToastRegistration.Write(subKey, null);
            using var again = Registry.CurrentUser.OpenSubKey(subKey)!;
            Assert.Null(again.GetValue("IconUri"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\AI-Usage-Tests", throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void TheIdentityNameIsTheDocumentedOne()
    {
        Assert.Equal("BGCoding.AI-Usage", ToastRegistration.AppUserModelId);
        Assert.Equal(@"Software\Classes\AppUserModelId\BGCoding.AI-Usage", ToastRegistration.KeyPath);
    }

    // The click handler's interface id is derived from its generic signature (the WinRT naming rule:
    // a version 5 id over the signature text); computing it here guards the constant against a typo.
    [Fact]
    public void TheClickHandlerInterfaceIdMatchesItsGenericSignature()
    {
        var signature = "pinterface({9de1c534-6ae1-11e0-84e1-18a905bcc53f};"
            + "rc(Windows.UI.Notifications.ToastNotification;{997e2675-059e-4e60-8b06-1760917c8b80});cinterface(IInspectable))";
        var wrt = new Guid("d57af411-737b-c042-abae-878b1e16adee").ToByteArray();
        var data = Encoding.UTF8.GetBytes(signature);
        var hash = SHA1.HashData([.. wrt, .. data]);
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        // The digest is big-endian; Guid wants the first three fields little-endian.
        var expected = new Guid(
            (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3],
            (short)((bytes[4] << 8) | bytes[5]), (short)((bytes[6] << 8) | bytes[7]),
            bytes[8], bytes[9], bytes[10], bytes[11], bytes[12], bytes[13], bytes[14], bytes[15]);

        Assert.Equal(expected, WinRtToastBackend.IidActivatedHandler);
    }
}

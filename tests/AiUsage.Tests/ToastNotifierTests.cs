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
        public Action<string>? LastActivated;

        public void Register()
        {
            Registered++;
            if (FailRegister is not null)
                throw FailRegister;
        }

        public void Show(string xml, Action<string> activated)
        {
            if (FailShow is not null)
                throw FailShow;
            Shown.Add(xml);
            LastActivated = activated;
        }
    }

    private static readonly IReadOnlyList<(string Label, string Argument)> ShowButton = [("Show widget", "show")];

    [Fact]
    public void TheXmlHasTitleTextAndOneShowButtonAndParses()
    {
        var xml = ToastXml.Build("Codex", "Codex: 5h at 90%", ShowButton);

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
        var xml = ToastXml.Build("A <b>&\"'", "x\u0001y<z>", [("Zeig \"es\"", "mute:a&\"b")]);

        var root = XDocument.Parse(xml).Root!; // must still be well-formed
        Assert.Equal("A <b>&\"'", root.Descendants("text").First().Value);
        Assert.Equal("xy<z>", root.Descendants("text").Last().Value);
        var action = root.Descendants("action").Single();
        Assert.Equal("Zeig \"es\"", action.Attribute("content")!.Value);
        Assert.Equal("mute:a&\"b", action.Attribute("arguments")!.Value);
        Assert.DoesNotContain("<b>", xml);
    }

    [Fact]
    public void AShownToastRegistersOnceAndPassesTheXmlWithTheLocalizedButton()
    {
        var backend = new FakeBackend();
        var notifier = new ToastNotifier(backend, _ => { });
        IReadOnlyList<(string Label, string Argument)> buttons = [("Widget zeigen", "show")];

        Assert.True(notifier.TryShow("Codex", "one", buttons));
        Assert.True(notifier.TryShow("Codex", "two", buttons));

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
        var notifier = new ToastNotifier(backend, log.Add);

        Assert.False(notifier.TryShow("Codex", "one", ShowButton));
        backend.FailShow = null; // even a platform that recovers is not asked again this run
        Assert.False(notifier.TryShow("Codex", "two", ShowButton));
        Assert.False(notifier.TryShow("Codex", "three", ShowButton));

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
        var notifier = new ToastNotifier(backend, log.Add);

        Assert.False(notifier.TryShow("Codex", "one", ShowButton));
        Assert.False(notifier.TryShow("Codex", "two", ShowButton));

        Assert.Single(log);
        Assert.Empty(backend.Shown);
    }

    [Fact]
    public void AClickOnTheToastRaisesActivated()
    {
        var backend = new FakeBackend();
        var notifier = new ToastNotifier(backend, _ => { });
        var raised = new List<string>();
        notifier.Activated += raised.Add;

        notifier.TryShow("Codex", "one", ShowButton);
        backend.LastActivated!("signin:claude");

        Assert.Equal(["signin:claude"], raised);
    }

    [Fact]
    public void BuildEmitsOneActionPerButtonAndAtMostThree()
    {
        var xml = ToastXml.Build("T", "x", [("A", "show"), ("B", "signin:k"), ("C", "mute:k"), ("D", "show")]);

        var actions = XDocument.Parse(xml).Root!.Descendants("action").ToList();
        Assert.Equal(["A", "B", "C"], actions.Select(a => a.Attribute("content")!.Value));
        Assert.Equal(["show", "signin:k", "mute:k"], actions.Select(a => a.Attribute("arguments")!.Value));
    }

    [Theory]
    [InlineData("show", "show", "")]
    [InlineData("signin:claude", "signin", "claude")]
    [InlineData("mute:codex-2", "mute", "codex-2")]
    [InlineData("", "show", "")]
    [InlineData(null, "show", "")]
    [InlineData("signin:", "show", "")]
    [InlineData("foo:bar", "show", "")]
    [InlineData(":claude", "show", "")]
    public void TryParseMapsAnythingUnknownToShow(string? argument, string kind, string key)
    {
        ToastXml.TryParse(argument, out var gotKind, out var gotKey);

        Assert.Equal(kind, gotKind);
        Assert.Equal(key, gotKey);
    }

    [Fact]
    public void ArgumentHelpersRoundTripThroughTryParse()
    {
        Assert.True(ToastXml.TryParse(ToastXml.SignInArgument("claude"), out var kind, out var key));
        Assert.Equal(("signin", "claude"), (kind, key));
        Assert.True(ToastXml.TryParse(ToastXml.MuteArgument("codex-2"), out kind, out key));
        Assert.Equal(("mute", "codex-2"), (kind, key));
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

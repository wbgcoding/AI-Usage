using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace AiUsage.Web;

/// <summary>What the provider's own site says about the session open in the sign-in window.</summary>
internal enum SignInSessionState
{
    SignedIn,
    NotSignedIn,

    /// <summary>No clear answer (rate limited, an unknown shape, a timeout): says nothing either way.</summary>
    Unknown,
}

/// <summary>
/// Asks the provider's own site, from inside the sign-in window, whether the session there is really
/// signed in: the same discovery script the hidden reader runs, evaluated on the landing page. The
/// address alone cannot tell a finished sign-in from a login page that bounced to the start page on a
/// stale session, and that left the tile asking for a sign-in again right after the window had
/// closed as "signed in". Only a clear "not signed in" counts against the session; any other answer
/// keeps the address-based verdict.
/// </summary>
internal static class SignInSessionCheck
{
    /// <summary>The verdict in one script answer, the same envelope the hidden reader reads.</summary>
    internal static SignInSessionState Read(string? envelopeJson)
    {
        if (string.IsNullOrWhiteSpace(envelopeJson))
            return SignInSessionState.Unknown;

        try
        {
            using var document = JsonDocument.Parse(envelopeJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String)
                return SignInSessionState.Unknown;

            return status.GetString() switch
            {
                "not_signed_in" => SignInSessionState.NotSignedIn,
                "ok" => SignInSessionState.SignedIn,
                _ => SignInSessionState.Unknown,
            };
        }
        catch (JsonException)
        {
            return SignInSessionState.Unknown;
        }
    }

    /// <summary>Runs <paramref name="script"/> once in <paramref name="core"/>, behind the same origin
    /// guard as the hidden reader, so it never fetches with the session's cookies from a foreign page.
    /// Never throws: a browser that went away or a slow answer reads as <see cref="SignInSessionState.Unknown"/>.</summary>
    internal static async Task<SignInSessionState> RunAsync(CoreWebView2 core, string script, string usageHost, TimeSpan timeout)
    {
        try
        {
            var parameters = JsonSerializer.Serialize(new
            {
                expression = WebView2HiddenBrowserHost.WrapWithOriginGuard(script, usageHost),
                awaitPromise = true,
                returnByValue = true,
            });
            var raw = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", parameters).WaitAsync(timeout);
            return Read(WebView2HiddenBrowserHost.UnwrapEvaluateResult(raw));
        }
        catch (Exception ex) when (ex is TimeoutException or COMException or InvalidOperationException or ObjectDisposedException)
        {
            return SignInSessionState.Unknown;
        }
    }
}

namespace AiUsage.Web;

/// <summary>
/// The provider's site sent the hidden session to a check a person has to answer (Google's
/// unusual-traffic page) instead of loading. The hidden session never loads such a page, so the read
/// reports "not signed in": the tile then offers the sign-in window, where the person can pass the
/// check and the session works again.
/// </summary>
public sealed class WebSessionCheckRequiredException : InvalidOperationException
{
    public WebSessionCheckRequiredException()
        : base("The provider's site asks for a check only a person can answer.")
    {
    }
}

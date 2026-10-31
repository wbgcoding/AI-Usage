namespace AiUsage.Models;

/// <summary>Which sign-in delivered the Claude Console balance on show.</summary>
public enum ClaudeCreditRoute
{
    /// <summary>No balance was read.</summary>
    None,

    /// <summary>The account's own claude.ai session answered for the console as well.</summary>
    ClaudeSignIn,

    /// <summary>The separate session on the console's own site answered.</summary>
    Console,
}

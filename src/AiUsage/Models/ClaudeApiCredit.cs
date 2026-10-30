namespace AiUsage.Models;

/// <summary>
/// The prepaid API credit balance of the Claude Console account behind a Claude sign-in, in major
/// units (dollars, not cents). Billing numbers only, never anything of the account's content.
/// </summary>
public sealed record ClaudeApiCredit(decimal Amount, string Currency);

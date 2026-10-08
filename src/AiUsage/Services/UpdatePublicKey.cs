namespace AiUsage.Services;

/// <summary>The public half of the update signing key (SubjectPublicKeyInfo, Base64). An update is
/// installed only when its detached signature verifies against this key.</summary>
internal static class UpdatePublicKey
{
    public const string Value = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEJlmkxNW9ZkOZq4B+4uBBRRBVmnLdH+ajJ+Qn+3hHSwVKC/eJN0Ikm36gzQkA1mRpgerlOhZTQIauwvN9DRlixQ==";
}

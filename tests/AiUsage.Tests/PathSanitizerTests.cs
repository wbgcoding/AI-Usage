using AiUsage.Services;

namespace AiUsage.Tests;

public class PathSanitizerTests
{
    [Fact]
    public void Sanitize_replaces_the_user_profile_path()
    {
        var text = @"failed to write C:\Users\SomePerson\AppData\Roaming\AI-Usage\settings.json";

        var result = PathSanitizer.Sanitize(text, userProfile: @"C:\Users\SomePerson", userName: "SomePerson");

        Assert.DoesNotContain("SomePerson", result);
        Assert.Contains("<user>", result);
    }

    [Fact]
    public void Sanitize_replaces_the_short_form_of_the_user_profile_path()
    {
        var text = @"cannot open C:\Users\SOMEPE~1\AppData\Local\Temp\AI-Usage\update and MAXIMI~1 alone";

        var result = PathSanitizer.Sanitize(
            text, userProfile: @"C:\Users\SomePerson", userName: "SomePerson", shortProfile: @"C:\Users\SOMEPE~1");

        Assert.Equal(@"cannot open <user>\AppData\Local\Temp\AI-Usage\update and MAXIMI~1 alone", result);
    }

    [Fact]
    public void Sanitize_replaces_the_account_name_wherever_it_appears_on_its_own()
    {
        var text = "logged in as SomePerson via token xyz";

        var result = PathSanitizer.Sanitize(text, userProfile: @"C:\Users\SomePerson", userName: "SomePerson");

        Assert.DoesNotContain("SomePerson", result);
    }

    [Fact]
    public void Sanitize_keeps_words_that_merely_contain_the_user_name()
    {
        var result = PathSanitizer.Sanitize(@"Maximum C:\Users\Max\x", userProfile: @"C:\Users\Max", userName: "Max");

        Assert.Equal(@"Maximum <user>\x", result);
    }

    [Fact]
    public void Sanitize_replaces_the_user_name_as_a_whole_path_segment()
    {
        var result = PathSanitizer.Sanitize(@"D:\Backup\Max\notes and /srv/max/data", userProfile: @"C:\Users\Max", userName: "Max");

        Assert.Equal(@"D:\Backup\<user>\notes and /srv/<user>/data", result);
    }

    [Fact]
    public void Sanitize_leaves_unrelated_text_untouched()
    {
        var text = "System.IO.IOException: disk full";

        var result = PathSanitizer.Sanitize(text, userProfile: @"C:\Users\SomePerson", userName: "SomePerson");

        Assert.Equal(text, result);
    }

    [Fact]
    public void Sanitize_masks_a_profile_folder_that_differs_from_the_account_name_in_every_spelling()
    {
        var profile = @"C:\Users\MaxG";
        var text = @"a C:/Users/MaxG/x b C:\Users\MaxG\y c C:\Users\MaxG\z d MaxG";

        var result = PathSanitizer.Sanitize(text, userProfile: profile, userName: "Max");

        Assert.DoesNotContain("MaxG", result);
        Assert.Equal(@"a <user>/x b <user>\y c <user>\z d <user>", result);
    }

    [Fact]
    public void Sanitize_masks_an_email_address()
    {
        var result = PathSanitizer.Sanitize("sign-in for jane.doe+ai@example.co.uk failed", userProfile: @"C:\Users\SomePerson", userName: "SomePerson");

        Assert.Equal("sign-in for {email} failed", result);
    }

    [Fact]
    public void Sanitize_masks_an_email_address_that_starts_with_the_account_name()
    {
        var result = PathSanitizer.Sanitize("mail someperson@example.com sent", userProfile: @"C:\Users\SomePerson", userName: "SomePerson");

        Assert.Equal("mail {email} sent", result);
    }

    [Fact]
    public void Sanitize_leaves_an_at_sign_without_an_address_alone()
    {
        var text = "user @ host, build 1.2@3 and a@b";

        Assert.Equal(text, PathSanitizer.Sanitize(text, userProfile: @"C:\Users\SomePerson", userName: "SomePerson"));
    }

    [Fact]
    public void Sanitize_masks_a_bearer_token()
    {
        var result = PathSanitizer.Sanitize("Authorization: Bearer abc.DEF-123_xyz~+/== rejected", userProfile: @"C:\Users\SomePerson", userName: "SomePerson");

        Assert.Equal("Authorization: Bearer {token} rejected", result);
    }

    [Fact]
    public void Sanitize_masks_an_access_token_field_plain_and_json_escaped()
    {
        var plain = PathSanitizer.Sanitize(@"{""access_token"":""sk-123abc"",""expires_in"":3600}", userProfile: @"C:\Users\SomePerson", userName: "SomePerson");
        var escaped = PathSanitizer.Sanitize(@"body {\""access_token\"": \""sk-123abc\"", \""scope\"": \""x\""}", userProfile: @"C:\Users\SomePerson", userName: "SomePerson");

        Assert.Equal(@"{""access_token"":""{token}"",""expires_in"":3600}", plain);
        Assert.Equal(@"body {\""access_token\"": \""{token}\"", \""scope\"": \""x\""}", escaped);
    }

    [Fact]
    public void Sanitize_masks_a_token_value_with_escapes_and_the_refresh_and_id_tokens()
    {
        var text = PathSanitizer.Sanitize(
            @"{""access_token"":""\u0041bc\/def"",""refresh_token"":""r-1"",""id_token"":""i\/2""}",
            userProfile: @"C:\Users\SomePerson", userName: "SomePerson");
        var nested = PathSanitizer.Sanitize(
            @"{\""access_token\"":\""ab\\/cd\"",\""x\"":1}",
            userProfile: @"C:\Users\SomePerson", userName: "SomePerson");

        Assert.Equal(@"{""access_token"":""{token}"",""refresh_token"":""{token}"",""id_token"":""{token}""}", text);
        Assert.Equal(@"{\""access_token\"":\""{token}\"",\""x\"":1}", nested);
    }

    [Theory]
    [InlineData(@"{""accessToken"":""abc.def"",""refreshToken"":""r-1""}", @"{""accessToken"":""{token}"",""refreshToken"":""{token}""}")]
    [InlineData(@"{""sessionKey"":""abc""}", @"{""sessionKey"":""{token}""}")]
    [InlineData("key sk-ant-oat01-AbCdEf123456_x rejected", "key {token} rejected")]
    [InlineData("google ya29.a0AfB_byC1234567 and 1//0gAbCdEfGh12", "google {token} and {token}")]
    [InlineData("Cookie: sessionKey=sk-ant-sid01-AbCdEf1234; lastActiveOrg=x", "Cookie: sessionKey={token}; lastActiveOrg=x")]
    [InlineData("https://example.com/a//b and 1//2", "https://example.com/a//b and 1//2")]
    public void Sanitize_masks_camel_case_token_fields_and_prefixed_tokens(string input, string expected)
    {
        Assert.Equal(expected, PathSanitizer.Sanitize(input, userProfile: @"C:\Users\SomePerson", userName: "SomePerson"));
    }

    [Fact]
    public void Sanitize_masks_the_real_user_name_and_profile_without_injected_values()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var result = PathSanitizer.Sanitize($@"cannot open {profile}\AppData\x");

        Assert.Equal(@"cannot open <user>\AppData\x", result);
        Assert.Equal(result, PathSanitizer.Sanitize($@"cannot open {profile}\AppData\x"));
    }

    [Fact]
    public void Sanitize_masks_github_openai_and_jwt_tokens()
    {
        var ghp = "gh" + "p_" + new string('a', 20);
        var gho = "gh" + "o_" + new string('b', 20);
        var pat = "github" + "_pat_" + new string('c', 22);
        var openAi = "sk" + "-" + new string('d', 24);
        var project = "sk" + "-proj-" + new string('e', 24);
        var jwt = "ey" + "Jabcdefgh" + "." + "ey" + "Jijklmnop" + "." + "sig12345678";

        var result = PathSanitizer.Sanitize(
            $"a {ghp} b {gho} c {pat} d {openAi} e {project} f {jwt} g",
            userProfile: @"C:\Users\SomePerson", userName: "SomePerson");

        Assert.Equal("a {token} b {token} c {token} d {token} e {token} f {token} g", result);
    }

    [Theory]
    [InlineData(@"{""authorization"":""x y""}", @"{""authorization"":""{token}""}")]
    [InlineData(@"{""Cookie"":""a=b""}", @"{""Cookie"":""{token}""}")]
    [InlineData(@"{""api_key"":""k1"",""client_secret"":""s1"",""apiKey"":""k2""}", @"{""api_key"":""{token}"",""client_secret"":""{token}"",""apiKey"":""{token}""}")]
    [InlineData(@"{""token"":""t1"",""secret"":""s2""}", @"{""token"":""{token}"",""secret"":""{token}""}")]
    [InlineData("the token is missing and authorization failed, ask-me-about-risk-assessment-things", "the token is missing and authorization failed, ask-me-about-risk-assessment-things")]
    public void Sanitize_masks_wider_credential_keys_and_leaves_prose(string input, string expected)
    {
        Assert.Equal(expected, PathSanitizer.Sanitize(input, userProfile: @"C:\Users\SomePerson", userName: "SomePerson"));
    }

    [Theory]
    // A JWE (five parts, the encrypted key part may be empty) and a JWT with an empty signature.
    [InlineData("cookie eyJhbGciOiJkaXIiLCJlbmMiOiJBMjU2R0NNIn0..Ab12Cd34Ef56Gh78.Zm9vYmFyYmF6cXV4.dGFnMTIzNDU2 end", "cookie {token} end")]
    [InlineData("jwe eyJhbGciOiJSU0EtT0FFUCJ9.a1B2c3D4e5F6.Ab12Cd34Ef56Gh78.Zm9vYmFyYmF6cXV4.dGFnMTIzNDU2 end", "jwe {token} end")]
    [InlineData("unsigned eyJhbGciOiJub25lIn0.eyJzdWIiOiIxMjM0NTY3ODkwIn0. end", "unsigned {token} end")]
    // An opaque bearer value, also with characters outside the token68 alphabet.
    [InlineData("Authorization: Bearer 7c1d9e0a%2Fb3:f5e2 rejected", "Authorization: Bearer {token} rejected")]
    [InlineData("header Bearer a1b2c3d4e5f6a7b8c9d0e1f2 sent", "header Bearer {token} sent")]
    // Form-encoded values.
    [InlineData("body refresh_token=1%2F%2Fabc123def456&client_id=app", "body refresh_token={token}&client_id=app")]
    [InlineData("grant access_token=abc.def&expires_in=3600", "grant access_token={token}&expires_in=3600")]
    [InlineData("login user=ann&password=hunter2&next=%2Fhome", "login user=ann&password={token}&next=%2Fhome")]
    [InlineData("GET /v1/usage?api_key=abcd1234efgh&format=json", "GET /v1/usage?api_key={token}&format=json")]
    public void Sanitize_masks_jwe_opaque_bearer_and_form_encoded_credentials(string input, string expected)
    {
        Assert.Equal(expected, PathSanitizer.Sanitize(input, userProfile: @"C:\Users\SomePerson", userName: "SomePerson"));
    }

    [Theory]
    [InlineData("The password is required and the token was refreshed.")]
    [InlineData("GET https://example.com/api/key/rotate returned 404")]
    [InlineData("password= missing, token= empty, api_key=")]
    [InlineData("see eyJ in the docs and version 1.2.3")]
    public void Sanitize_leaves_a_plain_sentence_and_a_url_path_alone(string input)
    {
        Assert.Equal(input, PathSanitizer.Sanitize(input, userProfile: @"C:\Users\SomePerson", userName: "SomePerson"));
    }
}

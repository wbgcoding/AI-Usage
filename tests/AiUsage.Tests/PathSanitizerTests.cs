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

    [Fact]
    public void Sanitize_masks_the_real_user_name_and_profile_without_injected_values()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var result = PathSanitizer.Sanitize($@"cannot open {profile}\AppData\x");

        Assert.Equal(@"cannot open <user>\AppData\x", result);
        Assert.Equal(result, PathSanitizer.Sanitize($@"cannot open {profile}\AppData\x"));
    }
}

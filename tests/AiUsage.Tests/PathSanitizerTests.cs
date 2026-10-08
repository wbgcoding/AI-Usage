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
}

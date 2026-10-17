using AiUsage.Stats;

namespace AiUsage.Tests;

public class ProjectLabelsTests
{
    [Theory]
    [InlineData(@"E:\Claude\AI-Usage", "AI-Usage")]
    [InlineData(@"E:\Claude\AI-Usage\", "AI-Usage")]
    [InlineData("/srv/work/tool/", "tool")]
    [InlineData("plain", "plain")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData("/", "/")]
    public void The_name_is_the_last_folder_or_the_whole_text_when_there_is_none(string path, string expected) =>
        Assert.Equal(expected, ProjectLabels.Name(path));

    [Fact]
    public void Distinct_names_stay_plain()
    {
        var labels = ProjectLabels.For([@"E:\Claude\AI-Usage", @"E:\Claude\Other"]);
        Assert.Equal("AI-Usage", labels[@"E:\Claude\AI-Usage"]);
        Assert.Equal("Other", labels[@"E:\Claude\Other"]);
    }

    [Fact]
    public void A_shared_folder_name_gets_its_parent_folder_appended()
    {
        var labels = ProjectLabels.For([@"E:\Claude\AI-Usage", @"D:\Old\ai-usage\", @"E:\Claude\Other"]);
        Assert.Equal("AI-Usage (Claude)", labels[@"E:\Claude\AI-Usage"]);
        Assert.Equal("ai-usage (Old)", labels[@"D:\Old\ai-usage\"]);
        Assert.Equal("Other", labels[@"E:\Claude\Other"]);
    }

    [Fact]
    public void A_shared_name_directly_below_a_drive_uses_the_drive_as_parent()
    {
        var labels = ProjectLabels.For([@"C:\Tool", @"D:\Tool"]);
        Assert.Equal("Tool (C:)", labels[@"C:\Tool"]);
        Assert.Equal("Tool (D:)", labels[@"D:\Tool"]);
    }

    [Fact]
    public void The_same_path_twice_is_one_project_not_a_duplicate()
    {
        var labels = ProjectLabels.For([@"E:\Claude\AI-Usage", @"e:\claude\ai-usage"]);
        Assert.Equal("AI-Usage", labels[@"E:\Claude\AI-Usage"]);
    }
}

using AiUsage.Storage;

namespace AiUsage.Tests;

[Collection(SharedStateTestsCollection.Name)]
public class SecondInstancePointerTests
{
    [Fact]
    public void The_second_instance_keeps_its_own_pointer_file_and_the_everyday_one_stays_put()
    {
        var root = TestPaths.CreateDirectory("pointer");
        AppPaths.SetPointerRootOverride(root);
        try
        {
            var everyday = AppPaths.LocationPointerFile;
            Assert.Equal(Path.Combine(root, "AI-Usage", "location.txt"), everyday);

            AppPaths.UseSecondInstancePointer();

            var own = AppPaths.LocationPointerFile;
            Assert.NotEqual(everyday, own);
            Assert.Equal(Path.Combine(root, "AI-Usage", "second-instance", "location.txt"), own);

            var target = Path.Combine(root, "moved");
            Directory.CreateDirectory(Path.GetDirectoryName(own)!);
            File.WriteAllText(own, target);
            Assert.Equal(target, AppPaths.ReadPointedDirectory(Path.Combine(root, "fake-temp")));
            Assert.False(File.Exists(everyday));
        }
        finally
        {
            AppPaths.ClearPointerRootOverrideForTests();
        }
    }

    [Fact]
    public void An_unreachable_second_instance_pointer_is_reported_like_the_everyday_one()
    {
        var root = TestPaths.CreateDirectory("pointer-unreachable");
        var blocker = Path.Combine(root, "blocker");
        File.WriteAllText(blocker, "a file where the pointed folder would have to go");
        var target = Path.Combine(blocker, "data");
        AppPaths.SetPointerRootOverride(root);
        try
        {
            AppPaths.UseSecondInstancePointer();
            Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.LocationPointerFile)!);
            File.WriteAllText(AppPaths.LocationPointerFile, target);

            var pointed = AppPaths.ReadPointedDirectory(Path.Combine(root, "fake-temp"), out var unreachable);

            Assert.Null(pointed);
            Assert.Equal(target, unreachable);

            var fallback = Path.Combine(root, "AI-Usage", "second-instance");
            AppPaths.SetStartRedirect(fallback, unreachable);
            Assert.Equal(fallback, AppPaths.DataDirectory);
            Assert.Equal(target, AppPaths.UnreachableDataFolder);

            AppPaths.SetOverride(Path.Combine(root, "moved"));
            Assert.Null(AppPaths.UnreachableDataFolder);
        }
        finally
        {
            AppPaths.ClearOverrideForTests();
            AppPaths.ClearPointerRootOverrideForTests();
        }
    }
}

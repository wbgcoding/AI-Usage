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
}

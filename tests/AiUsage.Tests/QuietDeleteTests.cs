using System.Text;
using AiUsage.Io;

namespace AiUsage.Tests;

/// <summary>Best-effort file deletion and the one app encoding.</summary>
public class QuietDeleteTests
{
    [Fact]
    public void An_existing_file_is_removed()
    {
        using var directory = TestPaths.CreateDisposableDirectory("quiet-delete");
        var path = Path.Combine(directory.Path, "a.txt");
        File.WriteAllText(path, "x");

        QuietDelete.Delete(path);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Several_paths_are_all_removed_and_a_missing_one_does_not_stop_the_rest()
    {
        using var directory = TestPaths.CreateDisposableDirectory("quiet-delete");
        var first = Path.Combine(directory.Path, "1.txt");
        var missing = Path.Combine(directory.Path, "none.txt");
        var last = Path.Combine(directory.Path, "3.txt");
        File.WriteAllText(first, "x");
        File.WriteAllText(last, "x");

        QuietDelete.Delete(first, missing, last);

        Assert.False(File.Exists(first));
        Assert.False(File.Exists(last));
    }

    [Fact]
    public void No_paths_and_a_missing_folder_do_nothing()
    {
        QuietDelete.Delete();
        QuietDelete.Delete(Path.Combine(TestPaths.Root, "no-such-folder", "file.txt"));
    }

    [Fact]
    public void A_file_that_is_still_open_stays_and_does_not_throw()
    {
        using var directory = TestPaths.CreateDisposableDirectory("quiet-delete");
        var path = Path.Combine(directory.Path, "busy.txt");
        using (new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            QuietDelete.Delete(path);
            Assert.True(File.Exists(path));
        }

        QuietDelete.Delete(path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void A_read_only_file_stays_and_does_not_throw()
    {
        using var directory = TestPaths.CreateDisposableDirectory("quiet-delete");
        var path = Path.Combine(directory.Path, "locked.txt");
        File.WriteAllText(path, "x");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            QuietDelete.Delete(path);
            Assert.True(File.Exists(path));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public void A_directory_path_does_not_throw_and_the_directory_stays()
    {
        using var directory = TestPaths.CreateDisposableDirectory("quiet-delete");

        QuietDelete.Delete(directory.Path);

        Assert.True(Directory.Exists(directory.Path));
    }

    [Fact]
    public void The_app_encoding_is_utf8_without_a_byte_order_mark()
    {
        Assert.Empty(AppEncoding.Utf8NoBom.GetPreamble());
        Assert.Equal(65001, AppEncoding.Utf8NoBom.CodePage);
        Assert.Equal([0x41], AppEncoding.Utf8NoBom.GetBytes("A"));
    }

    [Fact]
    public void Text_round_trips_through_the_app_encoding_including_non_ascii()
    {
        const string text = "Grüße 中文 \U0001F600";

        var bytes = AppEncoding.Utf8NoBom.GetBytes(text);

        Assert.Equal(text, AppEncoding.Utf8NoBom.GetString(bytes));
        Assert.NotEqual(0xEF, bytes[0]);
    }

    [Fact]
    public void Writing_a_file_with_the_app_encoding_leaves_no_byte_order_mark()
    {
        using var directory = TestPaths.CreateDisposableDirectory("app-encoding");
        var path = Path.Combine(directory.Path, "out.txt");

        File.WriteAllText(path, "abc", AppEncoding.Utf8NoBom);

        Assert.Equal("abc"u8.ToArray(), File.ReadAllBytes(path));
    }

    [Fact]
    public void An_invalid_sequence_decodes_to_the_replacement_character()
    {
        Assert.Equal("�", AppEncoding.Utf8NoBom.GetString([0xFF]));
    }

    [Fact]
    public void The_app_encoding_is_not_the_bom_writing_default()
    {
        Assert.NotEqual(new UTF8Encoding(true).GetPreamble().Length, AppEncoding.Utf8NoBom.GetPreamble().Length);
    }
}

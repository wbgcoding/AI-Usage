using System.Text.Json;
using AiUsage.Providers.Parsing;
using Xunit;

namespace AiUsage.Tests;

public class JsonReadingTests
{
    public static TheoryData<string> NotObjects => new() { "[1,2]", "\"text\"", "12", "true", "null" };

    [Theory]
    [MemberData(nameof(NotObjects))]
    public void Every_helper_answers_null_for_a_parent_that_is_not_an_object(string json)
    {
        using var document = JsonDocument.Parse(json);
        var parent = document.RootElement;

        Assert.Null(JsonReading.TryGetObject(parent, "a"));
        Assert.Null(JsonReading.TryGetString(parent, "a"));
        Assert.Null(JsonReading.TryGetDouble(parent, "a"));
        Assert.Null(JsonReading.TryGetInt32(parent, "a"));
        Assert.Null(JsonReading.TryGetInt64(parent, "a"));
    }

    [Fact]
    public void An_object_parent_still_reads_its_properties()
    {
        using var document = JsonDocument.Parse("{\"s\":\"x\",\"d\":1.5,\"i\":2,\"l\":3,\"o\":{}}");
        var parent = document.RootElement;

        Assert.Equal("x", JsonReading.TryGetString(parent, "s"));
        Assert.Equal(1.5, JsonReading.TryGetDouble(parent, "d"));
        Assert.Equal(2, JsonReading.TryGetInt32(parent, "i"));
        Assert.Equal(3L, JsonReading.TryGetInt64(parent, "l"));
        Assert.NotNull(JsonReading.TryGetObject(parent, "o"));
    }
}

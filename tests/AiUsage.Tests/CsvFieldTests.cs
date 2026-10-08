using AiUsage.Services;

namespace AiUsage.Tests;

public class CsvFieldTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "")]
    [InlineData("a\"b", "\"a\"\"b\"")]
    [InlineData("x,y", "\"x,y\"")]
    [InlineData("line1\nline2", "\"line1\nline2\"")]
    [InlineData("line1\r\nline2", "\"line1\r\nline2\"")]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+1", "'+1")]
    [InlineData("-1", "'-1")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("\tx", "'\tx")]
    [InlineData("=a,b", "\"'=a,b\"")]
    [InlineData("a=b", "a=b")]
    public void Escape_follows_rfc_4180_and_guards_formulas(string input, string expected)
    {
        Assert.Equal(expected, CsvField.Escape(input));
    }
}

using System.Text;

namespace AiUsage.Io;

/// <summary>The one text encoding every file this app writes or decodes uses: UTF-8 without a byte
/// order mark.</summary>
public static class AppEncoding
{
    public static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
}

using System.Text.Json.Serialization;

namespace AiUsage.Models;

/// <summary>The two windows shown in the UI, plus a catch-all for anything a provider adds.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WindowKind>))]
public enum WindowKind
{
    FiveHour,
    Weekly,
    Other,
}

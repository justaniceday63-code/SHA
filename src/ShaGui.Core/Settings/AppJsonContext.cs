using System.Text.Json.Serialization;

namespace ShaGui.Core.Settings;

/// <summary>
/// Source-generated JSON contract for <see cref="AppSettings"/>. The trimmed
/// portable build compiles reflection-based serialization out, so Save/Load
/// must go through this context or they throw at runtime ("Reflection-based
/// serialization has been disabled"), which cascades into a null database
/// and an unavailable History page.
/// </summary>
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppJsonContext : JsonSerializerContext
{
}

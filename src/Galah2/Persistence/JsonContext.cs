using System.Text.Json.Serialization;
using Galah2.Models;

namespace Galah2.Persistence;

/// <summary>
/// Source-generated JSON metadata for every document Galah persists.
///
/// Reflection-based <c>JsonSerializer</c> throws <c>JsonSerializerIsReflectionDisabled</c> under
/// NativeAOT, and the failure happens on the very first <c>Load()</c> — i.e. before the app has a
/// window, so it looks like an instant crash with no log line. Registering the types here is what
/// makes the AOT build boot at all.
///
/// Rule for future models: any type reachable from a persisted document must be listed in
/// <see cref="JsonContext.Default"/>'s tree, or the AOT build will throw at runtime while the
/// JIT build keeps working. That asymmetry is why the AOT publish must be smoke-tested, not just
/// compiled.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    UseStringEnumConverter = false)]
[JsonSerializable(typeof(CatalogDocument))]
[JsonSerializable(typeof(SettingsDocument))]
[JsonSerializable(typeof(UiStateDocument))]
[JsonSerializable(typeof(Galah2.Services.MigrationBundle))]
internal sealed partial class JsonContext : JsonSerializerContext
{
}

using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MeetUp.Api.Data;

/// <summary>
/// Serialises a collection property to a jsonb column explicitly.
///
/// Npgsql will not write arbitrary CLR collections to jsonb unless dynamic JSON is switched on
/// globally, and that failure only shows up when a row is actually saved — the model builds and
/// the migration applies happily either way. Converting here keeps the behaviour local to the
/// properties that need it instead of changing serialization for the whole data source.
/// </summary>
internal static class JsonbConverter
{
    private static readonly JsonSerializerOptions Options = new();

    public static ValueConverter<List<T>, string> For<T>() => new(
        list => JsonSerializer.Serialize(list, Options),
        json => JsonSerializer.Deserialize<List<T>>(json, Options) ?? new List<T>());

    /// <summary>
    /// Compares by serialised value so EF notices in-place edits to the list. Without this, mutating
    /// the collection on a tracked entity would not be detected as a change.
    /// </summary>
    public static ValueComparer<List<T>> ComparerFor<T>() => new(
        (left, right) => JsonSerializer.Serialize(left, Options) == JsonSerializer.Serialize(right, Options),
        list => JsonSerializer.Serialize(list, Options).GetHashCode(),
        list => JsonSerializer.Deserialize<List<T>>(JsonSerializer.Serialize(list, Options), Options) ?? new List<T>());
}

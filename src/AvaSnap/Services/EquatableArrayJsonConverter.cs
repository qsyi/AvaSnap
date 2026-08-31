using System.Text.Json;
using System.Text.Json.Serialization;
using AvaSnap.Views;

namespace AvaSnap.Services;

/// <summary><see cref="EquatableArray{T}"/> を素の JSON 配列として読み書きする。
/// プロジェクト保存(<see cref="ProjectService"/>)で MaskOp / MaskStrokePoint など
/// EquatableArray を含むレコードをそのまま直列化するために使う。</summary>
public sealed class EquatableArrayJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType &&
        typeToConvert.GetGenericTypeDefinition() == typeof(EquatableArray<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var elementType = typeToConvert.GetGenericArguments()[0];
        return (JsonConverter)Activator.CreateInstance(
            typeof(EquatableArrayJsonConverter<>).MakeGenericType(elementType))!;
    }
}

public sealed class EquatableArrayJsonConverter<T> : JsonConverter<EquatableArray<T>> where T : IEquatable<T>
{
    public override EquatableArray<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(JsonSerializer.Deserialize<T[]>(ref reader, options) ?? Array.Empty<T>());

    public override void Write(Utf8JsonWriter writer, EquatableArray<T> value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value.AsArray(), options);
}

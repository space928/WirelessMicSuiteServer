using static System.ComponentModel.TypeConverter;
using System.Collections;
using System.ComponentModel.Design.Serialization;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.Json;
using System;

namespace WirelessMicSuiteServer;

public class JsonStringConverter<T> : JsonConverterFactory where T : new()
{
    public JsonStringConverter()
    {

    }

    /// <inheritdoc />
    public sealed override bool CanConvert(Type typeToConvert) => typeToConvert == typeof(T);

    /// <inheritdoc />
    public sealed override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        if (typeToConvert != typeof(T))
        {
            throw new ArgumentOutOfRangeException();
        }

        return new StringConverter<T>();
    }
}

public class StringConverter<T> : JsonConverter<T> where T : new()
{
    private ConstructorInfo strConstructor;

    public StringConverter()
    {
        var str = typeof(T).GetConstructor([typeof(string)]);
        var spn = typeof(T).GetConstructor([typeof(ReadOnlySpan<char>)]);
        if (spn != null)
            strConstructor = spn;
        else if (str != null)
            strConstructor = str;
        else
            throw new ArgumentException("Target type must have a constructor which takes a single string as a parameter!");
    }

    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString();
        if (s == null)
            return default;

        return (T)strConstructor.Invoke([s]);
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value?.ToString());
    }
}

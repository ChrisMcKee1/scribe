using System.Text.Json;

namespace Scribe.StyleEval.Checks;

/// <summary>Flat views over a parsed JSON answer, so the JSON checkers stay readable.</summary>
internal static class JsonWalker
{
    /// <summary>Every string VALUE in the document, in document order. Keys are excluded.</summary>
    public static IEnumerable<string> Strings(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var value = element.GetString();
                if (value is not null)
                {
                    yield return value;
                }

                break;

            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    foreach (var nested in Strings(property.Value))
                    {
                        yield return nested;
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nested in Strings(item))
                    {
                        yield return nested;
                    }
                }

                break;
        }
    }

    /// <summary>Every property name in the document, in document order.</summary>
    public static IEnumerable<string> Keys(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    yield return property.Name;
                    foreach (var nested in Keys(property.Value))
                    {
                        yield return nested;
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nested in Keys(item))
                    {
                        yield return nested;
                    }
                }

                break;
        }
    }

    /// <summary>Every number value, as it was written in the source text.</summary>
    public static IEnumerable<string> NumberTexts(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                yield return element.GetRawText();
                break;

            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    foreach (var nested in NumberTexts(property.Value))
                    {
                        yield return nested;
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nested in NumberTexts(item))
                    {
                        yield return nested;
                    }
                }

                break;
        }
    }

    /// <summary>Every array in the document, outermost first.</summary>
    public static IEnumerable<JsonElement> Arrays(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            yield return element;
            foreach (var item in element.EnumerateArray())
            {
                foreach (var nested in Arrays(item))
                {
                    yield return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                foreach (var nested in Arrays(property.Value))
                {
                    yield return nested;
                }
            }
        }
    }
}

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bap.Companion.Core;

public sealed record WallpaperPropertyReadResult(
    IReadOnlyList<WallpaperPropertyDefinition> Properties,
    IReadOnlyList<string> Diagnostics)
{
    public static WallpaperPropertyReadResult Empty { get; } = new([], []);
}

public static partial class WallpaperPropertyReader
{
    private static readonly HashSet<string> EditableTypes = ["bool", "color", "slider", "combo", "textinput"];

    public static WallpaperPropertyReadResult Read(JsonElement project)
    {
        var result = new List<WallpaperPropertyDefinition>();
        var diagnostics = new List<string>();
        if (!project.TryGetProperty("general", out var general) || general.ValueKind != JsonValueKind.Object ||
            !general.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
            return WallpaperPropertyReadResult.Empty;

        foreach (var entry in properties.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object || !entry.Value.TryGetProperty("type", out var typeNode) || typeNode.ValueKind != JsonValueKind.String)
            {
                diagnostics.Add($"Property '{entry.Name}' has no valid type.");
                continue;
            }

            var type = typeNode.GetString()!;
            if (type is "file" or "directory") continue;
            if (!EditableTypes.Contains(type))
            {
                diagnostics.Add($"Property '{entry.Name}' uses unsupported type '{type}'.");
                continue;
            }
            if (!entry.Value.TryGetProperty("value", out var value) || !IsValidDefault(type, value))
            {
                diagnostics.Add($"Property '{entry.Name}' has an invalid default value.");
                continue;
            }

            var minimum = Number(entry.Value, "min");
            var maximum = Number(entry.Value, "max");
            var step = Number(entry.Value, "step");
            if (type == "slider" && (minimum is null || maximum is null || minimum > maximum || value.GetDouble() < minimum || value.GetDouble() > maximum))
            {
                diagnostics.Add($"Property '{entry.Name}' has invalid slider bounds.");
                continue;
            }

            var options = type == "combo" ? ReadOptions(entry.Value, entry.Name, diagnostics) : [];
            if (type == "combo" && (options.Count == 0 || !options.Any(option => JsonElement.DeepEquals(option.Value, value))))
            {
                diagnostics.Add($"Property '{entry.Name}' has invalid combo options or a default not present in its options.");
                continue;
            }

            var condition = entry.Value.TryGetProperty("condition", out var conditionNode) && conditionNode.ValueKind == JsonValueKind.String
                ? conditionNode.GetString() : null;
            var conditionSupported = string.IsNullOrWhiteSpace(condition) || PropertyCondition.IsSupported(condition);
            if (!conditionSupported) diagnostics.Add($"Property '{entry.Name}' uses an unsupported display condition.");
            result.Add(new(entry.Name, type, value.Clone(), String(entry.Value, "text"), minimum, maximum, step,
                Integer(entry.Value, "fraction") ?? Integer(entry.Value, "precision"), options, condition, conditionSupported));
        }

        return new(result.OrderBy(property => Number(properties.GetProperty(property.Key), "order") ?? 0)
            .ThenBy(property => property.Key, StringComparer.Ordinal).ToArray(), diagnostics);
    }

    public static bool IsValidValue(WallpaperPropertyDefinition definition, JsonElement value)
    {
        if (!IsValidDefault(definition.Type, value)) return false;
        return definition.Type switch
        {
            "slider" => value.GetDouble() >= definition.Minimum && value.GetDouble() <= definition.Maximum,
            "combo" => definition.Options?.Any(option => JsonElement.DeepEquals(option.Value, value)) == true,
            _ => true
        };
    }

    public static bool IsConditionMet(WallpaperPropertyDefinition definition, IReadOnlyDictionary<string, JsonElement> values)
    {
        if (string.IsNullOrWhiteSpace(definition.Condition)) return true;
        return definition.ConditionSupported && PropertyCondition.Evaluate(definition.Condition, values);
    }

    private static bool IsValidDefault(string type, JsonElement value) => type switch
    {
        "bool" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "color" => value.ValueKind == JsonValueKind.String && IsNormalizedColor(value.GetString()),
        "slider" => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number),
        "combo" => value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False,
        "textinput" => value.ValueKind == JsonValueKind.String,
        _ => false
    };

    private static bool IsNormalizedColor(string? value)
    {
        if (value is null) return false;
        var channels = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return channels.Length == 3 && channels.All(channel => float.TryParse(channel, CultureInfo.InvariantCulture, out var parsed) && float.IsFinite(parsed) && parsed is >= 0 and <= 1);
    }

    private static List<WallpaperPropertyOption> ReadOptions(JsonElement property, string key, List<string> diagnostics)
    {
        var options = new List<WallpaperPropertyOption>();
        if (!property.TryGetProperty("options", out var node) || node.ValueKind != JsonValueKind.Array) return options;
        foreach (var option in node.EnumerateArray())
        {
            if (option.ValueKind != JsonValueKind.Object || !option.TryGetProperty("value", out var value) ||
                value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))
            {
                diagnostics.Add($"Property '{key}' contains an invalid combo option.");
                return [];
            }
            var text = option.TryGetProperty("text", out var textNode) && textNode.ValueKind == JsonValueKind.String
                ? textNode.GetString()! : value.ToString();
            options.Add(new(value.Clone(), text));
        }
        return options;
    }

    private static string? String(JsonElement node, string name) => node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static double? Number(JsonElement node, string name) => node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;
    private static int? Integer(JsonElement node, string name) => node.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;

    private static partial class PropertyCondition
    {
        [GeneratedRegex(@"\s*(==|!=|&&|\|\||\(|\)|[A-Za-z][A-Za-z0-9_.-]*\.value|true|false|-?[0-9]+(?:\.[0-9]+)?|'(?:[^'\\]|\\.)*'|""(?:[^""\\]|\\.)*"")", RegexOptions.CultureInvariant)]
        private static partial Regex TokenPattern();

        public static bool IsSupported(string expression)
        {
            var tokens = Tokenize(expression);
            if (tokens is null || tokens.Count == 0) return false;
            var index = 0;
            try
            {
                ParseOr(tokens, ref index);
                return index == tokens.Count;
            }
            catch (FormatException) { return false; }
        }

        public static bool Evaluate(string expression, IReadOnlyDictionary<string, JsonElement> values)
        {
            var tokens = Tokenize(expression);
            if (tokens is null || tokens.Count == 0) return false;
            var index = 0;
            try
            {
                var result = ParseOr(tokens, ref index, values);
                return index == tokens.Count && result is bool boolean && boolean;
            }
            catch (FormatException) { return false; }
        }

        private static List<string>? Tokenize(string expression)
        {
            var tokens = new List<string>();
            var position = 0;
            foreach (Match match in TokenPattern().Matches(expression))
            {
                if (match.Index != position) return null;
                tokens.Add(match.Groups[1].Value);
                position += match.Length;
            }
            return position == expression.Length ? tokens : null;
        }

        private static void ParseOr(IReadOnlyList<string> tokens, ref int index)
        {
            ParseAnd(tokens, ref index);
            while (Take(tokens, ref index, "||")) ParseAnd(tokens, ref index);
        }
        private static void ParseAnd(IReadOnlyList<string> tokens, ref int index)
        {
            ParseAtom(tokens, ref index);
            while (Take(tokens, ref index, "&&")) ParseAtom(tokens, ref index);
        }
        private static void ParseAtom(IReadOnlyList<string> tokens, ref int index)
        {
            if (Take(tokens, ref index, "("))
            {
                ParseOr(tokens, ref index);
                if (!Take(tokens, ref index, ")")) throw new FormatException();
                return;
            }
            if (index >= tokens.Count || !IsValue(tokens[index++])) throw new FormatException();
            if (Take(tokens, ref index, "==") || Take(tokens, ref index, "!="))
            {
                if (index >= tokens.Count || !IsValue(tokens[index++])) throw new FormatException();
            }
        }

        private static object? ParseOr(IReadOnlyList<string> tokens, ref int index, IReadOnlyDictionary<string, JsonElement> values)
        {
            var left = ParseAnd(tokens, ref index, values);
            while (Take(tokens, ref index, "||"))
            {
                var right = ParseAnd(tokens, ref index, values);
                left = Truthy(left) || Truthy(right);
            }
            return left;
        }

        private static object? ParseAnd(IReadOnlyList<string> tokens, ref int index, IReadOnlyDictionary<string, JsonElement> values)
        {
            var left = ParseEvaluatedAtom(tokens, ref index, values);
            while (Take(tokens, ref index, "&&"))
            {
                var right = ParseEvaluatedAtom(tokens, ref index, values);
                left = Truthy(left) && Truthy(right);
            }
            return left;
        }

        private static object? ParseEvaluatedAtom(IReadOnlyList<string> tokens, ref int index, IReadOnlyDictionary<string, JsonElement> values)
        {
            if (Take(tokens, ref index, "("))
            {
                var nested = ParseOr(tokens, ref index, values);
                if (!Take(tokens, ref index, ")")) throw new FormatException();
                return nested;
            }
            var left = ParseValue(tokens, ref index, values);
            if (Take(tokens, ref index, "==")) return Equals(left, ParseValue(tokens, ref index, values));
            if (Take(tokens, ref index, "!=")) return !Equals(left, ParseValue(tokens, ref index, values));
            return left;
        }

        private static object? ParseValue(IReadOnlyList<string> tokens, ref int index, IReadOnlyDictionary<string, JsonElement> values)
        {
            if (index >= tokens.Count) throw new FormatException();
            var token = tokens[index++];
            if (token.EndsWith(".value", StringComparison.Ordinal))
                return values.TryGetValue(token[..^6], out var propertyValue) ? Primitive(propertyValue) : null;
            if (token == "true") return true;
            if (token == "false") return false;
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return number;
            if (token.Length >= 2 && token[0] == '\'' && token[^1] == '\'') return token[1..^1].Replace("\\'", "'", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
            if (token.Length >= 2 && token[0] == '"' && token[^1] == '"') return token[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
            throw new FormatException();
        }

        private static object? Primitive(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null => null,
            _ => null
        };

        private static bool Truthy(object? value) => value is bool boolean && boolean;
        private static bool Take(IReadOnlyList<string> tokens, ref int index, string token)
        {
            if (index >= tokens.Count || tokens[index] != token) return false;
            index++;
            return true;
        }
        private static bool IsValue(string token) => token is "true" or "false" || token.EndsWith(".value", StringComparison.Ordinal) ||
            double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ||
            (token.Length >= 2 && (token[0] == '\'' && token[^1] == '\'' || token[0] == '"' && token[^1] == '"'));
    }
}

/*
 * Imlight
 * Copyright (C) 2025 Revive101
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Imcodec.IO;
using Imcodec.ObjectProperty;
using Imcodec.Types;

namespace Imlight.Setup;

/// <summary>
/// Turns a decoded game object into JSON, writing every nested object as what it really is (a quest
/// result, a pet talent, and so on) with its class name in "$class", so nothing inside is lost.
/// </summary>
internal static class GameFileJson {

    internal const string CLASS_KEY = "$class";

    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> s_properties = new();

    /// <summary>Decodes a BINd game file, or returns null when it is not one this app knows.</summary>
    internal static PropertyClass? Decode(byte[] data) {
        if (!IsBind(data)) {
            return null;
        }

        try {
            return new BindSerializer().Deserialize<PropertyClass>(data, out var decoded) ? decoded : null;
        }
        catch (Exception) {
            return null;
        }
    }

    internal static bool IsBind(ReadOnlySpan<byte> data)
        => data.Length >= 4 && data[0] == 'B' && data[1] == 'I' && data[2] == 'N' && data[3] == 'd';

    internal static JsonNode? ToJson(object? value, int depth = 0) {
        if (value is null || depth > 64) {
            return null;
        }

        switch (value) {
            case string text: return JsonValue.Create(text);
            case ByteString bytes: return JsonValue.Create((string) bytes);
            case bool flag: return JsonValue.Create(flag);
            case Enum named: return JsonValue.Create(named.ToString());
            case GID gid: return JsonValue.Create(gid.Full);
            case float single: return float.IsFinite(single) ? JsonValue.Create(single) : JsonValue.Create(single.ToString());
            case double number: return double.IsFinite(number) ? JsonValue.Create(number) : JsonValue.Create(number.ToString());
            case byte or sbyte or short or ushort or int or uint or long or ulong or decimal:
                return JsonValue.Create(Convert.ToDecimal(value));
            case char letter: return JsonValue.Create(letter.ToString());
            case byte[] raw: return JsonValue.Create(Convert.ToHexString(raw));
            case PropertyClass decoded: return ObjectToJson(decoded, depth);
            case IDictionary map: {
                var json = new JsonObject();
                foreach (DictionaryEntry entry in map) {
                    json[entry.Key.ToString() ?? ""] = ToJson(entry.Value, depth + 1);
                }

                return json;
            }
            case IEnumerable list: {
                var json = new JsonArray();
                foreach (var item in list) {
                    json.Add(ToJson(item, depth + 1));
                }

                return json;
            }
        }

        // Vectors, colors and other small structs: their public fields.
        var type = value.GetType();
        var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
        if (type.IsValueType && fields.Length > 0) {
            var json = new JsonObject();
            foreach (var field in fields) {
                json[field.Name] = ToJson(field.GetValue(value), depth + 1);
            }

            return json;
        }

        return JsonValue.Create(value.ToString());
    }

    private static JsonObject ObjectToJson(PropertyClass decoded, int depth) {
        var json = new JsonObject { [CLASS_KEY] = decoded.GetType().Name };
        foreach (var property in PropertiesOf(decoded.GetType())) {
            object? value;
            try {
                value = property.GetValue(decoded);
            }
            catch (Exception) {
                continue;
            }

            json[property.Name] = ToJson(value, depth + 1);
        }

        return json;
    }

    private static PropertyInfo[] PropertiesOf(Type type) => s_properties.GetOrAdd(type, t => t
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && p.DeclaringType != typeof(PropertyClass))
        // Base class properties first, in the order the game declares them.
        .OrderBy(p => Depth(p.DeclaringType))
        .ToArray());

    private static int Depth(Type? type) {
        var depth = 0;
        for (; type?.BaseType is not null; type = type.BaseType) {
            depth++;
        }

        return depth;
    }

    // ---------------------------------------------------------------------
    // Reading values back out, for the key details
    // ---------------------------------------------------------------------

    internal static string Text(JsonNode? node, string key) {
        var value = node is JsonObject obj && obj.TryGetPropertyValue(key, out var found) ? found : null;

        return value switch {
            null => "",
            JsonValue scalar => scalar.ToString(),
            JsonArray array => string.Join(", ", array.Select(item => item is JsonObject o ? ClassOf(o) : item?.ToString() ?? "")),
            JsonObject inner => ClassOf(inner),
            _ => value.ToString(),
        };
    }

    internal static string ClassOf(JsonNode? node)
        => node is JsonObject obj && obj.TryGetPropertyValue(CLASS_KEY, out var name) ? name?.ToString() ?? "" : "";

    /// <summary>Every object anywhere inside a node, the node itself first.</summary>
    internal static IEnumerable<JsonObject> AllObjects(JsonNode? node) {
        switch (node) {
            case JsonObject obj:
                yield return obj;
                foreach (var (_, child) in obj) {
                    foreach (var inner in AllObjects(child)) {
                        yield return inner;
                    }
                }

                break;
            case JsonArray array:
                foreach (var child in array) {
                    foreach (var inner in AllObjects(child)) {
                        yield return inner;
                    }
                }

                break;
        }
    }

    /// <summary>
    /// The plain values of an object (numbers, words, lists of words) as "name=value; ...", leaving
    /// out empty ones, for a one-cell summary.
    /// </summary>
    internal static string Summary(JsonObject obj, int maxLength = 1500) {
        var text = new StringBuilder();
        foreach (var (key, value) in obj) {
            if (key == CLASS_KEY || value is null || value is JsonObject) {
                continue;
            }

            string shown;
            if (value is JsonArray array) {
                if (array.Count == 0) {
                    continue;
                }

                shown = "[" + string.Join(", ", array.Select(item => item is JsonObject o ? ClassOf(o) : item?.ToString() ?? "")) + "]";
            }
            else {
                shown = value.ToString();
                if (shown is "" or "0" or "false") {
                    continue;
                }
            }

            if (text.Length > 0) {
                text.Append("; ");
            }

            text.Append(key.StartsWith("m_") ? key[2..] : key).Append('=').Append(shown);
            if (text.Length > maxLength) {
                text.Length = maxLength;
                text.Append('…');
                break;
            }
        }

        return text.ToString();
    }

}

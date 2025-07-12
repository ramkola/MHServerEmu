using MHServerEmu.Core.Logging;
using MHServerEmu.Core.VectorMath;
using MHServerEmu.Games.GameData.Calligraphy;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Properties;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MHServerEmu.Games.GameData.PatchManager
{
    public class PrototypePatchEntry
    {
        public bool Enabled { get; }
        public string Prototype { get; }
        public string Path { get; }
        public string Description { get; }
        public ValueBase Value { get; }
        public PatchOperation Operation { get; }

        [JsonIgnore]
        public List<PathSegment> PathSegments { get; }
        [JsonIgnore]
        public bool Patched { get; set; }

        [JsonConstructor]
        public PrototypePatchEntry(bool enabled, string prototype, string path, string description, ValueBase value, PatchOperation operation = PatchOperation.Set)
        {
            Enabled = enabled;
            Prototype = prototype;
            Path = path;
            Description = description;
            Value = value;
            Operation = operation;
            PathSegments = ParsePath(path);
            Patched = false;
        }

        private static List<PathSegment> ParsePath(string path)
        {
            var segments = new List<PathSegment>();
            var regex = new Regex(@"([a-zA-Z_][a-zA-Z0-9_]*)(\[(\d+)\])*");
            var matches = regex.Matches(path);

            foreach (Match match in matches)
            {
                string fieldName = match.Groups[1].Value;
                var indices = new List<int>();

                var indexCaptures = match.Groups[3].Captures;
                foreach (Capture capture in indexCaptures)
                {
                    if (int.TryParse(capture.Value, out int index))
                        indices.Add(index);
                }

                segments.Add(new PathSegment(fieldName, indices));
            }

            return segments;
        }
    }

    public class PathSegment
    {
        public string FieldName { get; }
        public List<int> ArrayIndices { get; }
        public bool IsArray => ArrayIndices.Count > 0;
        public bool IsNestedArray => ArrayIndices.Count > 1;

        public PathSegment(string fieldName, List<int> arrayIndices)
        {
            FieldName = fieldName;
            ArrayIndices = arrayIndices ?? new List<int>();
        }
    }

    public enum PatchOperation
    {
        Set,
        Add,
        Insert,
        Remove,
        Replace
    }

    public class PatchEntryConverter : JsonConverter<PrototypePatchEntry>
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        public override PrototypePatchEntry Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using JsonDocument doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;

            if (!root.TryGetProperty("ValueType", out var valueTypeElement))
                throw new JsonException("Missing required property 'ValueType'");

            if (!root.TryGetProperty("Value", out var valueElement))
                throw new JsonException("Missing required property 'Value'");

            string valueTypeString = valueTypeElement.GetString();

            if (!valueTypeString.EndsWith("Array") && valueTypeString.Contains("[]"))
            {
                valueTypeString = valueTypeString.Replace("[]", "Array");
            }

            if (!Enum.TryParse<ValueType>(valueTypeString, out var valueType))
                throw new JsonException($"Invalid ValueType: {valueTypeString}");

            var operation = PatchOperation.Set;
            if (root.TryGetProperty("Operation", out var operationElement))
            {
                if (!Enum.TryParse<PatchOperation>(operationElement.GetString(), out operation))
                    operation = PatchOperation.Set;
            }

            try
            {
                var entry = new PrototypePatchEntry
                (
                    root.GetProperty("Enabled").GetBoolean(),
                    root.GetProperty("Prototype").GetString(),
                    root.GetProperty("Path").GetString(),
                    root.GetProperty("Description").GetString(),
                    GetValueBase(valueElement, valueType),
                    operation
                );

                if (valueType == ValueType.Properties) entry.Patched = true;

                return entry;
            }
            catch (Exception ex)
            {
                throw new JsonException($"Failed to deserialize PrototypePatchEntry: {ex.Message}", ex);
            }
        }

        public static ValueBase GetValueBase(JsonElement jsonElement, ValueType valueType)
        {
            switch (valueType)
            {
                case ValueType.String: return new SimpleValue<string>(jsonElement.GetString(), valueType);
                case ValueType.Boolean: return new SimpleValue<bool>(jsonElement.GetBoolean(), valueType);
                case ValueType.Float: return new SimpleValue<float>(jsonElement.GetSingle(), valueType);
                case ValueType.Integer: return new SimpleValue<int>(jsonElement.GetInt32(), valueType);
                case ValueType.Enum: return new SimpleValue<string>(jsonElement.GetString(), valueType);
                case ValueType.PrototypeGuid: return new SimpleValue<PrototypeGuid>((PrototypeGuid)jsonElement.GetUInt64(), valueType);
                case ValueType.PrototypeId:
                case ValueType.PrototypeDataRef: return new SimpleValue<PrototypeId>((PrototypeId)jsonElement.GetUInt64(), valueType);
                case ValueType.LocaleStringId: return new SimpleValue<LocaleStringId>((LocaleStringId)jsonElement.GetUInt64(), valueType);
                case ValueType.AssetId: return new SimpleValue<string>(jsonElement.GetString(), valueType);
                case ValueType.Vector3: return new SimpleValue<Vector3>(ParseJsonVector3(jsonElement), valueType);

                case ValueType.Prototype:
                case ValueType.Properties:
                case ValueType.PrototypeArray:
                case ValueType.PrototypeIdArray:
                case ValueType.PrototypeDataRefArray:
                    return new JsonValue(jsonElement.Clone(), valueType);

                default:
                    throw new NotSupportedException($"Type {valueType} not support.");
            }
        }

        private static Vector3 ParseJsonVector3(JsonElement jsonElement)
        {
            if (jsonElement.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Json element is not array");

            var jsonArray = jsonElement.EnumerateArray().ToArray();
            if (jsonArray.Length != 3)
                throw new InvalidOperationException("Json element is not Vector3");

            return new Vector3(jsonArray[0].GetSingle(), jsonArray[1].GetSingle(), jsonArray[2].GetSingle());
        }

        public static object ParseJsonElement(JsonElement value, Type targetType)
        {
            if (typeof(Prototype).IsAssignableFrom(targetType) && !targetType.IsArray)
            {
                return ParseJsonPrototype(value);
            }
            if (targetType.IsArray)
            {
                var elementType = targetType.GetElementType();
                var jsonArray = value.EnumerateArray().ToArray();
                var newArray = Array.CreateInstance(elementType, jsonArray.Length);

                for (int i = 0; i < jsonArray.Length; i++)
                {
                    var elementValue = ParseJsonElement(jsonArray[i], elementType);
                    newArray.SetValue(elementValue, i);
                }
                return newArray;
            }
            if (targetType == typeof(PropertyCollection))
            {
                return ParseJsonProperties(value);
            }

            if (targetType == typeof(PrototypeId))
            {
                if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out ulong ulongValue))
                    return (PrototypeId)ulongValue;
            }

            if (targetType == typeof(AssetId))
            {
                if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out ulong ulongValue))
                    return (AssetId)ulongValue;
            }

            if (targetType == typeof(PrototypeGuid))
            {
                if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out ulong ulongValue))
                    return (PrototypeGuid)ulongValue;
            }

            if (targetType == typeof(LocaleStringId))
            {
                if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out ulong ulongValue))
                    return (LocaleStringId)ulongValue;
            }

            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    return value.GetString();
                case JsonValueKind.Number:
                    if (value.TryGetUInt64(out ulong ulongValue))
                        return ulongValue;
                    else if (value.TryGetInt64(out long decimalValue))
                        return decimalValue;
                    else if (value.TryGetDouble(out double doubleValue))
                        return doubleValue;
                    else
                        return value.GetRawText();
                case JsonValueKind.True:
                case JsonValueKind.False:
                    return value.GetBoolean();
                default:
                    return value.ToString();
            }
        }

        public static Prototype ParseJsonPrototype(JsonElement jsonElement)
        {
            var referenceType = (PrototypeId)jsonElement.GetProperty("ParentDataRef").GetUInt64();
            Type classType = GameDatabase.DataDirectory.GetPrototypeClassType(referenceType);
            if (classType == null)
            {
                Logger.Error($"ParseJsonPrototype: Could not find class type for ParentDataRef {referenceType.GetName()}");
                return null;
            }

            var prototype = GameDatabase.PrototypeClassManager.AllocatePrototype(classType);

            CalligraphySerializer.CopyPrototypeDataRefFields(prototype, referenceType);
            prototype.ParentDataRef = referenceType;

            foreach (var property in jsonElement.EnumerateObject())
            {
                if (property.Name == "ParentDataRef") continue;

                var fieldInfo = prototype.GetType().GetProperty(property.Name);
                if (fieldInfo == null)
                {
                    if (property.Name != "PolymorphicData")
                    {
                        Logger.Warn($"ParseJsonPrototype: Field '{property.Name}' not found on prototype of type '{classType.Name}'.");
                    }
                    continue;
                }

                try
                {
                    object valueToSet = ParseJsonElement(property.Value, fieldInfo.PropertyType);
                    object convertedValue = PrototypePatchManager.ConvertValue(valueToSet, fieldInfo.PropertyType);
                    fieldInfo.SetValue(prototype, convertedValue);
                }
                catch (Exception ex)
                {
                    Logger.ErrorException(ex, $"ParseJsonPrototype: Failed to set field '{property.Name}' on prototype.");
                }
            }

            return prototype;
        }

        public static PropertyCollection ParseJsonProperties(JsonElement jsonElement)
        {
            PropertyCollection properties = new();
            var infoTable = GameDatabase.PropertyInfoTable;

            foreach (var property in jsonElement.EnumerateObject())
            {
                var propEnum = (PropertyEnum)Enum.Parse(typeof(PropertyEnum), property.Name);
                MHServerEmu.Games.Properties.PropertyInfo propertyInfo = infoTable.LookupPropertyInfo(propEnum);
                PropertyId propId = ParseJsonPropertyId(property.Value, propEnum, propertyInfo);
                PropertyValue propValue = ParseJsonPropertyValue(property.Value, propertyInfo);
                properties.SetProperty(propValue, propId);
            }

            return properties;
        }

        public static PropertyId ParseJsonPropertyId(JsonElement jsonElement, PropertyEnum propEnum, MHServerEmu.Games.Properties.PropertyInfo propInfo)
        {
            int paramCount = propInfo.ParamCount;
            if (paramCount == 0) return new(propEnum);

            var jsonArray = jsonElement.EnumerateArray().ToArray();
            Span<PropertyParam> paramValues = stackalloc PropertyParam[Property.MaxParamCount];
            propInfo.DefaultParamValues.CopyTo(paramValues);

            for (int i = 0; i < paramCount; i++)
            {
                if (i >= 4) break;
                if (i >= jsonArray.Length) continue;

                var paramValue = jsonArray[i];

                switch (propInfo.GetParamType(i))
                {
                    case PropertyParamType.Asset:
                        var assetParam = (AssetId)ParseJsonElement(paramValue, typeof(AssetId));
                        paramValues[i] = Property.ToParam(assetParam);
                        break;

                    case PropertyParamType.Prototype:
                        var protoRefParam = (PrototypeId)ParseJsonElement(paramValue, typeof(PrototypeId));
                        paramValues[i] = Property.ToParam(propEnum, i, protoRefParam);
                        break;

                    case PropertyParamType.Integer:
                        if (paramValue.TryGetInt64(out long decimalValue))
                            paramValues[i] = (PropertyParam)(int)decimalValue;
                        break;

                    default:
                        throw new InvalidOperationException("Encountered an unknown prop param type in an ParseJsonPropertyId!");
                }
            }

            return new(propEnum, paramValues);
        }

        public static PropertyValue ParseJsonPropertyValue(JsonElement jsonElement, MHServerEmu.Games.Properties.PropertyInfo propInfo)
        {
            if (propInfo.ParamCount > 0)
            {
                var jsonArray = jsonElement.EnumerateArray().ToArray();
                jsonElement = jsonArray[^1];
            }

            switch (propInfo.DataType)
            {
                case PropertyDataType.Integer:
                    if (jsonElement.TryGetInt64(out long decimalValue))
                        return (PropertyValue)decimalValue;
                    break;

                case PropertyDataType.Real:
                    if (jsonElement.TryGetDouble(out double doubleValue))
                        return (PropertyValue)(float)doubleValue;
                    break;

                case PropertyDataType.Boolean:
                    return (PropertyValue)jsonElement.GetBoolean();

                case PropertyDataType.Prototype:
                    var protoRefValue = (PrototypeId)ParseJsonElement(jsonElement, typeof(PrototypeId));
                    return (PropertyValue)protoRefValue;

                case PropertyDataType.Asset:
                    AssetId assetValue = (AssetId)ParseJsonElement(jsonElement, typeof(AssetId));
                    return (PropertyValue)assetValue;

                default:
                    throw new InvalidOperationException($"[ParseJsonPropertyValue] Assignment into invalid property (property type is not int/float/bool)! Property: {propInfo.PropertyName}");
            }

            return propInfo.DefaultValue;
        }

        public override void Write(Utf8JsonWriter writer, PrototypePatchEntry value, JsonSerializerOptions options)
        {
            throw new NotImplementedException();
        }
    }

    public enum ValueType
    {
        String,
        Boolean,
        Float,
        Integer,
        Enum,
        AssetId,
        PrototypeGuid,
        PrototypeId,
        PrototypeIdArray,
        LocaleStringId,
        PrototypeDataRef,
        PrototypeDataRefArray,
        Prototype,
        PrototypeArray,
        Vector3,
        Properties
    }

    public abstract class ValueBase
    {
        public abstract ValueType ValueType { get; }
        public abstract object GetValue();
    }

    public class SimpleValue<T> : ValueBase
    {
        public override ValueType ValueType { get; }
        public T Value { get; }

        public SimpleValue(T value, ValueType valueType)
        {
            Value = value;
            ValueType = valueType;
        }

        public override object GetValue() => Value;
    }

    public class JsonValue : ValueBase
    {
        public override ValueType ValueType { get; }
        public JsonElement Value { get; }
        private object _parsedValue;

        public JsonValue(JsonElement value, ValueType valueType)
        {
            Value = value;
            ValueType = valueType;
        }

        public override object GetValue()
        {
            if (_parsedValue == null)
            {
                Type targetType = ValueType switch
                {
                    ValueType.Prototype => typeof(Prototype),
                    ValueType.Properties => typeof(PropertyCollection),
                    ValueType.PrototypeArray => typeof(Prototype[]),
                    ValueType.PrototypeIdArray => typeof(PrototypeId[]),
                    ValueType.PrototypeDataRefArray => typeof(PrototypeId[]),
                    _ => typeof(object)
                };
                _parsedValue = PatchEntryConverter.ParseJsonElement(Value, targetType);
            }
            return _parsedValue;
        }
    }
}

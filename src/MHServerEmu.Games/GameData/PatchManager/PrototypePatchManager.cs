using MHServerEmu.Core.Helpers;
using MHServerEmu.Core.Logging;
using MHServerEmu.Games.GameData.Prototypes;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using MHServerEmu.Games.GameData.Calligraphy;

namespace MHServerEmu.Games.GameData.PatchManager
{
    public class PrototypePatchManager
    {
        private static readonly Logger Logger = LogManager.CreateLogger();
        private Stack<PrototypeId> _protoStack = new();
        private readonly Dictionary<PrototypeId, List<PrototypePatchEntry>> _patchDict = new();
        private Dictionary<Prototype, string> _pathDict = new();
        private HashSet<string> _processedPaths = new(); // Track processed paths to avoid duplicates
        private bool _initialized = false;

        public static PrototypePatchManager Instance { get; } = new();

        public void Initialize(bool enablePatchManager)
        {
            if (enablePatchManager)
            {
                _initialized = LoadPatchDataFromDisk();
            }
        }

        private bool LoadPatchDataFromDisk()
        {
            string patchDirectory = Path.Combine(FileHelper.DataDirectory, "Game", "Patches");
            if (Directory.Exists(patchDirectory) == false)
                return Logger.WarnReturn(false, "LoadPatchDataFromDisk(): Game data directory not found");

            int count = 0;
            var options = new JsonSerializerOptions { Converters = { new PatchEntryConverter() } };

            // Read all .json files that start with PatchData
            foreach (string filePath in FileHelper.GetFilesWithPrefix(patchDirectory, "PatchData", "json"))
            {
                string fileName = Path.GetFileName(filePath);

                PrototypePatchEntry[] updateValues = FileHelper.DeserializeJson<PrototypePatchEntry[]>(filePath, options);
                if (updateValues == null)
                {
                    Logger.Warn($"LoadPatchDataFromDisk(): Failed to parse {fileName}, skipping");
                    continue;
                }

                foreach (PrototypePatchEntry value in updateValues)
                {
                    if (value.Enabled == false) continue;
                    PrototypeId prototypeId = GameDatabase.GetPrototypeRefByName(value.Prototype);
                    if (prototypeId == PrototypeId.Invalid) continue;
                    AddPatchValue(prototypeId, value);
                    count++;
                }

                Logger.Trace($"Parsed patch data from {fileName}");
            }

            return Logger.InfoReturn(true, $"Loaded {count} patches");
        }

        private void AddPatchValue(PrototypeId prototypeId, in PrototypePatchEntry value)
        {
            if (_patchDict.TryGetValue(prototypeId, out var patchList) == false)
            {
                patchList = [];
                _patchDict[prototypeId] = patchList;
            }
            patchList.Add(value);
        }

        public bool CheckProperties(PrototypeId protoRef, out Properties.PropertyCollection prop)
        {
            prop = null;
            if (_initialized == false) return false;

            if (protoRef != PrototypeId.Invalid && _patchDict.TryGetValue(protoRef, out var list))
                foreach (var entry in list)
                    if (entry.Value.ValueType == ValueType.Properties)
                    {
                        prop = entry.Value.GetValue() as Properties.PropertyCollection;
                        return prop != null;
                    }

            return false;
        }

        public bool PreCheck(PrototypeId protoRef)
        {
            if (_initialized == false) return false;

            if (protoRef != PrototypeId.Invalid && _patchDict.TryGetValue(protoRef, out var list))
            {
                if (NotPatched(list))
                    _protoStack.Push(protoRef);
            }

            return _protoStack.Count > 0;
        }

        private static bool NotPatched(List<PrototypePatchEntry> list)
        {
            foreach (var entry in list)
                if (entry.Patched == false) return true;
            return false;
        }

        public void PostOverride(Prototype prototype)
        {
            if (_protoStack.Count == 0) return;

            string currentPath = string.Empty;
            if (prototype.DataRef == PrototypeId.Invalid
                && _pathDict.TryGetValue(prototype, out currentPath) == false) return;

            PrototypeId patchProtoRef = _protoStack.Peek();
            if (prototype.DataRef != PrototypeId.Invalid)
            {
                if (prototype.DataRef != patchProtoRef) return;
                if (_patchDict.ContainsKey(prototype.DataRef))
                    patchProtoRef = _protoStack.Pop();
            }

            if (_patchDict.TryGetValue(patchProtoRef, out var list) == false) return;

            // Process patches in order of depth (shallow first, then deeper)
            var sortedEntries = list.Where(e => !e.Patched)
                                   .OrderBy(e => GetPathDepth(e.Path))
                                   .ToList();

            foreach (var entry in sortedEntries)
            {
                if (entry.Patched == false)
                {
                    if (CheckAndUpdate(entry, prototype, currentPath))
                    {
                        // After successful patch, try to apply any deeper nested patches
                        ApplyDeeperNestedPatches(entry, prototype, currentPath, list);
                    }
                }
            }

            if (_protoStack.Count == 0)
            {
                _pathDict.Clear();
                _processedPaths.Clear();
            }
        }

        private static int GetPathDepth(string path)
        {
            return path.Count(c => c == '.');
        }

        private void ApplyDeeperNestedPatches(PrototypePatchEntry parentEntry, Prototype prototype, string currentPath, List<PrototypePatchEntry> allEntries)
        {
            string parentPath = NormalizePath(currentPath);
            string parentEntryPath = NormalizePath(parentEntry.СlearPath);

            // Find entries that are deeper nested under this parent
            var deeperEntries = allEntries.Where(e => !e.Patched &&
                                                     e != parentEntry &&
                                                     IsChildPath(e.СlearPath, parentEntryPath))
                                         .OrderBy(e => GetPathDepth(e.Path))
                                         .ToList();

            foreach (var deeperEntry in deeperEntries)
            {
                ApplyNestedPatch(deeperEntry, prototype, parentPath);
            }
        }

        private static bool IsChildPath(string childPath, string parentPath)
        {
            if (string.IsNullOrEmpty(parentPath)) return true;
            return childPath.StartsWith(parentPath + ".");
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            return path.StartsWith('.') ? path[1..] : path;
        }

        private void ApplyNestedPatch(PrototypePatchEntry entry, Prototype rootPrototype, string currentPath)
        {
            try
            {
                var pathParts = ParseNestedPath(entry.Path);
                object targetObject = rootPrototype;
                string accumulatedPath = currentPath;

                // Navigate to the target object
                for (int i = 0; i < pathParts.Length - 1; i++)
                {
                    var part = pathParts[i];
                    targetObject = NavigateToProperty(targetObject, part, ref accumulatedPath);
                    if (targetObject == null) return;
                }

                // Apply the patch to the final target
                var finalPart = pathParts[^1];
                ApplyPatchToTarget(targetObject, finalPart, entry);

                Logger.Trace($"Deep Patch Applied: {entry.Prototype} {entry.Path} = {entry.Value.GetValue()}");
            }
            catch (Exception ex)
            {
                Logger.WarnException(ex, $"Failed to apply deep nested patch: [{entry.Prototype}] [{entry.Path}] {ex.Message}");
            }
        }

        private static PathPart[] ParseNestedPath(string path)
        {
            var parts = new List<PathPart>();
            var segments = path.Split('.');

            foreach (var segment in segments)
            {
                if (string.IsNullOrEmpty(segment)) continue;

                var part = new PathPart();
                int bracketIndex = segment.IndexOf('[');

                if (bracketIndex != -1)
                {
                    part.PropertyName = segment[..bracketIndex];
                    part.IsArray = true;

                    int closeBracket = segment.IndexOf(']', bracketIndex);
                    if (closeBracket > bracketIndex)
                    {
                        string indexStr = segment.Substring(bracketIndex + 1, closeBracket - bracketIndex - 1);
                        if (int.TryParse(indexStr, out int index))
                            part.ArrayIndex = index;
                    }
                }
                else
                {
                    part.PropertyName = segment;
                    part.IsArray = false;
                    part.ArrayIndex = -1;
                }

                parts.Add(part);
            }

            return parts.ToArray();
        }

        private object NavigateToProperty(object obj, PathPart pathPart, ref string accumulatedPath)
        {
            if (obj == null) return null;

            var propertyInfo = obj.GetType().GetProperty(pathPart.PropertyName);
            if (propertyInfo == null) return null;

            object propertyValue = propertyInfo.GetValue(obj);
            if (propertyValue == null) return null;

            // Update accumulated path
            accumulatedPath = string.IsNullOrEmpty(accumulatedPath) ? pathPart.PropertyName : $"{accumulatedPath}.{pathPart.PropertyName}";

            if (pathPart.IsArray && pathPart.ArrayIndex >= 0)
            {
                if (propertyValue is Array array && pathPart.ArrayIndex < array.Length)
                {
                    accumulatedPath += $"[{pathPart.ArrayIndex}]";
                    return array.GetValue(pathPart.ArrayIndex);
                }
                return null;
            }

            return propertyValue;
        }

        private static void ApplyPatchToTarget(object targetObject, PathPart finalPart, PrototypePatchEntry entry)
        {
            if (targetObject == null) return;

            var propertyInfo = targetObject.GetType().GetProperty(finalPart.PropertyName);
            if (propertyInfo == null) return;

            if (finalPart.IsArray)
            {
                if (finalPart.ArrayIndex >= 0)
                    SetIndexValue(targetObject, propertyInfo, finalPart.ArrayIndex, entry.Value);
                else
                    InsertValue(targetObject, propertyInfo, entry.Value);
            }
            else
            {
                Type fieldType = propertyInfo.PropertyType;
                object convertedValue = ConvertValue(entry.Value.GetValue(), fieldType);
                propertyInfo.SetValue(targetObject, convertedValue);
            }

            entry.Patched = true;
        }

        private static bool CheckAndUpdate(PrototypePatchEntry entry, Prototype prototype, string currentPath)
        {
            if (currentPath.StartsWith('.')) currentPath = currentPath[1..];
            if (entry.СlearPath != currentPath) return false;

            var fieldInfo = prototype.GetType().GetProperty(entry.FieldName);
            if (fieldInfo == null) return false;

            UpdateValue(prototype, fieldInfo, entry);
            Logger.Trace($"Patch Prototype: {entry.Prototype} {entry.Path} = {entry.Value.GetValue()}");

            return true;
        }

        public static object ConvertValue(object rawValue, Type targetType)
        {
            // Handle AssetId lookup from a string value
            if (targetType == typeof(AssetId) && rawValue is string assetString)
            {
                // Example format: "MarvelUIIcons.Power_TaskMaster_BasicShot (Powers/Types/PowerIconPathType.type)"
                int typeNameStart = assetString.LastIndexOf('(');
                int typeNameEnd = assetString.LastIndexOf(')');

                if (typeNameStart != -1 && typeNameEnd > typeNameStart)
                {
                    string assetName = assetString.Substring(0, typeNameStart).Trim();
                    string assetTypeName = assetString.Substring(typeNameStart + 1, typeNameEnd - typeNameStart - 1).Trim();

                    var assetDirectory = GameDatabase.DataDirectory.AssetDirectory;
                    var assetType = assetDirectory.GetAssetType(assetTypeName);

                    if (assetType != null)
                    {
                        var assetId = assetType.FindAssetByName(assetName, DataFileSearchFlags.CaseInsensitive);
                        if (assetId != AssetId.Invalid)
                        {
                            return assetId;
                        }
                        Logger.Warn($"Could not find asset '{assetName}' in asset type '{assetTypeName}'.");
                    }
                    else
                    {
                        Logger.Warn($"Could not find asset type '{assetTypeName}'.");
                    }
                }
            }

            // Handle nulls immediately
            if (rawValue == null || (rawValue is JsonElement jsonVal && jsonVal.ValueKind == JsonValueKind.Null))
                return null;

            // Direct assignment if types already match
            if (targetType.IsInstanceOfType(rawValue))
                return rawValue;

            // Handle array conversion from a JsonElement[] (for PrototypeArray)
            if (targetType.IsArray && rawValue is JsonElement[] jsonElementArray)
            {
                Type elementType = targetType.GetElementType();
                Array newArray = Array.CreateInstance(elementType, jsonElementArray.Length);

                for (int i = 0; i < jsonElementArray.Length; i++)
                {
                    // Recursively convert each element of the JsonElement array
                    object elementValue = ConvertValue(jsonElementArray[i], elementType);
                    newArray.SetValue(elementValue, i);
                }
                return newArray;
            }

            // Handle array conversion from a single JsonElement (for nested arrays)
            if (targetType.IsArray && rawValue is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.Array)
            {
                Type elementType = targetType.GetElementType();
                var jsonArray = jsonElement.EnumerateArray().ToArray();
                Array newArray = Array.CreateInstance(elementType, jsonArray.Length);

                for (int i = 0; i < jsonArray.Length; i++)
                {
                    object elementValue = ConvertValue(jsonArray[i], elementType);
                    newArray.SetValue(elementValue, i);
                }
                return newArray;
            }

            // Handle complex single objects from JsonElement
            if (IsComplexObjectType(targetType) && rawValue is JsonElement complexJson && complexJson.ValueKind == JsonValueKind.Object)
            {
                return ConvertComplexObject(complexJson, targetType);
            }

            // Handle Prototypes from JsonElement
            if (typeof(Prototype).IsAssignableFrom(targetType) && rawValue is JsonElement prototypeJson)
            {
                return PatchEntryConverter.ParseJsonPrototype(prototypeJson);
            }

            // Handle Enums from a string value
            if (targetType.IsEnum && rawValue is string enumString)
            {
                return Enum.Parse(targetType, enumString, true);
            }

            // Fallback for simple IConvertible types (int, float, bool, etc.)
            TypeConverter converter = TypeDescriptor.GetConverter(targetType);
            if (converter != null && converter.CanConvertFrom(rawValue.GetType()))
                return converter.ConvertFrom(rawValue);

            return Convert.ChangeType(rawValue, targetType);
        }

        private static bool IsComplexObjectType(Type type)
        {
            // Check if the type is a complex object that likely needs JSON parsing
            return type.IsClass &&
                   type != typeof(string) &&
                   !type.IsPrimitive &&
                   !typeof(Prototype).IsAssignableFrom(type) &&
                   type.Namespace != null &&
                   (type.Namespace.Contains("MHServerEmu") || type.Namespace.Contains("Games"));
        }

        private static object ConvertComplexObject(JsonElement jsonElement, Type targetType)
        {
            // This method is for single objects. Arrays are now handled in ConvertValue.
            if (jsonElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException($"Cannot convert JSON of type {jsonElement.ValueKind} to a complex object. Expected a JSON object.");
            }

            try
            {
                object instance = Activator.CreateInstance(targetType);
                if (instance == null)
                    throw new InvalidOperationException($"Cannot create instance of {targetType.Name}");

                // Set properties from JSON
                foreach (var property in jsonElement.EnumerateObject())
                {
                    var propertyInfo = targetType.GetProperty(property.Name);
                    if (propertyInfo == null || !propertyInfo.CanWrite) continue;

                    try
                    {
                        object propertyValue = PatchEntryConverter.ParseJsonElement(property.Value, propertyInfo.PropertyType);
                        object convertedValue = ConvertValue(propertyValue, propertyInfo.PropertyType);
                        propertyInfo.SetValue(instance, convertedValue);
                    }
                    catch (Exception ex)
                    {
                        Logger.WarnException(ex, $"Failed to set property {property.Name} on {targetType.Name}: {ex.Message}");
                    }
                }
                return instance;
            }
            catch (Exception ex)
            {
                Logger.WarnException(ex, $"Failed to convert complex object to {targetType.Name}: {ex.Message}");
                throw;
            }
        }

        private static void UpdateValue(Prototype prototype, PropertyInfo fieldInfo, PrototypePatchEntry entry)
        {
            try
            {
                Type fieldType = fieldInfo.PropertyType;
                if (entry.ArrayValue)
                {
                    if (entry.ArrayIndex != -1)
                        SetIndexValue(prototype, fieldInfo, entry.ArrayIndex, entry.Value);
                    else
                        InsertValue(prototype, fieldInfo, entry.Value);
                }
                else
                {
                    object convertedValue = ConvertValue(entry.Value.GetValue(), fieldType);
                    fieldInfo.SetValue(prototype, convertedValue);
                }
                entry.Patched = true;
            }
            catch (Exception ex)
            {
                Logger.WarnException(ex, $"Failed UpdateValue: [{entry.Prototype}] [{entry.Path}] {ex.Message}");
            }
        }

        private static void SetIndexValue(object target, PropertyInfo fieldInfo, int index, ValueBase value)
        {
            Type fieldType = fieldInfo.PropertyType;
            if (fieldType.IsArray == false)
                throw new InvalidOperationException($"Field {fieldInfo.Name} is not array.");

            Array array = (Array)fieldInfo.GetValue(target);
            if (array == null || index < 0 || index >= array.Length)
                throw new IndexOutOfRangeException($"Invalid index {index} for array {fieldInfo.Name}.");

            object valueEntry = value.GetValue();
            Type elementType = fieldType.GetElementType();

            if (elementType == null || IsTypeCompatible(elementType, valueEntry, value.ValueType) == false)
                throw new InvalidOperationException($"Type {value.ValueType} is not assignable to {elementType?.Name}.");

            object converted = ConvertValue(valueEntry, elementType);
            array.SetValue(converted, index);
        }

        private static void InsertValue(object target, PropertyInfo fieldInfo, ValueBase value)
        {
            Type fieldType = fieldInfo.PropertyType;
            if (fieldType.IsArray == false)
                throw new InvalidOperationException($"Field {fieldInfo.Name} is not array.");

            var valueEntry = value.GetValue();
            Type elementType = fieldType.GetElementType();

            if (elementType == null || IsTypeCompatible(elementType, valueEntry, value.ValueType) == false)
                throw new InvalidOperationException($"Type {value.ValueType} is not assignable for {elementType?.Name}.");

            var currentArray = (Array)fieldInfo.GetValue(target);
            int newLength = CalcNewLength(currentArray, valueEntry);
            var newArray = Array.CreateInstance(elementType, newLength);

            if (currentArray != null)
                Array.Copy(currentArray, newArray, currentArray.Length);

            AddElements(newArray, elementType, valueEntry, currentArray?.Length ?? 0);
            fieldInfo.SetValue(target, newArray);
        }

        private static int CalcNewLength(Array currentArray, object valueEntry)
        {
            int currentLength = currentArray?.Length ?? 0;
            int valuesCount = 1;
            if (valueEntry is Array array)
            {
                int length = array.Length;
                if (length > 1) valuesCount = length;
            }
            return currentLength + valuesCount;
        }

        private static bool IsTypeCompatible(Type baseType, object rawValue, ValueType valueType)
        {
            // Handle the case where the raw value is an array (e.g., from a JSON array in the patch)
            if (rawValue is Array rawArray)
            {
                // Check if every element in the array is compatible.
                foreach (var element in rawArray)
                {
                    var elementValueType = valueType.ToString().EndsWith("Array")
                        ? (ValueType)Enum.Parse(typeof(ValueType), valueType.ToString().Replace("Array", ""))
                        : valueType;

                    if (!IsTypeCompatible(baseType, element, elementValueType))
                    {
                        return false;
                    }
                }
                return true; 
            }
            if (typeof(Prototype).IsAssignableFrom(baseType))
            {
                if (rawValue is PrototypeId dataRef)
                {
                    var actualPrototype = GameDatabase.GetPrototype<Prototype>(dataRef);
                    if (actualPrototype == null)
                    {
                        Logger.Warn($"IsTypeCompatible check failed: Could not find prototype for DataRef {dataRef}");
                        return false;
                    }
                    return baseType.IsAssignableFrom(actualPrototype.GetType());
                }
                else if (rawValue is JsonElement jsonElement && (valueType == ValueType.Prototype || valueType == ValueType.ComplexObject))
                {
                    if (jsonElement.TryGetProperty("ParentDataRef", out var parentDataRefElement) && parentDataRefElement.TryGetUInt64(out var id))
                    {
                        var referenceType = (PrototypeId)id;
                        Type classType = GameDatabase.DataDirectory.GetPrototypeClassType(referenceType);
                        if (classType != null)
                        {
                            return baseType.IsAssignableFrom(classType);
                        }
                    }
                    Logger.Warn($"IsTypeCompatible check failed: Could not determine prototype type from JsonElement.");
                    return false;
                }
            }

            Type entryType = rawValue.GetType();
            return baseType.IsAssignableFrom(entryType);
        }

        private static void AddElements(Array newArray, Type elementType, object valueEntry, int lastIndex)
        {
            if (valueEntry is Array array)
            {
                foreach (var entry in array)
                {
                    object elementValue = GetElementValue(entry, elementType);
                    newArray.SetValue(elementValue, lastIndex++);
                }
            }
            else
            {
                object elementValue = GetElementValue(valueEntry, elementType);
                newArray.SetValue(elementValue, lastIndex);
            }
        }

        private static object GetElementValue(object valueEntry, Type elementType)
        {
            //  Handle PrototypeId to Prototype conversion
            if (elementType.IsClass && valueEntry is PrototypeId dataRef)
            {
                var prototype = GameDatabase.GetPrototype<Prototype>(dataRef)
                    ?? throw new InvalidOperationException($"DataRef {dataRef} is not Prototype.");
                valueEntry = prototype;
            }

            return ConvertValue(valueEntry, elementType);
        }

        public void SetPath(Prototype parent, Prototype child, string fieldName)
        {
            string parentPath = _pathDict.TryGetValue(parent, out var path) ? path : string.Empty;
            if (parent.DataRef != PrototypeId.Invalid && _patchDict.ContainsKey(parent.DataRef))
                parentPath = string.Empty;
            _pathDict[child] = $"{parentPath}.{fieldName}";
        }

        public void SetPathIndex(Prototype parent, Prototype child, string fieldName, int index)
        {
            string parentPath = _pathDict.TryGetValue(parent, out var path) ? path : string.Empty;
            if (parent.DataRef != PrototypeId.Invalid && _patchDict.ContainsKey(parent.DataRef))
                parentPath = string.Empty;
            _pathDict[child] = $"{parentPath}.{fieldName}[{index}]";
        }

        private struct PathPart
        {
            public string PropertyName;
            public bool IsArray;
            public int ArrayIndex;
        }
    }
}

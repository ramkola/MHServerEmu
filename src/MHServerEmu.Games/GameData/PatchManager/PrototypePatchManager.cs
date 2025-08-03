using MHServerEmu.Core.Helpers;
using MHServerEmu.Core.Logging;
using MHServerEmu.Games.GameData.Prototypes;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using MHServerEmu.Games.GameData.Calligraphy;
using System.Collections.Generic;
using System.Linq;
using System;
using MHServerEmu.Games.Properties;

namespace MHServerEmu.Games.GameData.PatchManager
{
    public class PrototypePatchManager
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        private static readonly Dictionary<PrototypeId, List<PrototypePatchEntry>> _patchDict = new();
        private static readonly Dictionary<PrototypeId, Prototype> _deferredPrototypes = new();
        private static readonly HashSet<PrototypeId> _postProcessedPrototypes = new();
        private static readonly Dictionary<Prototype, string> _instancePathMap = new();

        private bool _initialized = false;

        public static PrototypePatchManager Instance { get; } = new();

        public void Initialize(bool enablePatchManager)
        {
            if (enablePatchManager && !_initialized)
            {
                _initialized = LoadPatchDataFromDisk();
            }
        }

        private bool LoadPatchDataFromDisk()
        {
            _patchDict.Clear();
            _deferredPrototypes.Clear();
            _postProcessedPrototypes.Clear();
            _instancePathMap.Clear();

            string patchDirectory = Path.Combine(FileHelper.DataDirectory, "Game", "Patches");
            if (!Directory.Exists(patchDirectory))
                return Logger.WarnReturn(false, "LoadPatchDataFromDisk(): Game data directory not found");

            int count = 0;
            var options = new JsonSerializerOptions { Converters = { new PatchEntryConverter() } };

            foreach (string filePath in FileHelper.GetFilesWithPrefix(patchDirectory, "PatchData", "json"))
            {
                string fileName = Path.GetFileName(filePath);
                try
                {
                    PrototypePatchEntry[] updateValues = FileHelper.DeserializeJson<PrototypePatchEntry[]>(filePath, options);
                    if (updateValues == null) continue;

                    foreach (PrototypePatchEntry value in updateValues)
                    {
                        if (!value.Enabled) continue;
                        PrototypeId prototypeId = GameDatabase.GetPrototypeRefByName(value.Prototype);
                        if (prototypeId == PrototypeId.Invalid) continue;

                        AddPatchValue(prototypeId, value);
                        count++;
                    }
                }
                catch (Exception ex)
                {
                    Logger.ErrorException(ex, $"Error loading patch file {fileName}");
                }
            }
            return Logger.InfoReturn(true, $"Loaded {count} patches");
        }

        private void AddPatchValue(PrototypeId prototypeId, in PrototypePatchEntry value)
        {
            if (!_patchDict.TryGetValue(prototypeId, out var patchList))
            {
                patchList = new List<PrototypePatchEntry>();
                _patchDict[prototypeId] = patchList;
            }
            patchList.Add(value);
        }

        public bool CheckProperties(PrototypeId protoRef, out PropertyCollection prop)
        {
            prop = null;
            if (!_initialized) return false;

            if (_patchDict.TryGetValue(protoRef, out var list))
            {
                foreach (var entry in list)
                {
                    if (entry.Value.ValueType == ValueType.Properties)
                    {
                        prop = entry.Value.GetValue() as PropertyCollection;
                        if (prop != null)
                        {
                            entry.Patched = true;
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        public bool PreCheck(PrototypeId protoRef)
        {
            if (!_initialized) return false;
            return _patchDict.ContainsKey(protoRef);
        }

        public void PostOverride(Prototype prototype)
        {
            if (!_initialized || prototype == null) return;

            var currentId = prototype.DataRef;
            _postProcessedPrototypes.Add(currentId);

            if (_patchDict.ContainsKey(currentId))
            {
                _deferredPrototypes[currentId] = prototype;
            }

            ProcessDeferredQueue();
        }

        private void ProcessDeferredQueue()
        {
            bool progressMade;
            do
            {
                progressMade = false;
                var readyToPatch = _deferredPrototypes.Keys
                    .Where(id => IsReadyToPatch(_deferredPrototypes[id]))
                    .ToList();

                foreach (var idToPatch in readyToPatch)
                {
                    var protoToPatch = _deferredPrototypes[idToPatch];

                    ForceUpdateFromParent(protoToPatch);

                    if (_patchDict.TryGetValue(idToPatch, out var entries))
                    {
                        foreach (var entry in entries.Where(e => !e.Patched).OrderBy(e => e.Path.Count(c => c == '.')))
                        {
                            try
                            {
                                ApplyPatch(protoToPatch, entry);
                            }
                            catch (Exception ex)
                            {
                                Logger.ErrorException(ex, $"Failed to apply patch [{entry.Prototype}] -> [{entry.Path}]");
                            }
                        }
                    }

                    _deferredPrototypes.Remove(idToPatch);
                    progressMade = true;
                }
            } while (progressMade);
        }

        private bool IsReadyToPatch(Prototype proto)
        {
            var parentId = proto.ParentDataRef;
            return parentId == PrototypeId.Invalid || _postProcessedPrototypes.Contains(parentId);
        }

        private void ForceUpdateFromParent(Prototype child)
        {
            if (child.ParentDataRef == PrototypeId.Invalid) return;
            var parent = GameDatabase.GetPrototype<Prototype>(child.ParentDataRef);
            if (parent == null) return;

            var parentProperties = parent.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
            var childType = child.GetType();

            foreach (var parentProp in parentProperties)
            {
                if (parentProp.CanRead)
                {
                    var childProp = childType.GetProperty(parentProp.Name, BindingFlags.Public | BindingFlags.Instance);
                    if (childProp != null && childProp.CanWrite && childProp.PropertyType == parentProp.PropertyType && childProp.DeclaringType.IsAssignableFrom(parent.GetType()))
                    {
                        childProp.SetValue(child, parentProp.GetValue(parent));
                    }
                }
            }
        }

        private void ApplyPatch(object currentObject, PrototypePatchEntry entry)
        {
            string[] pathParts = entry.Path.Split('.');
            for (int i = 0; i < pathParts.Length - 1; i++)
            {
                currentObject = GetObjectFromPathPart(currentObject, pathParts[i]);
                if (currentObject == null) return;
            }
            UpdateValue(currentObject, pathParts[^1], entry);
        }

        private object GetObjectFromPathPart(object source, string part)
        {
            if (source == null) return null;

            if (part.EndsWith("]"))
            {
                int bracketIndex = part.IndexOf('[');
                string propName = part.Substring(0, bracketIndex);
                int index = int.Parse(part.Substring(bracketIndex + 1, part.Length - bracketIndex - 2));
                var propInfo = source.GetType().GetProperty(propName);
                if (propInfo?.GetValue(source) is not Array array || index >= array.Length) return null;
                return array.GetValue(index);
            }
            else
            {
                return source.GetType().GetProperty(part)?.GetValue(source);
            }
        }

        private void UpdateValue(object targetObject, string fieldName, PrototypePatchEntry entry)
        {
            if (targetObject == null) return;

            var propInfo = targetObject.GetType().GetProperty(entry.FieldName);
            if (propInfo == null) return;

            if (entry.ArrayValue)
            {
                if (entry.ArrayIndex == -1) InsertValue(targetObject, propInfo, entry.Value);
                else SetIndexValue(targetObject, propInfo, entry.ArrayIndex, entry.Value);
            }
            else
            {
                propInfo.SetValue(targetObject, ConvertValue(entry.Value.GetValue(), propInfo.PropertyType));
            }
            entry.Patched = true;
            Logger.Trace($"Patch Applied: {entry.Prototype} -> {entry.Path} = {entry.Value.GetValue()}");
        }

        public void SetPath(Prototype parent, Prototype child, string fieldName)
        {
            if (_instancePathMap.TryGetValue(parent, out var parentPath))
            {
                _instancePathMap[child] = string.IsNullOrEmpty(parentPath) ? fieldName : $"{parentPath}.{fieldName}";
            }
        }

        public void SetPathIndex(Prototype parent, Prototype child, string fieldName, int index)
        {
            if (_instancePathMap.TryGetValue(parent, out var parentPath))
            {
                _instancePathMap[child] = string.IsNullOrEmpty(parentPath) ? $"{fieldName}[{index}]" : $"{parentPath}.{fieldName}[{index}]";
            }
        }

        private static void SetIndexValue(object target, System.Reflection.PropertyInfo fieldInfo, int index, ValueBase value)
        {
            if (fieldInfo.GetValue(target) is not Array array || index < 0 || index >= array.Length) return;
            Type elementType = fieldInfo.PropertyType.GetElementType();
            object valueEntry = value.GetValue();
            if (elementType == null || !IsTypeCompatible(elementType, valueEntry, value.ValueType))
                throw new InvalidOperationException($"Type {value.ValueType} is not assignable to {elementType.Name}.");
            array.SetValue(ConvertValue(valueEntry, elementType), index);
        }

        private static void InsertValue(object target, System.Reflection.PropertyInfo fieldInfo, ValueBase value)
        {
            if (!fieldInfo.PropertyType.IsArray) throw new InvalidOperationException($"Field {fieldInfo.Name} is not an array.");
            Type elementType = fieldInfo.PropertyType.GetElementType();
            var valueEntry = value.GetValue();
            if (elementType == null || !IsTypeCompatible(elementType, valueEntry, value.ValueType))
                throw new InvalidOperationException($"Type {value.ValueType} is not assignable for {elementType.Name}.");
            var currentArray = (Array)fieldInfo.GetValue(target);
            var newArray = Array.CreateInstance(elementType, (currentArray?.Length ?? 0) + 1);
            if (currentArray != null) Array.Copy(currentArray, newArray, currentArray.Length);
            newArray.SetValue(GetElementValue(valueEntry, elementType), newArray.Length - 1);
            fieldInfo.SetValue(target, newArray);
        }

        private static bool IsTypeCompatible(Type baseType, object rawValue, ValueType valueType)
        {
            if (rawValue is Array rawArray)
            {
                var elementValueType = valueType.ToString().EndsWith("Array") ? (ValueType)Enum.Parse(typeof(ValueType), valueType.ToString().Replace("Array", "")) : valueType;
                return rawArray.Cast<object>().All(element => IsTypeCompatible(baseType, element, elementValueType));
            }
            if (typeof(Prototype).IsAssignableFrom(baseType))
            {
                if (rawValue is PrototypeId dataRef)
                {
                    var actualPrototype = GameDatabase.GetPrototype<Prototype>(dataRef);
                    return actualPrototype != null && baseType.IsAssignableFrom(actualPrototype.GetType());
                }
                if (rawValue is JsonElement jsonElement && (valueType == ValueType.Prototype || valueType == ValueType.ComplexObject))
                {
                    if (jsonElement.TryGetProperty("ParentDataRef", out var parentDataRefElement) && parentDataRefElement.TryGetUInt64(out var id))
                    {
                        Type classType = GameDatabase.DataDirectory.GetPrototypeClassType((PrototypeId)id);
                        return classType != null && baseType.IsAssignableFrom(classType);
                    }
                    return false;
                }
            }
            return baseType.IsAssignableFrom(rawValue.GetType());
        }

        private static object GetElementValue(object valueEntry, Type elementType)
        {
            if (typeof(Prototype).IsAssignableFrom(elementType) && valueEntry is PrototypeId dataRef)
                return GameDatabase.GetPrototype<Prototype>(dataRef) ?? throw new InvalidOperationException($"DataRef {dataRef} is not a valid Prototype.");
            return ConvertValue(valueEntry, elementType);
        }

        public static object ConvertValue(object rawValue, Type targetType)
        {
            if (rawValue == null || (rawValue is JsonElement jsonVal && jsonVal.ValueKind == JsonValueKind.Null)) return null;
            if (targetType.IsInstanceOfType(rawValue)) return rawValue;
            if (targetType == typeof(AssetId) && rawValue is string assetString)
            {
                int typeNameStart = assetString.LastIndexOf('(');
                int typeNameEnd = assetString.LastIndexOf(')');
                if (typeNameStart != -1 && typeNameEnd > typeNameStart)
                {
                    string assetName = assetString.Substring(0, typeNameStart).Trim();
                    string assetTypeName = assetString.Substring(typeNameStart + 1, typeNameEnd - typeNameStart - 1).Trim();
                    var assetType = GameDatabase.DataDirectory.AssetDirectory.GetAssetType(assetTypeName);
                    if (assetType != null)
                    {
                        var assetId = assetType.FindAssetByName(assetName, DataFileSearchFlags.CaseInsensitive);
                        if (assetId != AssetId.Invalid) return assetId;
                    }
                }
            }
            if (typeof(Prototype).IsAssignableFrom(targetType) && rawValue is JsonElement prototypeJson)
                return PatchEntryConverter.ParseJsonPrototype(prototypeJson);
            if (targetType.IsEnum && rawValue is string enumString)
                return Enum.Parse(targetType, enumString, true);
            TypeConverter converter = TypeDescriptor.GetConverter(targetType);
            if (converter != null && converter.CanConvertFrom(rawValue.GetType()))
                return converter.ConvertFrom(rawValue);
            return Convert.ChangeType(rawValue, targetType);
        }
    }
}

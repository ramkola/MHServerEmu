
using MHServerEmu.Core.Helpers;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Memory;
using MHServerEmu.Games.GameData.Calligraphy;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Properties;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace MHServerEmu.Games.GameData.PatchManager
{
    public class PrototypePatchManager
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        
        private readonly Dictionary<PrototypeId, List<PrototypePatchEntry>> _patchDict = new();

        private bool _initialized = false;

        public static PrototypePatchManager Instance { get; } = new();

        public void Initialize(bool enablePatchManager)
        {
            if (enablePatchManager)
            {
               
                _initialized = LoadPatchDataFromDisk();

             
                if (_patchDict.Count > 0)
                {
                    Task.Run(() => ApplyAllPatches());
                }
            }
        }

        private bool LoadPatchDataFromDisk()
        {
            string patchDirectory = Path.Combine(FileHelper.DataDirectory, "Game", "Patches");
            if (Directory.Exists(patchDirectory) == false)
                return Logger.WarnReturn(false, "LoadPatchDataFromDisk(): Game data directory not found");

            int count = 0;
            var options = new JsonSerializerOptions { Converters = { new PatchEntryConverter() } };

            List<string> patchFiles = ListPool<string>.Instance.Get();
            try
            {
                foreach (string filePath in FileHelper.GetFilesWithPrefix(patchDirectory, "PatchData", "json"))
                {
                    patchFiles.Add(filePath);
                }

                foreach (string filePath in patchFiles)
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
                        if (prototypeId == PrototypeId.Invalid)
                        {
                            Logger.Warn($"Could not find prototype ID for '{value.Prototype}' in patch '{value.Description}'. Skipping.");
                            continue;
                        }
                        AddPatchValue(prototypeId, value);
                        count++;
                    }

                    Logger.Trace($"Parsed patch data from {fileName}");
                }
            }
            finally
            {
                ListPool<string>.Instance.Return(patchFiles);
            }

            return Logger.InfoReturn(true, $"Loaded {count} patches to be applied after initialization.");
        }

        // This method now applies ALL patches after the database is initialized.
        private void ApplyAllPatches()
        {
            // This is the "smart timer" that waits for the database to be fully loaded.
            while (!GameDatabase.IsInitialized)
            {
                Task.Delay(100).Wait();
            }

            Logger.Info("GameDatabase is initialized. Applying all patches...");
            foreach (var kvp in _patchDict)
            {
                PrototypeId prototypeId = kvp.Key;
                var patches = kvp.Value;

                var prototype = GameDatabase.GetPrototype<Prototype>(prototypeId);
                if (prototype == null)
                {
                    Logger.Warn($"Could not find prototype '{prototypeId.GetName()}' to apply patches.");
                    continue;
                }

                foreach (var entry in patches)
                {
                    try
                    {
                        ApplyPatchToTarget(entry, prototype);
                    }
                    catch (Exception ex)
                    {
                        Logger.ErrorException(ex, $"Failed to apply patch: [{entry.Prototype}] [{entry.Path}] {ex.Message}");
                    }
                }
            }
            Logger.Info("Finished applying all patches.");
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

        
        public bool PreCheck(PrototypeId protoRef) => false;
        public void PostOverride(Prototype prototype) { }
        public void SetPath(Prototype parent, Prototype child, string fieldName) { }
        public void SetPathIndex(Prototype parent, Prototype child, string fieldName, int index) { }

       
        public bool CheckProperties(PrototypeId protoRef, out PropertyCollection prop)
        {
            prop = null;
            if (!_initialized) return false;

            if (protoRef != PrototypeId.Invalid && _patchDict.TryGetValue(protoRef, out var list))
            {
                foreach (var entry in list)
                {
                    if (entry.Value.ValueType == ValueType.Properties)
                    {
                        prop = entry.Value.GetValue() as PropertyCollection;
                        return prop != null;
                    }
                }
            }
            return false;
        }

        private static bool ApplyPatchToTarget(PrototypePatchEntry entry, Prototype prototype)
        {
            if (entry.PathSegments.Count == 0) return false;

            bool success = ApplyNestedPatch(entry, prototype);

            if (success)
            {
                Logger.Trace($"Patch Prototype: {entry.Prototype} {entry.Path} = {entry.Value.GetValue()} (Operation: {entry.Operation})");
                entry.Patched = true;
            }

            return success;
        }

        private static bool ApplyNestedPatch(PrototypePatchEntry entry, Prototype prototype)
        {
            object currentObject = prototype;

            for (int i = 0; i < entry.PathSegments.Count - 1; i++)
            {
                var segment = entry.PathSegments[i];
                currentObject = NavigateToSegment(currentObject, segment);
                if (currentObject == null) return false;
            }

            var finalSegment = entry.PathSegments.Last();
            return ApplyOperationToSegment(currentObject, finalSegment, entry);
        }

        private static object NavigateToSegment(object obj, PathSegment segment)
        {
            var fieldInfo = obj.GetType().GetProperty(segment.FieldName);
            if (fieldInfo == null) return null;

            object value = fieldInfo.GetValue(obj);
            if (value == null) return null;

            foreach (int index in segment.ArrayIndices)
            {
                if (value is not Array array) return null;
                if (index < 0 || index >= array.Length) return null;
                value = array.GetValue(index);
                if (value == null) return null;
            }

            return value;
        }

        private static bool ApplyOperationToSegment(object targetObject, PathSegment segment, PrototypePatchEntry entry)
        {
            System.Reflection.PropertyInfo fieldInfo = targetObject.GetType().GetProperty(segment.FieldName);
            if (fieldInfo == null) return false;

            switch (entry.Operation)
            {
                case PatchOperation.Set:
                    return HandleSetOperation(targetObject, fieldInfo, segment, entry);
                case PatchOperation.Add:
                    return HandleAddOperation(targetObject, fieldInfo, segment, entry);
                case PatchOperation.Insert:
                    return HandleInsertOperation(targetObject, fieldInfo, segment, entry);
                case PatchOperation.Remove:
                    return HandleRemoveOperation(targetObject, fieldInfo, segment, entry);
                case PatchOperation.Replace:
                    return HandleReplaceOperation(targetObject, fieldInfo, segment, entry);
                default:
                    throw new NotSupportedException($"Operation {entry.Operation} not supported");
            }
        }

        private static bool HandleSetOperation(object targetObject, System.Reflection.PropertyInfo fieldInfo, PathSegment segment, PrototypePatchEntry entry)
        {
            if (segment.IsArray)
            {
                return SetNestedArrayValue(targetObject, fieldInfo, segment.ArrayIndices, entry.Value);
            }
            else
            {
                object valueToSet = ConvertValue(entry.Value.GetValue(), fieldInfo.PropertyType);
                fieldInfo.SetValue(targetObject, valueToSet);
                return true;
            }
        }

        private static bool HandleAddOperation(object targetObject, System.Reflection.PropertyInfo fieldInfo, PathSegment segment, PrototypePatchEntry entry)
        {
            if (!fieldInfo.PropertyType.IsArray)
                throw new InvalidOperationException($"Add operation can only be used on array fields. Field {segment.FieldName} is not an array.");

            Array currentArray = (Array)fieldInfo.GetValue(targetObject);
            Type elementType = fieldInfo.PropertyType.GetElementType();

            object valueToAdd = entry.Value.GetValue();
            int elementsToAdd = valueToAdd is Array addArray ? addArray.Length : 1;
            int currentLength = currentArray?.Length ?? 0;
            int newLength = currentLength + elementsToAdd;

            Array newArray = Array.CreateInstance(elementType, newLength);

            if (currentArray != null)
                Array.Copy(currentArray, newArray, currentLength);

            AddElementsToArray(newArray, elementType, valueToAdd, currentLength);

            fieldInfo.SetValue(targetObject, newArray);
            return true;
        }

        private static bool HandleInsertOperation(object targetObject, System.Reflection.PropertyInfo fieldInfo, PathSegment segment, PrototypePatchEntry entry)
        {
            if (!fieldInfo.PropertyType.IsArray)
                throw new InvalidOperationException($"Insert operation can only be used on array fields. Field {segment.FieldName} is not an array.");

            if (!segment.IsArray || segment.ArrayIndices.Count != 1)
                throw new InvalidOperationException("Insert operation requires exactly one array index to specify insertion point.");

            Array currentArray = (Array)fieldInfo.GetValue(targetObject);
            Type elementType = fieldInfo.PropertyType.GetElementType();
            int insertIndex = segment.ArrayIndices[0];

            if (insertIndex < 0 || insertIndex > (currentArray?.Length ?? 0))
                throw new IndexOutOfRangeException($"Insert index {insertIndex} is out of range.");

            object valueToInsert = entry.Value.GetValue();
            int elementsToInsert = valueToInsert is Array insertArray ? insertArray.Length : 1;
            int currentLength = currentArray?.Length ?? 0;
            int newLength = currentLength + elementsToInsert;

            Array newArray = Array.CreateInstance(elementType, newLength);

            if (currentArray != null)
            {
                if (insertIndex > 0)
                    Array.Copy(currentArray, 0, newArray, 0, insertIndex);

                if (insertIndex < currentLength)
                    Array.Copy(currentArray, insertIndex, newArray, insertIndex + elementsToInsert, currentLength - insertIndex);
            }

            AddElementsToArray(newArray, elementType, valueToInsert, insertIndex);

            fieldInfo.SetValue(targetObject, newArray);
            return true;
        }

        private static bool HandleRemoveOperation(object targetObject, System.Reflection.PropertyInfo fieldInfo, PathSegment segment, PrototypePatchEntry entry)
        {
            if (!fieldInfo.PropertyType.IsArray)
                throw new InvalidOperationException($"Remove operation can only be used on array fields. Field {segment.FieldName} is not an array.");

            Array currentArray = (Array)fieldInfo.GetValue(targetObject);
            if (currentArray == null || currentArray.Length == 0) return true;

            Type elementType = fieldInfo.PropertyType.GetElementType();

            if (segment.IsArray && segment.ArrayIndices.Count == 1)
            {
                int removeIndex = segment.ArrayIndices[0];
                if (removeIndex < 0 || removeIndex >= currentArray.Length)
                    throw new IndexOutOfRangeException($"Remove index {removeIndex} is out of range.");

                Array newArray = Array.CreateInstance(elementType, currentArray.Length - 1);
                int newIndex = 0;

                for (int i = 0; i < currentArray.Length; i++)
                {
                    if (i != removeIndex)
                    {
                        newArray.SetValue(currentArray.GetValue(i), newIndex++);
                    }
                }

                fieldInfo.SetValue(targetObject, newArray);
            }
            else
            {
                List<object> tempList = ListPool<object>.Instance.Get();
                try
                {
                    object valueToRemove = ConvertValue(entry.Value.GetValue(), elementType);

                    for (int i = 0; i < currentArray.Length; i++)
                    {
                        object element = currentArray.GetValue(i);
                        if (!Equals(element, valueToRemove))
                            tempList.Add(element);
                    }

                    Array newArray = Array.CreateInstance(elementType, tempList.Count);
                    for (int i = 0; i < tempList.Count; i++)
                        newArray.SetValue(tempList[i], i);

                    fieldInfo.SetValue(targetObject, newArray);
                }
                finally
                {
                    ListPool<object>.Instance.Return(tempList);
                }
            }

            return true;
        }

        private static bool HandleReplaceOperation(object targetObject, System.Reflection.PropertyInfo fieldInfo, PathSegment segment, PrototypePatchEntry entry)
        {
            if (!segment.IsArray)
                throw new InvalidOperationException("Replace operation requires array indices to specify what to replace.");

            return SetNestedArrayValue(targetObject, fieldInfo, segment.ArrayIndices, entry.Value);
        }

        private static bool SetNestedArrayValue(object targetObject, System.Reflection.PropertyInfo fieldInfo, List<int> indices, ValueBase value)
        {
            Array array = (Array)fieldInfo.GetValue(targetObject);
            if (array == null) return false;

            object currentElement = array;

            for (int i = 0; i < indices.Count - 1; i++)
            {
                int index = indices[i];
                if (currentElement is not Array currentArray) return false;
                if (index < 0 || index >= currentArray.Length) return false;
                currentElement = currentArray.GetValue(index);
                if (currentElement == null) return false;
            }

            if (currentElement is Array finalArray)
            {
                int finalIndex = indices.Last();
                if (finalIndex < 0 || finalIndex >= finalArray.Length) return false;

                Type elementType = finalArray.GetType().GetElementType();

               
                object elementValue = GetElementValue(value.GetValue(), elementType);
                finalArray.SetValue(elementValue, finalIndex);
                return true;
            }

            return false;
        }

        private static void AddElementsToArray(Array targetArray, Type elementType, object valueToAdd, int startIndex)
        {
            if (valueToAdd is Array sourceArray)
            {
                for (int i = 0; i < sourceArray.Length; i++)
                {
                    object elementValue = GetElementValue(sourceArray.GetValue(i), elementType);
                    targetArray.SetValue(elementValue, startIndex + i);
                }
            }
            else
            {
                object elementValue = GetElementValue(valueToAdd, elementType);
                targetArray.SetValue(elementValue, startIndex);
            }
        }

        public static object ConvertValue(object rawValue, Type targetType)
        {
            if (rawValue == null || targetType.IsInstanceOfType(rawValue))
                return rawValue;

            if (rawValue is string stringValue)
            {
                if (targetType == typeof(PrototypeId))
                {
                    var resolvedId = GameDatabase.GetPrototypeRefByName(stringValue);
                    if (resolvedId != PrototypeId.Invalid)
                    {
                        Logger.Trace($"Successfully resolved prototype name '{stringValue}' to PrototypeId: {resolvedId}");
                        return resolvedId;
                    }
                    Logger.Warn($"Could not resolve prototype name '{stringValue}' to a PrototypeId.");
                }

                if (targetType == typeof(AssetId))
                {
                    Logger.Trace($"Attempting to resolve asset name '{stringValue}' to AssetId");
                    var searchFlags = new[] { DataFileSearchFlags.None, DataFileSearchFlags.CaseInsensitive };
                    foreach (var flags in searchFlags)
                    {
                        foreach (var assetType in AssetDirectory.Instance.IterateAssetTypes())
                        {
                            var assetId = assetType.FindAssetByName(stringValue, flags);
                            if (assetId != AssetId.Invalid)
                            {
                                Logger.Trace($"Successfully resolved asset name '{stringValue}' to AssetId: {assetId} in AssetType: {assetType}");
                                return assetId;
                            }
                        }
                    }
                    Logger.Warn($"Could not find an asset with the name '{stringValue}' in any loaded AssetType. Available asset types: {string.Join(", ", AssetDirectory.Instance.IterateAssetTypes().Select(at => at.ToString()))}");
                }
            }

            if (rawValue is Array sourceArray && targetType.IsArray)
            {
                var targetElementType = targetType.GetElementType();
                var newArray = Array.CreateInstance(targetElementType, sourceArray.Length);
                try
                {
                    for (int i = 0; i < sourceArray.Length; i++)
                    {
                        newArray.SetValue(sourceArray.GetValue(i), i);
                    }
                    Logger.Trace($"Successfully converted array from {sourceArray.GetType().Name} to {newArray.GetType().Name} via element-wise copy.");
                    return newArray;
                }
                catch (InvalidCastException)
                {
                    Logger.Warn($"Failed to convert array from {sourceArray.GetType().Name} to {newArray.GetType().Name} due to an invalid element cast.");
                }
            }

            try
            {
                TypeConverter converter = TypeDescriptor.GetConverter(targetType);
                if (converter != null && converter.CanConvertFrom(rawValue.GetType()))
                {
                    var convertedValue = converter.ConvertFrom(rawValue);
                    Logger.Trace($"Successfully converted '{rawValue}' to {targetType.Name} using TypeConverter");
                    return convertedValue;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"TypeConverter failed to convert '{rawValue}' to {targetType.Name}: {ex.Message}");
            }

            try
            {
                var convertedValue = Convert.ChangeType(rawValue, targetType);
                Logger.Trace($"Successfully converted '{rawValue}' to {targetType.Name} using Convert.ChangeType");
                return convertedValue;
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to convert '{rawValue}' to {targetType.Name}: {ex.Message}");
                throw;
            }
        }

        private static object GetElementValue(object valueEntry, Type elementType)
        {
            if (elementType.IsClass && valueEntry is PrototypeId dataRef)
            {
                var prototype = GameDatabase.GetPrototype<Prototype>(dataRef)
                    ?? throw new InvalidOperationException($"DataRef {dataRef} is not Prototype.");
                valueEntry = prototype;
            }
            else if (valueEntry is Prototype proto)
            {
                if (!elementType.IsInstanceOfType(proto))
                {
                    return ConvertValue(proto, elementType);
                }
                return proto;
            }

            return ConvertValue(valueEntry, elementType);
        }
    }
}

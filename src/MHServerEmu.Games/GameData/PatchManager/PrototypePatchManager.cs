using MHServerEmu.Core.Helpers;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Memory;
using MHServerEmu.Games.GameData.Prototypes;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json;

namespace MHServerEmu.Games.GameData.PatchManager
{
    public class PrototypePatchManager
    {
        private static readonly Logger Logger = LogManager.CreateLogger();
        private Stack<PrototypeId> _protoStack = new();
        private readonly Dictionary<PrototypeId, List<PrototypePatchEntry>> _patchDict = new();
        private Dictionary<Prototype, string> _pathDict = new();
        private bool _initialized = false;

        public static PrototypePatchManager Instance { get; } = new();

        public void Initialize(bool enablePatchManager)
        {
            if (enablePatchManager) _initialized = LoadPatchDataFromDisk();
        }

        private bool LoadPatchDataFromDisk()
        {
            string patchDirectory = Path.Combine(FileHelper.DataDirectory, "Game", "Patches");
            if (Directory.Exists(patchDirectory) == false)
                return Logger.WarnReturn(false, "LoadPatchDataFromDisk(): Game data directory not found");

            int count = 0;
            var options = new JsonSerializerOptions { Converters = { new PatchEntryConverter() } };

            // Use pooled list for file paths to avoid allocation
            List<string> patchFiles = ListPool<string>.Instance.Get();
            try
            {
                // Read all .json files that start with PatchData
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
                        if (prototypeId == PrototypeId.Invalid) continue;
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

            foreach (var entry in list)
                if (entry.Patched == false)
                    CheckAndUpdate(entry, prototype, currentPath);

            if (_protoStack.Count == 0)
                _pathDict.Clear();
        }

        private static bool CheckAndUpdate(PrototypePatchEntry entry, Prototype prototype, string currentPath)
        {
            try
            {
                return ApplyPatchToTarget(entry, prototype, currentPath);
            }
            catch (Exception ex)
            {
                Logger.WarnException(ex, $"Failed to apply patch: [{entry.Prototype}] [{entry.Path}] {ex.Message}");
                return false;
            }
        }

        private static bool ApplyPatchToTarget(PrototypePatchEntry entry, Prototype prototype, string currentPath)
        {
            if (currentPath.StartsWith('.')) currentPath = currentPath[1..];

            // Check if this is the right object for this patch
            if (entry.PathSegments.Count == 0) return false;

            // Use pooled list for building expected path
            List<string> pathSegments = ListPool<string>.Instance.Get();
            try
            {
                for (int i = 0; i < entry.PathSegments.Count - 1; i++)
                {
                    pathSegments.Add(entry.PathSegments[i].FieldName);
                }

                string expectedPath = string.Join(".", pathSegments);
                if (expectedPath != currentPath) return false;

                // Apply the patch using the new nested path system
                bool success = ApplyNestedPatch(entry, prototype);

                if (success)
                {
                    Logger.Trace($"Patch Prototype: {entry.Prototype} {entry.Path} = {entry.Value.GetValue()} (Operation: {entry.Operation})");
                    entry.Patched = true;
                }

                return success;
            }
            finally
            {
                ListPool<string>.Instance.Return(pathSegments);
            }
        }

        private static bool ApplyNestedPatch(PrototypePatchEntry entry, Prototype prototype)
        {
            object currentObject = prototype;

            // Navigate to the target object/array
            for (int i = 0; i < entry.PathSegments.Count - 1; i++)
            {
                var segment = entry.PathSegments[i];
                currentObject = NavigateToSegment(currentObject, segment);
                if (currentObject == null) return false;
            }

            // Apply the operation to the final segment
            var finalSegment = entry.PathSegments.Last();
            return ApplyOperationToSegment(currentObject, finalSegment, entry);
        }

        private static object NavigateToSegment(object obj, PathSegment segment)
        {
            var fieldInfo = obj.GetType().GetProperty(segment.FieldName);
            if (fieldInfo == null) return null;

            object value = fieldInfo.GetValue(obj);
            if (value == null) return null;

            // Navigate through array dimensions if needed
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
            var fieldInfo = targetObject.GetType().GetProperty(segment.FieldName);
            if (fieldInfo == null) return false;

            Type fieldType = fieldInfo.PropertyType;
            object currentValue = fieldInfo.GetValue(targetObject);

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

        private static bool HandleSetOperation(object targetObject, PropertyInfo fieldInfo, PathSegment segment, PrototypePatchEntry entry)
        {
            Type fieldType = fieldInfo.PropertyType;

            if (segment.IsArray)
            {
                // Setting array element at specific indices (including nested arrays)
                return SetNestedArrayValue(targetObject, fieldInfo, segment.ArrayIndices, entry.Value);
            }
            else
            {
                // Setting field value directly
                object convertedValue = ConvertValue(entry.Value.GetValue(), fieldType);
                fieldInfo.SetValue(targetObject, convertedValue);
                return true;
            }
        }

        private static bool HandleAddOperation(object targetObject, PropertyInfo fieldInfo, PathSegment segment, PrototypePatchEntry entry)
        {
            if (!fieldInfo.PropertyType.IsArray)
                throw new InvalidOperationException($"Add operation can only be used on array fields. Field {segment.FieldName} is not an array.");

            Array currentArray = (Array)fieldInfo.GetValue(targetObject);
            Type elementType = fieldInfo.PropertyType.GetElementType();

            // Calculate new array size
            object valueToAdd = entry.Value.GetValue();
            int elementsToAdd = valueToAdd is Array addArray ? addArray.Length : 1;
            int currentLength = currentArray?.Length ?? 0;
            int newLength = currentLength + elementsToAdd;

            // Create new array
            Array newArray = Array.CreateInstance(elementType, newLength);

            // Copy existing elements
            if (currentArray != null)
                Array.Copy(currentArray, newArray, currentLength);

            // Add new elements
            AddElementsToArray(newArray, elementType, valueToAdd, currentLength);

            fieldInfo.SetValue(targetObject, newArray);
            return true;
        }

        private static bool HandleInsertOperation(object targetObject, PropertyInfo fieldInfo, PathSegment segment, PrototypePatchEntry entry)
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

            // Calculate new array size
            object valueToInsert = entry.Value.GetValue();
            int elementsToInsert = valueToInsert is Array insertArray ? insertArray.Length : 1;
            int currentLength = currentArray?.Length ?? 0;
            int newLength = currentLength + elementsToInsert;

            // Create new array and copy elements
            Array newArray = Array.CreateInstance(elementType, newLength);

            if (currentArray != null)
            {
                // Copy elements before insertion point
                if (insertIndex > 0)
                    Array.Copy(currentArray, 0, newArray, 0, insertIndex);

                // Copy elements after insertion point
                if (insertIndex < currentLength)
                    Array.Copy(currentArray, insertIndex, newArray, insertIndex + elementsToInsert, currentLength - insertIndex);
            }

            // Insert new elements
            AddElementsToArray(newArray, elementType, valueToInsert, insertIndex);

            fieldInfo.SetValue(targetObject, newArray);
            return true;
        }

        private static bool HandleRemoveOperation(object targetObject, PropertyInfo fieldInfo, PathSegment segment, PrototypePatchEntry entry)
        {
            if (!fieldInfo.PropertyType.IsArray)
                throw new InvalidOperationException($"Remove operation can only be used on array fields. Field {segment.FieldName} is not an array.");

            Array currentArray = (Array)fieldInfo.GetValue(targetObject);
            if (currentArray == null || currentArray.Length == 0) return true;

            Type elementType = fieldInfo.PropertyType.GetElementType();

            if (segment.IsArray && segment.ArrayIndices.Count == 1)
            {
                // Remove by index
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
                // Remove by value - use pooled list instead of creating new List<object>
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

        private static bool HandleReplaceOperation(object targetObject, PropertyInfo fieldInfo, PathSegment segment, PrototypePatchEntry entry)
        {
            if (!segment.IsArray)
                throw new InvalidOperationException("Replace operation requires array indices to specify what to replace.");

            return SetNestedArrayValue(targetObject, fieldInfo, segment.ArrayIndices, entry.Value);
        }

        private static bool SetNestedArrayValue(object targetObject, PropertyInfo fieldInfo, List<int> indices, ValueBase value)
        {
            Array array = (Array)fieldInfo.GetValue(targetObject);
            if (array == null) return false;

            // Navigate to the target array element through nested dimensions
            object currentElement = array;

            for (int i = 0; i < indices.Count - 1; i++)
            {
                int index = indices[i];
                if (currentElement is not Array currentArray) return false;
                if (index < 0 || index >= currentArray.Length) return false;
                currentElement = currentArray.GetValue(index);
                if (currentElement == null) return false;
            }

            // Set the final value
            if (currentElement is Array finalArray)
            {
                int finalIndex = indices.Last();
                if (finalIndex < 0 || finalIndex >= finalArray.Length) return false;

                Type elementType = finalArray.GetType().GetElementType();
                object convertedValue = ConvertValue(value.GetValue(), elementType);
                finalArray.SetValue(convertedValue, finalIndex);
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
            if (targetType.IsInstanceOfType(rawValue))
                return rawValue;

            TypeConverter converter = TypeDescriptor.GetConverter(targetType);
            if (converter != null && converter.CanConvertFrom(rawValue.GetType()))
                return converter.ConvertFrom(rawValue);

            return Convert.ChangeType(rawValue, targetType);
        }

        private static object GetElementValue(object valueEntry, Type elementType)
        {
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
    }
}
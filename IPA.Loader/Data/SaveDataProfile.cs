using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Newtonsoft.Json.Linq;

namespace IPA.Data
{
    internal sealed class SaveDataProfile
    {
        internal string Version;
        internal string Era;
        internal bool Ini;
        internal readonly Dictionary<string, DataShape> Files = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, DataShape> IniFields = new(StringComparer.Ordinal);

        internal static SaveDataProfile Read(IEnumerable<ModuleDefinition> modules, string version)
        {
            var profile = new SaveDataProfile { Version = version };
            var scoring = new StringBuilder();
            foreach (var module in modules)
            foreach (var type in module.GetTypes())
            {
                string file = type.FullName == "GameDynamicData" ? "GameDynamicData.dat"
                    : type.FullName == "PlayerSaveData" ? "PlayerData.dat"
                    : type.FullName == "MainSettingsModel/Config" || type.FullName == "MainSettingsModelSO/Config" ? "settings.cfg" : null;
                if (file != null) profile.Files[file] = DataShape.Read(type, new HashSet<string>(), 0);
                if (type.Name == "ScoreModel" || type.Name == "NoteScoreDefinition" || type.Name == "ScoringElement")
                {
                    scoring.Append(type.FullName);
                    foreach (var field in type.Fields) scoring.Append(field.FullName).Append(field.HasConstant ? field.Constant : null);
                    foreach (var method in type.Methods.Where(m => m.HasBody))
                    {
                        scoring.Append(method.FullName);
                        foreach (var instruction in method.Body.Instructions)
                        {
                            scoring.Append(instruction.OpCode.Name);
                            if (instruction.Operand is MethodReference call) scoring.Append(call.FullName);
                            else if (instruction.Operand is FieldReference field) scoring.Append(field.FullName);
                            else if (instruction.Operand is string || instruction.Operand is IConvertible)
                                scoring.Append(Convert.ToString(instruction.Operand, CultureInfo.InvariantCulture));
                        }
                    }
                }
                foreach (var method in type.Methods.Where(m => m.HasBody))
                {
                    if (method.Body.Instructions.Any(i => i.OpCode.Code == Code.Ldstr && Equals(i.Operand, "settings.ini"))) profile.Ini = true;
                    if (type.Name == "SettingStrings" && method.Name == "WriteProperties")
                    {
                        string key = null; TypeReference valueType = null;
                        foreach (var instruction in method.Body.Instructions)
                        {
                            if (instruction.OpCode.Code == Code.Ldstr) { key = instruction.Operand as string; valueType = null; }
                            if (instruction.Operand is FieldReference field) valueType = field.FieldType;
                            if (instruction.Operand is MethodReference call && call.Name == "AppendProperty" && key != null && valueType != null)
                            {
                                profile.IniFields[key] = DataShape.Read(valueType, new HashSet<string>(), 0); key = null;
                            }
                        }
                    }
                }
            }
            // Include the score record layout, so equal scoring code cannot combine different save shapes.
            foreach (var file in profile.Files.Where(p => p.Key.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)))
                scoring.Append(file.Key).Append(file.Value.Signature());
            if (scoring.Length == 0) scoring.Append(version);
            using var hash = SHA256.Create();
            profile.Era = "score-" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(scoring.ToString()))).Replace("-", "").Substring(0, 16).ToLowerInvariant();
            return profile;
        }
    }

    internal sealed class DataShape
    {
        internal string Kind;
        internal string DefaultString;
        internal DataShape Element;
        internal readonly Dictionary<string, DataShape> Fields = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, long> EnumValues = new(StringComparer.OrdinalIgnoreCase);

        internal static DataShape Read(TypeReference reference, HashSet<string> parents, int depth)
        {
            if (depth > 18) return new DataShape { Kind = "opaque" };
            if (reference is ArrayType array) return new DataShape { Kind = "array", Element = Read(array.ElementType, parents, depth + 1) };
            if (reference is GenericInstanceType generic && generic.ElementType.FullName == "System.Collections.Generic.List`1")
                return new DataShape { Kind = "array", Element = Read(generic.GenericArguments[0], parents, depth + 1) };
            string name = reference.FullName;
            if (name == "System.String") return new DataShape { Kind = "string", DefaultString = "" };
            if (name == "System.Boolean") return new DataShape { Kind = "bool" };
            if (name == "System.Single" || name == "System.Double" || name == "System.Decimal") return new DataShape { Kind = "number" };
            if (name.StartsWith("System.Int", StringComparison.Ordinal) || name.StartsWith("System.UInt", StringComparison.Ordinal) || name == "System.Byte" || name == "System.SByte") return new DataShape { Kind = "integer" };
            TypeDefinition type;
            try { type = reference.Resolve(); }
            catch (AssemblyResolutionException) { return new DataShape { Kind = "opaque" }; }
            if (type == null) return new DataShape { Kind = "opaque" };
            if (type.IsEnum)
            {
                var result = new DataShape { Kind = "enum" };
                foreach (var field in type.Fields.Where(f => f.HasConstant)) result.EnumValues[field.Name] = Convert.ToInt64(field.Constant, CultureInfo.InvariantCulture);
                return result;
            }
            if (!parents.Add(name)) return new DataShape { Kind = "opaque" };
            var shape = new DataShape { Kind = "object" };
            if (type.BaseType != null && type.BaseType.FullName != "System.Object" && type.BaseType.FullName != "System.ValueType")
                foreach (var field in Read(type.BaseType, new HashSet<string>(parents), depth + 1).Fields) shape.Fields[field.Key] = field.Value;
            foreach (var field in type.Fields.Where(f => !f.IsStatic && !f.IsNotSerialized && (f.IsPublic || f.CustomAttributes.Any(a => a.AttributeType.FullName == "UnityEngine.SerializeField"))))
                shape.Fields[field.Name] = Read(field.FieldType, new HashSet<string>(parents), depth + 1);
            // Unity save DTOs put their schema version in a constructor assignment.
            foreach (var constructor in type.Methods.Where(m => m.IsConstructor && !m.IsStatic && m.HasBody))
            {
                var instructions = constructor.Body.Instructions;
                for (int i = 1; i < instructions.Count; i++)
                    if (instructions[i].OpCode.Code == Code.Stfld && instructions[i].Operand is FieldReference field
                        && instructions[i - 1].OpCode.Code == Code.Ldstr && shape.Fields.TryGetValue(field.Name, out var member))
                        member.DefaultString = (string)instructions[i - 1].Operand;
            }
            return shape;
        }

        internal JToken Project(JToken input, Action<string, JToken> preserve, string path = "")
        {
            switch (Kind)
            {
                case "object":
                    var result = new JObject();
                    foreach (var field in Fields)
                    {
                        JToken value = (input as JObject)?[field.Key];
                        if (field.Key == "lastSelectedBeatmapCharacteristicName" && value?.Type == JTokenType.String && !SaveDataStore.SupportsCharacteristic((string)value))
                        {
                            preserve(path + "/" + field.Key, value); value = new JValue("Standard");
                        }
                        result[field.Key] = field.Value.Project(value, preserve, path + "/" + field.Key);
                        if ((field.Key == "version" || field.Key == "_version") && !string.IsNullOrEmpty(field.Value.DefaultString))
                            result[field.Key] = field.Value.DefaultString;
                    }
                    if (input is JObject obj)
                        foreach (var property in obj.Properties().Where(p => !Fields.ContainsKey(p.Name))) preserve(path + "/" + property.Name, property.Value);
                    return result;
                case "array":
                    var array = new JArray();
                    int index = 0;
                    foreach (var value in input as JArray ?? new JArray())
                    {
                        string itemPath = path + "/" + index++;
                        if (Element.InvalidRecord(value)) { preserve(itemPath, value); continue; }
                        array.Add(Element.Project(value, preserve, itemPath));
                    }
                    return array;
                case "enum":
                    if (input != null && input.Type == JTokenType.String && EnumValues.TryGetValue((string)input, out long named)) return new JValue(named);
                    if (input != null && input.Type == JTokenType.Integer && EnumValues.ContainsValue((long)input)) return input.DeepClone();
                    if (input != null && input.Type != JTokenType.Null) preserve(path, input);
                    return new JValue(EnumValues.ContainsValue(0L) ? 0L : EnumValues.Values.FirstOrDefault());
                case "string": return input?.Type == JTokenType.String ? input.DeepClone() : new JValue(DefaultString ?? "");
                case "bool": return input?.Type == JTokenType.Boolean ? input.DeepClone() : new JValue(false);
                case "integer": return input?.Type == JTokenType.Integer ? input.DeepClone() : new JValue(0);
                case "number": return input?.Type == JTokenType.Float || input?.Type == JTokenType.Integer ? input.DeepClone() : new JValue(0f);
                default: return input?.DeepClone() ?? JValue.CreateNull();
            }
        }

        private bool InvalidRecord(JToken input)
        {
            if (!(input is JObject obj) || (obj["levelId"] ?? obj["_levelID"] ?? obj["levelID"]) == null) return false;
            if (obj["beatmapCharacteristicName"]?.Type == JTokenType.String && !SaveDataStore.SupportsCharacteristic((string)obj["beatmapCharacteristicName"])) return true;
            foreach (string key in new[] { "difficulty", "_difficulty", "_gameplayMode" })
            {
                JToken value = obj[key];
                if (value == null || !Fields.TryGetValue(key, out var member) || member.Kind != "enum") continue;
                if (value.Type == JTokenType.Integer && member.EnumValues.ContainsValue((long)value)) continue;
                if (value.Type == JTokenType.String && member.EnumValues.ContainsKey((string)value)) continue;
                return true;
            }
            return false;
        }

        internal string Signature() => Kind + "{" + string.Join(",", Fields.Select(f => f.Key + ":" + f.Value.Signature()))
            + string.Join(",", EnumValues.Select(e => e.Key + "=" + e.Value)) + Element?.Signature() + "}";
    }
}

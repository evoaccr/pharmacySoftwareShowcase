using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using pharmacyApp.AI.Configuration;

namespace pharmacyApp.Infrastructure;

public sealed record AiSecrets
{
    public string OpenAiApiKey { get; init; } = string.Empty;
    public string GatewayToken { get; init; } = string.Empty;
}

/// <summary>One JSONC settings file. Property-level edits retain user comments and unrelated sections.</summary>
public static class AppConfiguration
{
    private static readonly ConfigurationStore Store = new(AppPaths.ConfigurationFilePath);
    public static string FilePath => Store.FilePath;
    public static void EnsureFile() => Store.EnsureFile();
    public static T ReadSection<T>(string section) where T : new() => Store.ReadSection<T>(section);
    public static DatabaseConnectionSettings ReadDatabase() => Store.ReadDatabase();
    public static void WriteSection<T>(string section, T value) => Store.WriteSection(section, value);
}

public sealed class ConfigurationStore(string filePath)
{
    private static readonly object Sync = new();
    private static readonly JsonDocumentOptions ReadOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, WriteIndented = true,
        PropertyNameCaseInsensitive = false
    };
    public string FilePath { get; } = Path.GetFullPath(filePath);

    public void EnsureFile()
    {
        lock (Sync)
        {
            if (Path.GetFileName(FilePath) == AppPaths.ConfigurationFileName)
                Startup.StartupDiagnostics.MigrateConfiguration(Path.GetDirectoryName(FilePath)!);
            if (File.Exists(FilePath)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var defaults = new
            {
                Application = new ApplicationSettings(),
                Database = new DatabaseConnectionSettings("localhost", 5432, "PH", "postgres", ""),
                Ai = new AiConfiguration(),
                Secrets = new AiSecrets()
            };
            var header = "// local settings: restart after changing Application or Database.\n" +
                         "// Prefix notes or disabled lines with //; /* block comments */ are also supported.\n" +
                         "// Keep credentials private when sharing this folder.\n";
            File.WriteAllText(FilePath, header + JsonSerializer.Serialize(defaults, SerializerOptions) + "\n", new UTF8Encoding(false));
        }
    }

    public T ReadSection<T>(string section) where T : new()
    {
        lock (Sync)
        {
            EnsureFile();
            using var document = Parse(File.ReadAllText(FilePath));
            return document.RootElement.TryGetProperty(section, out var value)
                ? value.Deserialize<T>(SerializerOptions) ?? new T()
                : new T();
        }
    }

    public DatabaseConnectionSettings ReadDatabase()
    {
        lock (Sync)
        {
            EnsureFile();
            using var document = Parse(File.ReadAllText(FilePath));
            return document.RootElement.TryGetProperty("Database", out var value)
                ? value.Deserialize<DatabaseConnectionSettings>(SerializerOptions) ?? throw new JsonException("Database settings cannot be null.")
                : new DatabaseConnectionSettings("localhost", 5432, "PH", "postgres", "");
        }
    }

    public void WriteSection<T>(string section, T value)
    {
        lock (Sync)
        {
            EnsureFile();
            var original = File.ReadAllText(FilePath);
            using var validation = Parse(original); // Never replace an invalid user-edited file with defaults.
            var updated = SetValue(original, [section], JsonSerializer.SerializeToElement(value, SerializerOptions));
            using var verified = Parse(updated);
            if (updated == original) return;
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, updated, new UTF8Encoding(false));
            File.Move(temporary, FilePath, true);
        }
    }

    private static JsonDocument Parse(string json)
    {
        var document = JsonDocument.Parse(json, ReadOptions);
        try
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("The local configuration must contain a JSON object.");
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json), new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true
            });
            reader.Read();
            _ = ReadNode(ref reader);
            return document;
        }
        catch { document.Dispose(); throw; }
    }

    private sealed class Node
    {
        public int Start, End, Close;
        public bool IsObject;
        public Dictionary<string, Node> Children = new(StringComparer.Ordinal);
    }

    private static Node ReadNode(ref Utf8JsonReader reader)
    {
        var node = new Node { Start = (int)reader.TokenStartIndex, IsObject = reader.TokenType == JsonTokenType.StartObject };
        if (node.IsObject)
        {
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("Expected a property name.");
                var name = reader.GetString()!;
                if (!reader.Read()) throw new JsonException("Expected a property value.");
                if (!node.Children.TryAdd(name, ReadNode(ref reader))) throw new JsonException($"Duplicate configuration key: {name}");
            }
            node.Close = (int)reader.TokenStartIndex;
        }
        else if (reader.TokenType == JsonTokenType.StartArray) reader.Skip();
        node.End = (int)reader.BytesConsumed;
        return node;
    }

    private static string SetValue(string json, string[] path, JsonElement value)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (!reader.Read()) throw new JsonException("Empty configuration file.");
        var root = ReadNode(ref reader);
        var parent = root;
        for (var i = 0; i < path.Length - 1; i++) parent = parent.Children[path[i]];
        var name = path[^1];
        if (parent.Children.TryGetValue(name, out var existing))
        {
            if (existing.IsObject && value.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in value.EnumerateObject()) json = SetValue(json, [.. path, property.Name], property.Value);
                return json;
            }
            using var old = JsonDocument.Parse(bytes.AsMemory(existing.Start, existing.End - existing.Start), ReadOptions);
            if (JsonElement.DeepEquals(old.RootElement, value)) return json;
            return ReplaceBytes(bytes, existing.Start, existing.End, value.GetRawText());
        }
        var close = parent.Close;
        var last = parent.Children.Values.LastOrDefault();
        if (last is not null)
        {
            var gap = Encoding.UTF8.GetString(bytes, last.End, close - last.End);
            gap = Regex.Replace(gap, @"//[^\r\n]*|/\*[\s\S]*?\*/", "");
            if (!gap.Contains(','))
            {
                json = ReplaceBytes(bytes, last.End, last.End, ",");
                bytes = Encoding.UTF8.GetBytes(json);
                close++;
            }
        }
        var indent = new string(' ', path.Length * 2);
        var objectIndent = new string(' ', (path.Length - 1) * 2);
        var serialized = JsonSerializer.Serialize(value, SerializerOptions).Replace("\n", "\n" + indent);
        var addition = "\n" + indent + JsonSerializer.Serialize(name) + ": " + serialized + "\n" + objectIndent;
        return ReplaceBytes(bytes, close, close, addition);
    }

    private static string ReplaceBytes(byte[] bytes, int start, int end, string value) =>
        Encoding.UTF8.GetString(bytes, 0, start) + value + Encoding.UTF8.GetString(bytes, end, bytes.Length - end);
}

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace LocalMcp.BuildingBlocks.Configuration;

public sealed record WorkspaceConfigurationEntry
{
    public required string Alias { get; init; }
    public required string Path { get; init; }
    public bool Writable { get; init; }
    public string? Description { get; init; }
}

public sealed class LocalWorkspaceConfigurationStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public LocalWorkspaceConfigurationStore(string? configurationPath = null)
    {
        ConfigurationPath = string.IsNullOrWhiteSpace(configurationPath)
            ? LocalConfigurationPaths.GetConfigurationFilePath()
            : System.IO.Path.GetFullPath(configurationPath);
    }

    public string ConfigurationPath { get; }

    public async Task<IReadOnlyList<WorkspaceConfigurationEntry>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ConfigurationPath))
            return [];

        await using var stream = File.OpenRead(ConfigurationPath);
        var root = await JsonNode.ParseAsync(
            stream,
            cancellationToken: cancellationToken) as JsonObject;

        return ReadWorkspaces(root)
            .OrderBy(workspace => workspace.Alias, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task SaveAsync(
        IEnumerable<WorkspaceConfigurationEntry> workspaces,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspaces);

        var normalized = NormalizeAndValidate(workspaces);
        var root = await LoadRootAsync(cancellationToken);
        var previousManagedPaths = ReadWorkspaces(root)
            .Select(workspace => NormalizePath(workspace.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var aliases = new JsonObject();
        foreach (var workspace in normalized)
        {
            var definition = new JsonObject
            {
                ["Path"] = workspace.Path,
                ["Writable"] = workspace.Writable
            };

            if (!string.IsNullOrWhiteSpace(workspace.Description))
                definition["Description"] = workspace.Description;

            aliases[workspace.Alias] = definition;
        }

        root["Workspaces"] = new JsonObject
        {
            ["Aliases"] = aliases
        };

        var fileAccess = root["FileAccess"] as JsonObject ?? new JsonObject();
        var preservedAllowedRoots = ReadStringArray(fileAccess["AllowedRoots"])
            .Where(path => !previousManagedPaths.Contains(NormalizePath(path)));
        var preservedWritableRoots = ReadStringArray(fileAccess["WritableRoots"])
            .Where(path => !previousManagedPaths.Contains(NormalizePath(path)));

        fileAccess["AllowedRoots"] = ToJsonArray(
            preservedAllowedRoots.Concat(normalized.Select(workspace => workspace.Path)));
        fileAccess["WritableRoots"] = ToJsonArray(
            preservedWritableRoots.Concat(
                normalized.Where(workspace => workspace.Writable).Select(workspace => workspace.Path)));
        root["FileAccess"] = fileAccess;

        var directory = System.IO.Path.GetDirectoryName(ConfigurationPath)
            ?? throw new InvalidOperationException("The configuration path has no parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = System.IO.Path.Combine(
            directory,
            $".{System.IO.Path.GetFileName(ConfigurationPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            var json = root.ToJsonString(SerializerOptions);
            await File.WriteAllTextAsync(
                temporaryPath,
                json,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            File.Move(temporaryPath, ConfigurationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private async Task<JsonObject> LoadRootAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(ConfigurationPath))
            return new JsonObject();

        await using var stream = File.OpenRead(ConfigurationPath);
        return await JsonNode.ParseAsync(
            stream,
            cancellationToken: cancellationToken) as JsonObject
            ?? new JsonObject();
    }

    private static IReadOnlyList<WorkspaceConfigurationEntry> NormalizeAndValidate(
        IEnumerable<WorkspaceConfigurationEntry> workspaces)
    {
        var result = new List<WorkspaceConfigurationEntry>();
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var workspace in workspaces)
        {
            var alias = workspace.Alias.Trim();
            if (!IsValidAlias(alias))
            {
                throw new InvalidDataException(
                    $"Workspace alias '{workspace.Alias}' is invalid. Use 1-64 letters, numbers, dots, dashes, or underscores.");
            }

            if (!aliases.Add(alias))
            {
                throw new InvalidDataException(
                    $"Workspace alias '{alias}' is duplicated.");
            }

            var path = NormalizePath(workspace.Path);
            if (!System.IO.Path.IsPathFullyQualified(path) || path.Any(char.IsControl))
            {
                throw new InvalidDataException(
                    $"Workspace '{alias}' must use a valid absolute path.");
            }

            var description = string.IsNullOrWhiteSpace(workspace.Description)
                ? null
                : workspace.Description.Trim();
            if (description is { Length: > 256 } ||
                (description?.Any(char.IsControl) ?? false))
            {
                throw new InvalidDataException(
                    $"Workspace '{alias}' has an invalid description.");
            }

            result.Add(new WorkspaceConfigurationEntry
            {
                Alias = alias,
                Path = path,
                Writable = workspace.Writable,
                Description = description
            });
        }

        return result
            .OrderBy(workspace => workspace.Alias, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<WorkspaceConfigurationEntry> ReadWorkspaces(JsonObject? root)
    {
        if (root?["Workspaces"] is not JsonObject workspacesSection ||
            workspacesSection["Aliases"] is not JsonObject aliases)
        {
            return [];
        }

        var result = new List<WorkspaceConfigurationEntry>();
        foreach (var pair in aliases)
        {
            if (pair.Value is not JsonObject definition ||
                definition["Path"] is not JsonValue pathValue ||
                !pathValue.TryGetValue<string>(out var path) ||
                string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var writable = definition["Writable"] is JsonValue writableValue &&
                writableValue.TryGetValue<bool>(out var writableResult) &&
                writableResult;
            var description = definition["Description"] is JsonValue descriptionValue &&
                descriptionValue.TryGetValue<string>(out var descriptionResult)
                    ? descriptionResult
                    : null;

            result.Add(new WorkspaceConfigurationEntry
            {
                Alias = pair.Key,
                Path = path,
                Writable = writable,
                Description = description
            });
        }

        return result;
    }

    private static IEnumerable<string> ReadStringArray(JsonNode? node)
    {
        if (node is not JsonArray array)
            yield break;

        foreach (var item in array)
        {
            if (item is JsonValue value &&
                value.TryGetValue<string>(out var text) &&
                !string.IsNullOrWhiteSpace(text))
            {
                yield return text;
            }
        }
    }

    private static JsonArray ToJsonArray(IEnumerable<string> paths)
    {
        var array = new JsonArray();
        foreach (var path in paths
                     .Select(NormalizePath)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            array.Add(path);
        }

        return array;
    }

    private static string NormalizePath(string path) =>
        System.IO.Path.TrimEndingDirectorySeparator(
            System.IO.Path.GetFullPath(path.Trim()));

    private static bool IsValidAlias(string alias)
    {
        if (alias.Length is < 1 or > 64 || !char.IsLetterOrDigit(alias[0]))
            return false;

        return alias.All(character =>
            char.IsLetterOrDigit(character) || character is '.' or '-' or '_');
    }
}

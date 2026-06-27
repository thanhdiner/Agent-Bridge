using Microsoft.Extensions.Options;
using LocalMcp.Contracts.Results;
using LocalMcp.BuildingBlocks.Errors;

namespace LocalMcp.Agent.Windows.Security;

public sealed class PathPolicy : IPathPolicy
{
    private readonly FileAccessOptions _options;

    public PathPolicy(IOptions<FileAccessOptions> options)
    {
        _options = options.Value;
    }

    public CommandError? Validate(string rawPath, out string normalizedPath, bool isDirectory = false)
    {
        normalizedPath = string.Empty;

        // 1. Reject null, empty, or whitespace-only paths
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return new CommandError(ErrorCodes.InvalidPath, "Path is null, empty, or whitespace.");
        }

        // 2. Convert to normalized absolute path
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(rawPath);
        }
        catch (Exception)
        {
            return new CommandError(ErrorCodes.InvalidPath, "The path format is invalid.");
        }

        // 3. Resolve symbolic links/junctions recursively to find the actual physical path
        string physicalPath;
        try
        {
            physicalPath = ResolvePhysicalPath(fullPath);
        }
        catch (Exception ex)
        {
            return new CommandError(ErrorCodes.AccessDenied, $"Failed to resolve physical path: {ex.Message}");
        }

        // 4. Verify path is inside one of the allowed roots
        var allowedRoot = GetMatchingAllowedRoot(physicalPath);
        if (allowedRoot is null)
        {
            return new CommandError(
                ErrorCodes.PathOutsideAllowedRoot,
                "The requested path lies outside the configured allowed root directories."
            );
        }

        // Also check that the original fullPath (before symlink resolution) is inside an allowed root
        var originalAllowedRoot = GetMatchingAllowedRoot(fullPath);
        if (originalAllowedRoot is null)
        {
            return new CommandError(
                ErrorCodes.PathOutsideAllowedRoot,
                "The requested path lies outside the configured allowed root directories."
            );
        }

        // 5. Reject denied directory segments
        var segments = physicalPath.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries
        );
        foreach (var segment in segments)
        {
            if (_options.DeniedSegments.Any(ds => string.Equals(ds, segment, StringComparison.OrdinalIgnoreCase)))
            {
                return new CommandError(
                    ErrorCodes.AccessDenied,
                    $"Access denied to path containing segment '{segment}'."
                );
            }
        }

        // 6. Reject denied filenames
        var fileName = Path.GetFileName(physicalPath);
        if (!string.IsNullOrEmpty(fileName))
        {
            if (_options.DeniedFileNames.Any(df => string.Equals(df, fileName, StringComparison.OrdinalIgnoreCase)))
            {
                return new CommandError(
                    ErrorCodes.AccessDenied,
                    $"Access denied to file '{fileName}'."
                );
            }
        }

        // 7. Verify file/directory exists
        if (isDirectory)
        {
            if (!Directory.Exists(physicalPath))
            {
                return new CommandError(ErrorCodes.DirectoryNotFound, "The requested directory was not found.");
            }
        }
        else
        {
            if (!File.Exists(physicalPath))
            {
                return new CommandError(ErrorCodes.FileNotFound, "The requested file was not found.");
            }

            // 8. Check file size before reading
            var fileInfo = new FileInfo(physicalPath);
            if (fileInfo.Length > _options.MaxReadBytes)
            {
                return new CommandError(
                    ErrorCodes.FileTooLarge,
                    $"The file size ({fileInfo.Length} bytes) exceeds the allowed limit of {_options.MaxReadBytes} bytes."
                );
            }
        }

        normalizedPath = physicalPath;
        return null;
    }

    private string? GetMatchingAllowedRoot(string path)
    {
        foreach (var root in _options.AllowedRoots)
        {
            try
            {
                var fullRoot = Path.GetFullPath(root);
                if (IsSubdirectoryOf(path, fullRoot))
                {
                    return fullRoot;
                }
            }
            catch
            {
                // Ignore invalid roots in options
            }
        }
        return null;
    }

    private static bool IsSubdirectoryOf(string path, string root)
    {
        var normalizedPath = Path.GetFullPath(path).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var normalizedRoot = Path.GetFullPath(root).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

        // Windows-appropriate path comparison (case insensitive)
        if (!normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Prevent prefix collisions (e.g. F:\Project vs F:\ProjectFake)
        if (normalizedPath.Length == normalizedRoot.Length)
        {
            return true;
        }

        var nextChar = normalizedPath[normalizedRoot.Length];
        return nextChar == Path.DirectorySeparatorChar;
    }

    private string ResolvePhysicalPath(string path)
    {
        var current = Path.GetFullPath(path);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (true)
        {
            if (!visited.Add(current))
            {
                throw new IOException("Symbolic link loop detected.");
            }

            FileSystemInfo? fsi = null;
            if (File.Exists(current))
            {
                fsi = new FileInfo(current);
            }
            else if (Directory.Exists(current))
            {
                fsi = new DirectoryInfo(current);
            }

            if (fsi is null)
            {
                var parent = Path.GetDirectoryName(current);
                if (parent is null)
                {
                    break;
                }

                var resolvedParent = ResolvePhysicalPath(parent);
                if (!string.Equals(resolvedParent, parent, StringComparison.OrdinalIgnoreCase))
                {
                    current = Path.Combine(resolvedParent, Path.GetFileName(current));
                    continue;
                }
                break;
            }

            if (fsi.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                var target = fsi.ResolveLinkTarget(true);
                if (target is not null)
                {
                    current = target.FullName;
                    continue;
                }
            }

            var currentParent = Path.GetDirectoryName(current);
            if (currentParent is null)
            {
                break;
            }

            var resolvedCurrentParent = ResolvePhysicalPath(currentParent);
            if (!string.Equals(resolvedCurrentParent, currentParent, StringComparison.OrdinalIgnoreCase))
            {
                current = Path.Combine(resolvedCurrentParent, Path.GetFileName(current));
                continue;
            }

            break;
        }

        return Path.GetFullPath(current);
    }
}

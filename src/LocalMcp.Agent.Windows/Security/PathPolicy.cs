using Microsoft.Extensions.Options;
using LocalMcp.Contracts.Results;
using LocalMcp.BuildingBlocks.Errors;
using System.IO;

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
        if (isDirectory)
        {
            return AuthorizeReadDirectory(rawPath, out normalizedPath);
        }
        else
        {
            return AuthorizeReadFile(rawPath, out normalizedPath);
        }
    }

    public CommandError? AuthorizeReadFile(string rawPath, out string normalizedPath)
    {
        normalizedPath = string.Empty;

        var pathError = ValidateBasicPath(rawPath, out var fullPath);
        if (pathError is not null)
        {
            return pathError;
        }

        string physicalPath;
        try
        {
            if (File.Exists(fullPath) || Directory.Exists(fullPath))
            {
                physicalPath = ResolvePhysicalPath(fullPath);
            }
            else
            {
                var parent = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(parent))
                {
                    var resolvedParent = ResolvePhysicalPath(parent);
                    physicalPath = Path.Combine(resolvedParent, Path.GetFileName(fullPath));
                }
                else
                {
                    physicalPath = fullPath;
                }
            }
        }
        catch (Exception)
        {
            return new CommandError(ErrorCodes.AccessDenied, "Failed to resolve physical path.");
        }

        // Validate allowed roots & denied patterns on physical path (sandbox check)
        var rootError = ValidateAllowedRootsAndSegments(physicalPath, fullPath);
        if (rootError is not null)
        {
            return rootError;
        }

        // Must exist as a file
        if (Directory.Exists(physicalPath))
        {
            return new CommandError(ErrorCodes.AccessDenied, "The requested path is a directory, not a file.");
        }
        if (!File.Exists(physicalPath))
        {
            return new CommandError(ErrorCodes.FileNotFound, "The requested file was not found.");
        }

        // Size check
        var fileInfo = new FileInfo(physicalPath);
        if (fileInfo.Length > _options.MaxReadBytes)
        {
            return new CommandError(
                ErrorCodes.FileTooLarge,
                $"The file size ({fileInfo.Length} bytes) exceeds the allowed limit of {_options.MaxReadBytes} bytes."
            );
        }

        normalizedPath = physicalPath;
        return null;
    }

    public CommandError? AuthorizeReadDirectory(string rawPath, out string normalizedPath)
    {
        normalizedPath = string.Empty;

        var pathError = ValidateBasicPath(rawPath, out var fullPath);
        if (pathError is not null)
        {
            return pathError;
        }

        string physicalPath;
        try
        {
            if (File.Exists(fullPath) || Directory.Exists(fullPath))
            {
                physicalPath = ResolvePhysicalPath(fullPath);
            }
            else
            {
                var parent = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(parent))
                {
                    var resolvedParent = ResolvePhysicalPath(parent);
                    physicalPath = Path.Combine(resolvedParent, Path.GetFileName(fullPath));
                }
                else
                {
                    physicalPath = fullPath;
                }
            }
        }
        catch (Exception)
        {
            return new CommandError(ErrorCodes.AccessDenied, "Failed to resolve physical path.");
        }

        var rootError = ValidateAllowedRootsAndSegments(physicalPath, fullPath);
        if (rootError is not null)
        {
            return rootError;
        }

        // Must exist as a directory
        if (File.Exists(physicalPath))
        {
            return new CommandError(ErrorCodes.AccessDenied, "The requested path is a file, not a directory.");
        }
        if (!Directory.Exists(physicalPath))
        {
            return new CommandError(ErrorCodes.DirectoryNotFound, "The requested directory was not found.");
        }

        normalizedPath = physicalPath;
        return null;
    }

    public CommandError? AuthorizeWriteFile(string rawPath, out string normalizedPath, bool mustExist = false)
    {
        normalizedPath = string.Empty;

        // Check if WritableRoots is configured
        if (_options.WritableRoots == null || _options.WritableRoots.Count == 0)
        {
            return new CommandError(ErrorCodes.WritableRootNotConfigured, "No writable roots are configured on the agent.");
        }

        var pathError = ValidateBasicPath(rawPath, out var fullPath);
        if (pathError is not null)
        {
            return pathError;
        }

        // Cannot write to an existing directory
        if (Directory.Exists(fullPath))
        {
            return new CommandError(ErrorCodes.AccessDenied, "The target path is a directory, not a file.");
        }

        string physicalPath;
        try
        {
            if (File.Exists(fullPath))
            {
                physicalPath = ResolvePhysicalPath(fullPath);
            }
            else
            {
                // File does not exist yet, resolve parent directory
                var parent = Path.GetDirectoryName(fullPath);
                if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
                {
                    return new CommandError(ErrorCodes.DirectoryNotFound, "The parent directory of the target path was not found.");
                }
                var physicalParent = ResolvePhysicalPath(parent);
                physicalPath = Path.Combine(physicalParent, Path.GetFileName(fullPath));
            }
        }
        catch (Exception)
        {
            return new CommandError(ErrorCodes.AccessDenied, "Failed to resolve physical path.");
        }

        // Must be within WritableRoots
        var writableRoot = GetMatchingWritableRoot(physicalPath);
        if (writableRoot is null)
        {
            return new CommandError(
                ErrorCodes.WriteNotAllowed,
                "The requested path lies outside the configured writable root directories."
            );
        }

        // Original path must also match a writable root to prevent prefix escaping before resolution
        var originalWritableRoot = GetMatchingWritableRoot(fullPath);
        if (originalWritableRoot is null)
        {
            return new CommandError(
                ErrorCodes.WriteNotAllowed,
                "The requested path lies outside the configured writable root directories."
            );
        }

        // Path must also be within AllowedRoots (double protection)
        var allowedRoot = GetMatchingAllowedRoot(physicalPath);
        if (allowedRoot is null)
        {
            return new CommandError(
                ErrorCodes.PathOutsideAllowedRoot,
                "The requested path lies outside the configured allowed root directories."
            );
        }

        var originalAllowedRoot = GetMatchingAllowedRoot(fullPath);
        if (originalAllowedRoot is null)
        {
            return new CommandError(
                ErrorCodes.PathOutsideAllowedRoot,
                "The requested path lies outside the configured allowed root directories."
            );
        }

        // Split segments check
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

        // Reject generic and write-specific denied filenames
        var fileName = Path.GetFileName(physicalPath);
        if (!string.IsNullOrEmpty(fileName))
        {
            if (_options.DeniedFileNames.Any(df => MatchFileName(fileName, df)) ||
                _options.DeniedWriteFileNames.Any(dw => MatchFileName(fileName, dw)))
            {
                return new CommandError(
                    ErrorCodes.AccessDenied,
                    $"Access denied to file '{fileName}'."
                );
            }

            // Reject denied extensions (e.g. .pem, .key)
            var ext = Path.GetExtension(physicalPath);
            if (!string.IsNullOrEmpty(ext))
            {
                if (_options.DeniedWriteExtensions.Any(de => string.Equals(de, ext, StringComparison.OrdinalIgnoreCase)))
                {
                    return new CommandError(
                        ErrorCodes.AccessDenied,
                        $"Access denied to file with extension '{ext}'."
                    );
                }
            }
        }

        // File-level existence check if required
        if (mustExist && !File.Exists(physicalPath))
        {
            return new CommandError(ErrorCodes.FileNotFound, "The requested file was not found.");
        }

        // Check if the existing file is read-only
        if (File.Exists(physicalPath))
        {
            var attributes = File.GetAttributes(physicalPath);
            if (attributes.HasFlag(FileAttributes.ReadOnly))
            {
                return new CommandError(ErrorCodes.FileReadOnly, "The target file is read-only.");
            }
        }

        normalizedPath = physicalPath;
        return null;
    }

    private CommandError? ValidateBasicPath(string rawPath, out string fullPath)
    {
        fullPath = string.Empty;

        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return new CommandError(ErrorCodes.InvalidPath, "Path is null, empty, or whitespace.");
        }

        try
        {
            fullPath = Path.GetFullPath(rawPath);
        }
        catch (Exception)
        {
            return new CommandError(ErrorCodes.InvalidPath, "The path format is invalid.");
        }

        return null;
    }

    private CommandError? ValidateAllowedRootsAndSegments(string physicalPath, string originalFullPath)
    {
        var allowedRoot = GetMatchingAllowedRoot(physicalPath);
        if (allowedRoot is null)
        {
            return new CommandError(
                ErrorCodes.PathOutsideAllowedRoot,
                "The requested path lies outside the configured allowed root directories."
            );
        }

        var originalAllowedRoot = GetMatchingAllowedRoot(originalFullPath);
        if (originalAllowedRoot is null)
        {
            return new CommandError(
                ErrorCodes.PathOutsideAllowedRoot,
                "The requested path lies outside the configured allowed root directories."
            );
        }

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

        var fileName = Path.GetFileName(physicalPath);
        if (!string.IsNullOrEmpty(fileName))
        {
            if (_options.DeniedFileNames.Any(df => MatchFileName(fileName, df)))
            {
                return new CommandError(
                    ErrorCodes.AccessDenied,
                    $"Access denied to file '{fileName}'."
                );
            }
        }

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

    private string? GetMatchingWritableRoot(string path)
    {
        foreach (var root in _options.WritableRoots)
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

    public static bool IsSubdirectoryOf(string path, string root)
    {
        var normalizedPath = Path.GetFullPath(path).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var normalizedRoot = Path.GetFullPath(root).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

        if (!normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

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

    private static bool MatchFileName(string fileName, string pattern)
    {
        if (pattern.EndsWith('*'))
        {
            var prefix = pattern.Substring(0, pattern.Length - 1);
            return fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        return string.Equals(fileName, pattern, StringComparison.OrdinalIgnoreCase);
    }
}

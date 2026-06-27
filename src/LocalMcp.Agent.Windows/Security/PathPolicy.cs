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

    public CommandError? AuthorizeCreateDirectory(string rawPath, out string normalizedPath, bool recursive)
    {
        normalizedPath = string.Empty;

        if (_options.WritableRoots == null || _options.WritableRoots.Count == 0)
        {
            return new CommandError(ErrorCodes.WritableRootNotConfigured, "No writable roots are configured on the agent.");
        }

        var pathError = ValidateBasicPath(rawPath, out var fullPath);
        if (pathError is not null)
        {
            return pathError;
        }

        if (File.Exists(fullPath))
        {
            return new CommandError(ErrorCodes.AccessDenied, "A file already exists at the target path.");
        }

        if (!recursive)
        {
            var parent = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            {
                return new CommandError(ErrorCodes.DirectoryNotFound, "The parent directory was not found.");
            }
        }

        string physicalPath;
        try
        {
            if (Directory.Exists(fullPath))
            {
                var originalAttrs = File.GetAttributes(fullPath);
                if (originalAttrs.HasFlag(FileAttributes.ReparsePoint))
                {
                    return new CommandError(ErrorCodes.AccessDenied, "Reparse points are not allowed on the path.");
                }
                physicalPath = ResolvePhysicalPath(fullPath);
            }
            else
            {
                var current = fullPath;
                string? existingAncestor = null;
                while (!string.IsNullOrEmpty(current))
                {
                    if (Directory.Exists(current) || File.Exists(current))
                    {
                        var attrs = File.GetAttributes(current);
                        if (attrs.HasFlag(FileAttributes.ReparsePoint))
                        {
                            return new CommandError(ErrorCodes.AccessDenied, "Reparse points are not allowed on the path.");
                        }
                    }

                    var parent = Path.GetDirectoryName(current);
                    if (string.IsNullOrEmpty(parent))
                    {
                        break;
                    }
                    if (Directory.Exists(parent))
                    {
                        existingAncestor = parent;
                        break;
                    }
                    current = parent;
                }

                if (!string.IsNullOrEmpty(existingAncestor))
                {
                    var ancestorAttrs = File.GetAttributes(existingAncestor);
                    if (ancestorAttrs.HasFlag(FileAttributes.ReparsePoint))
                    {
                        return new CommandError(ErrorCodes.AccessDenied, "Reparse points are not allowed on the path.");
                    }
                    var resolvedParent = ResolvePhysicalPath(existingAncestor);
                    var relative = Path.GetRelativePath(existingAncestor, fullPath);
                    physicalPath = Path.GetFullPath(Path.Combine(resolvedParent, relative));
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

        var writableRoot = GetMatchingWritableRoot(physicalPath);
        if (writableRoot is null)
        {
            return new CommandError(ErrorCodes.WriteNotAllowed, "The requested path lies outside the configured writable root directories.");
        }

        var originalWritableRoot = GetMatchingWritableRoot(fullPath);
        if (originalWritableRoot is null)
        {
            return new CommandError(ErrorCodes.WriteNotAllowed, "The requested path lies outside the configured writable root directories.");
        }

        var allowedRoot = GetMatchingAllowedRoot(physicalPath);
        if (allowedRoot is null)
        {
            return new CommandError(ErrorCodes.PathOutsideAllowedRoot, "The requested path lies outside the configured allowed root directories.");
        }

        var originalAllowedRoot = GetMatchingAllowedRoot(fullPath);
        if (originalAllowedRoot is null)
        {
            return new CommandError(ErrorCodes.PathOutsideAllowedRoot, "The requested path lies outside the configured allowed root directories.");
        }

        var segments = physicalPath.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries
        );
        foreach (var segment in segments)
        {
            if (_options.DeniedSegments.Any(ds => string.Equals(ds, segment, StringComparison.OrdinalIgnoreCase)))
            {
                return new CommandError(ErrorCodes.AccessDenied, $"Access denied to path containing segment '{segment}'.");
            }
        }

        var fileName = Path.GetFileName(physicalPath);
        if (!string.IsNullOrEmpty(fileName))
        {
            if (_options.DeniedFileNames.Any(df => MatchFileName(fileName, df)) ||
                _options.DeniedWriteFileNames.Any(dw => MatchFileName(fileName, dw)))
            {
                return new CommandError(ErrorCodes.AccessDenied, $"Access denied to directory '{fileName}'.");
            }
        }

        normalizedPath = physicalPath;
        return null;
    }

    public CommandError? AuthorizeStat(string rawPath, out string normalizedPath)
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
                var originalAttrs = File.GetAttributes(fullPath);
                if (originalAttrs.HasFlag(FileAttributes.ReparsePoint))
                {
                    return new CommandError(ErrorCodes.AccessDenied, "Reparse points are not allowed on the path.");
                }
                physicalPath = ResolvePhysicalPath(fullPath);
            }
            else
            {
                var current = fullPath;
                string? existingAncestor = null;
                while (!string.IsNullOrEmpty(current))
                {
                    if (Directory.Exists(current) || File.Exists(current))
                    {
                        var attrs = File.GetAttributes(current);
                        if (attrs.HasFlag(FileAttributes.ReparsePoint))
                        {
                            return new CommandError(ErrorCodes.AccessDenied, "Reparse points are not allowed on the path.");
                        }
                    }

                    var parent = Path.GetDirectoryName(current);
                    if (string.IsNullOrEmpty(parent))
                    {
                        break;
                    }
                    if (Directory.Exists(parent))
                    {
                        existingAncestor = parent;
                        break;
                    }
                    current = parent;
                }

                if (!string.IsNullOrEmpty(existingAncestor))
                {
                    var ancestorAttrs = File.GetAttributes(existingAncestor);
                    if (ancestorAttrs.HasFlag(FileAttributes.ReparsePoint))
                    {
                        return new CommandError(ErrorCodes.AccessDenied, "Reparse points are not allowed on the path.");
                    }
                    var resolvedParent = ResolvePhysicalPath(existingAncestor);
                    var relative = Path.GetRelativePath(existingAncestor, fullPath);
                    physicalPath = Path.GetFullPath(Path.Combine(resolvedParent, relative));
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

        normalizedPath = physicalPath;
        return null;
    }

    public CommandError? AuthorizeMove(
        string rawSource,
        string rawDestination,
        bool overwrite,
        out string normalizedSource,
        out string normalizedDestination)
    {
        normalizedSource = string.Empty;
        normalizedDestination = string.Empty;

        if (_options.WritableRoots == null || _options.WritableRoots.Count == 0)
            return new CommandError(ErrorCodes.WritableRootNotConfigured, "No writable roots are configured on the agent.");

        var srcError = ValidateBasicPath(rawSource, out var fullSource);
        if (srcError is not null) return srcError;

        var destError = ValidateBasicPath(rawDestination, out var fullDest);
        if (destError is not null) return destError;

        var reparseSrc = VerifyNoReparsePointsInOriginalPath(fullSource);
        if (reparseSrc is not null) return reparseSrc;

        var reparseDest = VerifyNoReparsePointsInOriginalPath(fullDest);
        if (reparseDest is not null) return reparseDest;

        // Resolve source – must exist
        string physicalSource;
        try
        {
            if (File.Exists(fullSource) || Directory.Exists(fullSource))
            {
                var originalAttrs = File.GetAttributes(fullSource);
                if (originalAttrs.HasFlag(FileAttributes.ReparsePoint))
                    return new CommandError(ErrorCodes.AccessDenied, "Reparse points are not allowed on the source path.");
                physicalSource = ResolvePhysicalPath(fullSource);
            }
            else
            {
                return new CommandError(ErrorCodes.FileNotFound, "Source path was not found.");
            }
        }
        catch (Exception)
        {
            return new CommandError(ErrorCodes.AccessDenied, "Failed to resolve physical source path.");
        }

        // Resolve destination
        string physicalDest;
        try
        {
            if (File.Exists(fullDest) || Directory.Exists(fullDest))
            {
                var originalAttrs = File.GetAttributes(fullDest);
                if (originalAttrs.HasFlag(FileAttributes.ReparsePoint))
                    return new CommandError(ErrorCodes.AccessDenied, "Reparse points are not allowed on the destination path.");
                physicalDest = ResolvePhysicalPath(fullDest);
            }
            else
            {
                var parent = Path.GetDirectoryName(fullDest);
                if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
                    return new CommandError(ErrorCodes.DirectoryNotFound, "The parent directory of the destination was not found.");
                var physicalParent = ResolvePhysicalPath(parent);
                physicalDest = Path.Combine(physicalParent, Path.GetFileName(fullDest));
            }
        }
        catch (Exception)
        {
            return new CommandError(ErrorCodes.AccessDenied, "Failed to resolve physical destination path.");
        }

        // Directory overwrite is never allowed
        if (Directory.Exists(physicalDest))
            return new CommandError(ErrorCodes.AccessDenied, "Directory overwrite is not allowed.");

        // File overwrite
        if (File.Exists(physicalDest))
        {
            if (!overwrite)
                return new CommandError(ErrorCodes.AccessDenied, "The destination file already exists.");
            var destAttrs = File.GetAttributes(physicalDest);
            if (destAttrs.HasFlag(FileAttributes.ReadOnly))
                return new CommandError(ErrorCodes.FileReadOnly, "The destination file is read-only.");
        }

        // Source sandboxing: WritableRoots AND AllowedRoots (move deletes source)
        if (GetMatchingWritableRoot(physicalSource) is null || GetMatchingWritableRoot(fullSource) is null)
            return new CommandError(ErrorCodes.WriteNotAllowed, "The source path lies outside the configured writable root directories.");
        if (GetMatchingAllowedRoot(physicalSource) is null || GetMatchingAllowedRoot(fullSource) is null)
            return new CommandError(ErrorCodes.PathOutsideAllowedRoot, "The source path lies outside the configured allowed root directories.");

        // Destination sandboxing: WritableRoots AND AllowedRoots
        if (GetMatchingWritableRoot(physicalDest) is null || GetMatchingWritableRoot(fullDest) is null)
            return new CommandError(ErrorCodes.WriteNotAllowed, "The destination path lies outside the configured writable root directories.");
        if (GetMatchingAllowedRoot(physicalDest) is null || GetMatchingAllowedRoot(fullDest) is null)
            return new CommandError(ErrorCodes.PathOutsideAllowedRoot, "The destination path lies outside the configured allowed root directories.");

        // Denied segments + filenames – source
        foreach (var seg in physicalSource.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (_options.DeniedSegments.Any(ds => string.Equals(ds, seg, StringComparison.OrdinalIgnoreCase)))
                return new CommandError(ErrorCodes.AccessDenied, $"Access denied to source path containing segment '{seg}'.");
        }
        var srcFileName = Path.GetFileName(physicalSource);
        if (!string.IsNullOrEmpty(srcFileName) &&
            (_options.DeniedFileNames.Any(df => MatchFileName(srcFileName, df)) ||
             _options.DeniedWriteFileNames.Any(dw => MatchFileName(srcFileName, dw))))
            return new CommandError(ErrorCodes.AccessDenied, $"Access denied to source '{srcFileName}'.");

        // Denied segments + filenames + extensions – destination
        foreach (var seg in physicalDest.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (_options.DeniedSegments.Any(ds => string.Equals(ds, seg, StringComparison.OrdinalIgnoreCase)))
                return new CommandError(ErrorCodes.AccessDenied, $"Access denied to destination path containing segment '{seg}'.");
        }
        var destFileName = Path.GetFileName(physicalDest);
        if (!string.IsNullOrEmpty(destFileName))
        {
            if (_options.DeniedFileNames.Any(df => MatchFileName(destFileName, df)) ||
                _options.DeniedWriteFileNames.Any(dw => MatchFileName(destFileName, dw)))
                return new CommandError(ErrorCodes.AccessDenied, $"Access denied to destination '{destFileName}'.");

            var ext = Path.GetExtension(physicalDest);
            if (!string.IsNullOrEmpty(ext) &&
                _options.DeniedWriteExtensions.Any(de => string.Equals(de, ext, StringComparison.OrdinalIgnoreCase)))
                return new CommandError(ErrorCodes.AccessDenied, $"Access denied to destination with extension '{ext}'.");
        }

        // Reject cross-volume moves
        var srcRoot = Path.GetPathRoot(physicalSource);
        var dstRoot = Path.GetPathRoot(physicalDest);
        if (!string.Equals(srcRoot, dstRoot, StringComparison.OrdinalIgnoreCase))
            return new CommandError(ErrorCodes.AccessDenied, "Cross-volume moves are not supported.");

        normalizedSource = physicalSource;
        normalizedDestination = physicalDest;
        return null;
    }

    public CommandError? AuthorizeCopy(
        string rawSource,
        string rawDestination,
        bool overwrite,
        out string normalizedSource,
        out string normalizedDestination)
    {
        normalizedSource = string.Empty;
        normalizedDestination = string.Empty;

        if (_options.WritableRoots == null || _options.WritableRoots.Count == 0)
            return new CommandError(ErrorCodes.WritableRootNotConfigured, "No writable roots are configured on the agent.");

        var srcError = ValidateBasicPath(rawSource, out var fullSource);
        if (srcError is not null) return srcError;

        var destError = ValidateBasicPath(rawDestination, out var fullDest);
        if (destError is not null) return destError;

        var reparseSrc = VerifyNoReparsePointsInOriginalPath(fullSource);
        if (reparseSrc is not null) return reparseSrc;

        var reparseDest = VerifyNoReparsePointsInOriginalPath(fullDest);
        if (reparseDest is not null) return reparseDest;

        // Resolve source – must exist as either a file or directory.
        string physicalSource;
        try
        {
            if (Directory.Exists(fullSource))
            {
                var originalAttrs = File.GetAttributes(fullSource);
                if (originalAttrs.HasFlag(FileAttributes.ReparsePoint))
                    return new CommandError(ErrorCodes.AccessDenied, "Reparse points are not allowed on the source path.");
                physicalSource = ResolvePhysicalPath(fullSource);
            }
            else if (File.Exists(fullSource))
            {
                var originalAttrs = File.GetAttributes(fullSource);
                if (originalAttrs.HasFlag(FileAttributes.ReparsePoint))
                    return new CommandError(ErrorCodes.AccessDenied, "Reparse points are not allowed on the source path.");
                physicalSource = ResolvePhysicalPath(fullSource);
            }
            else
            {
                return new CommandError(ErrorCodes.FileNotFound, "Source file was not found.");
            }
        }
        catch (Exception)
        {
            return new CommandError(ErrorCodes.AccessDenied, "Failed to resolve physical source path.");
        }

        // Resolve destination
        string physicalDest;
        try
        {
            if (File.Exists(fullDest) || Directory.Exists(fullDest))
            {
                var originalAttrs = File.GetAttributes(fullDest);
                if (originalAttrs.HasFlag(FileAttributes.ReparsePoint))
                    return new CommandError(ErrorCodes.AccessDenied, "Reparse points are not allowed on the destination path.");
                physicalDest = ResolvePhysicalPath(fullDest);
            }
            else
            {
                var parent = Path.GetDirectoryName(fullDest);
                if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
                    return new CommandError(ErrorCodes.DirectoryNotFound, "The parent directory of the destination was not found.");
                var physicalParent = ResolvePhysicalPath(parent);
                physicalDest = Path.Combine(physicalParent, Path.GetFileName(fullDest));
            }
        }
        catch (Exception)
        {
            return new CommandError(ErrorCodes.AccessDenied, "Failed to resolve physical destination path.");
        }

        if (Directory.Exists(physicalSource) && (File.Exists(physicalDest) || Directory.Exists(physicalDest)))
            return new CommandError(ErrorCodes.AccessDenied, "The destination path already exists.");

        if (!Directory.Exists(physicalSource) && Directory.Exists(physicalDest))
            return new CommandError(ErrorCodes.AccessDenied, "Destination is a directory.");

        if (File.Exists(physicalDest))
        {
            if (!overwrite)
                return new CommandError(ErrorCodes.AccessDenied, "The destination file already exists.");
            var destAttrs = File.GetAttributes(physicalDest);
            if (destAttrs.HasFlag(FileAttributes.ReadOnly))
                return new CommandError(ErrorCodes.FileReadOnly, "The destination file is read-only.");
        }

        // Source sandboxing: AllowedRoots only (we only read it)
        if (GetMatchingAllowedRoot(physicalSource) is null || GetMatchingAllowedRoot(fullSource) is null)
            return new CommandError(ErrorCodes.PathOutsideAllowedRoot, "The source path lies outside the configured allowed root directories.");

        // Destination sandboxing: WritableRoots AND AllowedRoots
        if (GetMatchingWritableRoot(physicalDest) is null || GetMatchingWritableRoot(fullDest) is null)
            return new CommandError(ErrorCodes.WriteNotAllowed, "The destination path lies outside the configured writable root directories.");
        if (GetMatchingAllowedRoot(physicalDest) is null || GetMatchingAllowedRoot(fullDest) is null)
            return new CommandError(ErrorCodes.PathOutsideAllowedRoot, "The destination path lies outside the configured allowed root directories.");

        if (Directory.Exists(physicalSource) &&
            (IsSubdirectoryOf(physicalDest, physicalSource) || IsSubdirectoryOf(fullDest, fullSource)))
            return new CommandError(ErrorCodes.AccessDenied, "The destination directory cannot be the source directory or a descendant of it.");

        // Denied segments + filenames – source (read-only denied list)
        foreach (var seg in physicalSource.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (_options.DeniedSegments.Any(ds => string.Equals(ds, seg, StringComparison.OrdinalIgnoreCase)))
                return new CommandError(ErrorCodes.AccessDenied, $"Access denied to source path containing segment '{seg}'.");
        }
        var srcFileName = Path.GetFileName(physicalSource);
        if (!string.IsNullOrEmpty(srcFileName) && _options.DeniedFileNames.Any(df => MatchFileName(srcFileName, df)))
            return new CommandError(ErrorCodes.AccessDenied, $"Access denied to source '{srcFileName}'.");

        // Denied segments + filenames + extensions – destination
        foreach (var seg in physicalDest.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (_options.DeniedSegments.Any(ds => string.Equals(ds, seg, StringComparison.OrdinalIgnoreCase)))
                return new CommandError(ErrorCodes.AccessDenied, $"Access denied to destination path containing segment '{seg}'.");
        }
        var destFileName = Path.GetFileName(physicalDest);
        if (!string.IsNullOrEmpty(destFileName))
        {
            if (_options.DeniedFileNames.Any(df => MatchFileName(destFileName, df)) ||
                _options.DeniedWriteFileNames.Any(dw => MatchFileName(destFileName, dw)))
                return new CommandError(ErrorCodes.AccessDenied, $"Access denied to destination '{destFileName}'.");

            var ext = Path.GetExtension(physicalDest);
            if (!string.IsNullOrEmpty(ext) &&
                _options.DeniedWriteExtensions.Any(de => string.Equals(de, ext, StringComparison.OrdinalIgnoreCase)))
                return new CommandError(ErrorCodes.AccessDenied, $"Access denied to destination with extension '{ext}'.");
        }

        normalizedSource = physicalSource;
        normalizedDestination = physicalDest;
        return null;
    }

    public CommandError? AuthorizeDeleteFile(string rawPath, bool missingOk, out string normalizedPath)
    {
        normalizedPath = string.Empty;

        if (_options.WritableRoots == null || _options.WritableRoots.Count == 0)
            return new CommandError(ErrorCodes.WritableRootNotConfigured, "No writable roots are configured on the agent.");

        var pathError = ValidateBasicPath(rawPath, out var fullPath);
        if (pathError is not null)
            return pathError;

        var reparseError = VerifyNoReparsePointsInOriginalPath(fullPath);
        if (reparseError is not null)
            return reparseError;

        if (Directory.Exists(fullPath))
            return new CommandError(ErrorCodes.AccessDenied, "Deleting directories is not supported.");

        string physicalPath;
        try
        {
            if (File.Exists(fullPath))
            {
                var attributes = File.GetAttributes(fullPath);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    return new CommandError(ErrorCodes.AccessDenied, "Reparse points are not allowed on the delete path.");

                physicalPath = ResolvePhysicalPath(fullPath);
            }
            else
            {
                var ancestor = Path.GetDirectoryName(fullPath);
                while (!string.IsNullOrEmpty(ancestor) && !Directory.Exists(ancestor) && !File.Exists(ancestor))
                {
                    var parent = Path.GetDirectoryName(ancestor);
                    if (string.IsNullOrEmpty(parent) || string.Equals(parent, ancestor, StringComparison.OrdinalIgnoreCase))
                        break;
                    ancestor = parent;
                }

                if (!string.IsNullOrEmpty(ancestor) && File.Exists(ancestor))
                    return new CommandError(ErrorCodes.DirectoryNotFound, "A parent path component is a file, not a directory.");

                if (!string.IsNullOrEmpty(ancestor) && Directory.Exists(ancestor))
                {
                    var resolvedAncestor = ResolvePhysicalPath(ancestor);
                    var relative = Path.GetRelativePath(ancestor, fullPath);
                    physicalPath = Path.GetFullPath(Path.Combine(resolvedAncestor, relative));
                }
                else
                {
                    physicalPath = fullPath;
                }
            }
        }
        catch (Exception)
        {
            return new CommandError(ErrorCodes.AccessDenied, "Failed to resolve physical delete path.");
        }

        if (GetMatchingWritableRoot(physicalPath) is null || GetMatchingWritableRoot(fullPath) is null)
            return new CommandError(ErrorCodes.WriteNotAllowed, "The requested path lies outside the configured writable root directories.");

        if (GetMatchingAllowedRoot(physicalPath) is null || GetMatchingAllowedRoot(fullPath) is null)
            return new CommandError(ErrorCodes.PathOutsideAllowedRoot, "The requested path lies outside the configured allowed root directories.");

        foreach (var segment in physicalPath.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (_options.DeniedSegments.Any(ds => string.Equals(ds, segment, StringComparison.OrdinalIgnoreCase)))
                return new CommandError(ErrorCodes.AccessDenied, $"Access denied to delete path containing segment '{segment}'.");
        }

        var fileName = Path.GetFileName(physicalPath);
        if (!string.IsNullOrEmpty(fileName))
        {
            if (_options.DeniedFileNames.Any(df => MatchFileName(fileName, df)) ||
                _options.DeniedWriteFileNames.Any(dw => MatchFileName(fileName, dw)))
                return new CommandError(ErrorCodes.AccessDenied, $"Access denied to delete file '{fileName}'.");

            var extension = Path.GetExtension(physicalPath);
            if (!string.IsNullOrEmpty(extension) &&
                _options.DeniedWriteExtensions.Any(de => string.Equals(de, extension, StringComparison.OrdinalIgnoreCase)))
                return new CommandError(ErrorCodes.AccessDenied, $"Access denied to delete file with extension '{extension}'.");
        }

        if (File.Exists(physicalPath))
        {
            var attributes = File.GetAttributes(physicalPath);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
                return new CommandError(ErrorCodes.AccessDenied, "Reparse points are not allowed on the delete path.");
            if (attributes.HasFlag(FileAttributes.ReadOnly))
                return new CommandError(ErrorCodes.FileReadOnly, "The target file is read-only.");
        }
        else if (!missingOk)
        {
            return new CommandError(ErrorCodes.FileNotFound, "The requested file was not found.");
        }

        normalizedPath = physicalPath;
        return null;
    }

    public CommandError? AuthorizeRemoveDirectory(string rawPath, bool missingOk, out string normalizedPath)
    {
        normalizedPath = string.Empty;

        if (_options.WritableRoots == null || _options.WritableRoots.Count == 0)
            return new CommandError(ErrorCodes.WritableRootNotConfigured, "No writable roots are configured on the agent.");

        var pathError = ValidateBasicPath(rawPath, out var fullPath);
        if (pathError is not null)
            return pathError;

        var reparseError = VerifyNoReparsePointsInOriginalPath(fullPath);
        if (reparseError is not null)
            return reparseError;

        if (File.Exists(fullPath))
            return new CommandError(ErrorCodes.AccessDenied, "The requested path is a file, not a directory.");

        string physicalPath;
        try
        {
            if (Directory.Exists(fullPath))
            {
                var attributes = File.GetAttributes(fullPath);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    return new CommandError(ErrorCodes.AccessDenied, "Reparse points are not allowed on the directory removal path.");

                physicalPath = ResolvePhysicalPath(fullPath);
            }
            else
            {
                var ancestor = Path.GetDirectoryName(fullPath);
                while (!string.IsNullOrEmpty(ancestor) && !Directory.Exists(ancestor) && !File.Exists(ancestor))
                {
                    var parent = Path.GetDirectoryName(ancestor);
                    if (string.IsNullOrEmpty(parent) || string.Equals(parent, ancestor, StringComparison.OrdinalIgnoreCase))
                        break;
                    ancestor = parent;
                }

                if (!string.IsNullOrEmpty(ancestor) && File.Exists(ancestor))
                    return new CommandError(ErrorCodes.DirectoryNotFound, "A parent path component is a file, not a directory.");

                if (!string.IsNullOrEmpty(ancestor) && Directory.Exists(ancestor))
                {
                    var resolvedAncestor = ResolvePhysicalPath(ancestor);
                    var relative = Path.GetRelativePath(ancestor, fullPath);
                    physicalPath = Path.GetFullPath(Path.Combine(resolvedAncestor, relative));
                }
                else
                {
                    physicalPath = fullPath;
                }
            }
        }
        catch (Exception)
        {
            return new CommandError(ErrorCodes.AccessDenied, "Failed to resolve physical directory removal path.");
        }

        if (GetMatchingWritableRoot(physicalPath) is null || GetMatchingWritableRoot(fullPath) is null)
            return new CommandError(ErrorCodes.WriteNotAllowed, "The requested path lies outside the configured writable root directories.");

        if (GetMatchingAllowedRoot(physicalPath) is null || GetMatchingAllowedRoot(fullPath) is null)
            return new CommandError(ErrorCodes.PathOutsideAllowedRoot, "The requested path lies outside the configured allowed root directories.");

        var comparableFullPath = Path.TrimEndingDirectorySeparator(fullPath);
        var comparablePhysicalPath = Path.TrimEndingDirectorySeparator(physicalPath);
        foreach (var configuredRoot in _options.AllowedRoots.Concat(_options.WritableRoots))
        {
            try
            {
                var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredRoot));
                if (string.Equals(comparableFullPath, fullRoot, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(comparablePhysicalPath, fullRoot, StringComparison.OrdinalIgnoreCase))
                    return new CommandError(ErrorCodes.AccessDenied, "Configured root directories cannot be removed.");
            }
            catch
            {
                // Invalid configured roots are ignored by the existing root matching logic.
            }
        }

        foreach (var segment in physicalPath.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (_options.DeniedSegments.Any(ds => string.Equals(ds, segment, StringComparison.OrdinalIgnoreCase)))
                return new CommandError(ErrorCodes.AccessDenied, $"Access denied to directory removal path containing segment '{segment}'.");
        }

        var directoryName = Path.GetFileName(physicalPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!string.IsNullOrEmpty(directoryName) &&
            (_options.DeniedFileNames.Any(df => MatchFileName(directoryName, df)) ||
             _options.DeniedWriteFileNames.Any(dw => MatchFileName(directoryName, dw))))
            return new CommandError(ErrorCodes.AccessDenied, $"Access denied to directory '{directoryName}'.");

        if (Directory.Exists(physicalPath))
        {
            var attributes = File.GetAttributes(physicalPath);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
                return new CommandError(ErrorCodes.AccessDenied, "Reparse points are not allowed on the directory removal path.");
        }
        else if (!missingOk)
        {
            return new CommandError(ErrorCodes.DirectoryNotFound, "The requested directory was not found.");
        }

        normalizedPath = physicalPath;
        return null;
    }

    private CommandError? VerifyNoReparsePointsInOriginalPath(string fullPath)
    {
        var current = fullPath;
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if (Directory.Exists(current) || File.Exists(current))
                {
                    var attrs = File.GetAttributes(current);
                    if (attrs.HasFlag(FileAttributes.ReparsePoint))
                        return new CommandError(ErrorCodes.AccessDenied, "Reparse points are not allowed on the path.");
                }
            }
            catch (Exception)
            {
                // Transient errors on attribute checks – let the main resolution handle it
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                break;
            current = parent;
        }
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

    internal static bool MatchFileName(string fileName, string pattern)
    {
        if (pattern.EndsWith('*'))
        {
            var prefix = pattern.Substring(0, pattern.Length - 1);
            return fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        return string.Equals(fileName, pattern, StringComparison.OrdinalIgnoreCase);
    }
}

using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.Security;

public interface IPathPolicy
{
    /// <summary>
    /// Legacy/general validation for backward compatibility.
    /// </summary>
    CommandError? Validate(string rawPath, out string normalizedPath, bool isDirectory = false);

    /// <summary>
    /// Authorises a file path for read access.
    /// </summary>
    CommandError? AuthorizeReadFile(string rawPath, out string normalizedPath);

    /// <summary>
    /// Authorises a directory path for read access.
    /// </summary>
    CommandError? AuthorizeReadDirectory(string rawPath, out string normalizedPath);

    /// <summary>
    /// Authorises a file path for write access.
    /// </summary>
    /// <param name="rawPath">The raw input path.</param>
    /// <param name="normalizedPath">The output canonical physical path.</param>
    /// <param name="mustExist">If true, the target file must already exist.</param>
    CommandError? AuthorizeWriteFile(string rawPath, out string normalizedPath, bool mustExist = false);

    /// <summary>
    /// Authorises a path for directory creation.
    /// </summary>
    CommandError? AuthorizeCreateDirectory(string rawPath, out string normalizedPath, bool recursive);

    /// <summary>
    /// Authorises a path for checking its metadata (existence and stats).
    /// </summary>
    CommandError? AuthorizeStat(string rawPath, out string normalizedPath);
}

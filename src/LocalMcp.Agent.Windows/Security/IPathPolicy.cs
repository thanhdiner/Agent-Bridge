using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.Security;

public interface IPathPolicy
{
    /// <summary>
    /// Validates if the path is permitted under file access policies, is within allowed roots,
    /// and doesn't contain forbidden directories/filenames or escape attempts.
    /// </summary>
    /// <param name="rawPath">The raw input path.</param>
    /// <param name="normalizedPath">The output normalized absolute canonical path if valid.</param>
    /// <returns>A structured CommandError if invalid, or null if valid.</returns>
    CommandError? Validate(string rawPath, out string normalizedPath, bool isDirectory = false);
}

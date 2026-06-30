using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LocalMcp.BuildingBlocks.Configuration;

namespace AgentBridge.Desktop.Services;

public sealed class InternalTokenStore
{
    public const string TokenEnvironmentVariable = "LOCALMCP_AGENT_TOKEN";

    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("AgentBridge.GatewayAgentToken.v1");

    private readonly string _tokenPath;

    public InternalTokenStore(string? tokenPath = null)
    {
        _tokenPath = string.IsNullOrWhiteSpace(tokenPath)
            ? LocalConfigurationPaths.GetRuntimeTokenFilePath()
            : Path.GetFullPath(tokenPath);
    }

    public string TokenPath => _tokenPath;

    public async Task<string> LoadOrCreateAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_tokenPath)!);

        if (File.Exists(_tokenPath))
        {
            try
            {
                var protectedBytes = await File.ReadAllBytesAsync(_tokenPath, cancellationToken);
                var tokenBytes = ProtectedData.Unprotect(
                    protectedBytes,
                    Entropy,
                    DataProtectionScope.CurrentUser);

                try
                {
                    if (tokenBytes.Length != 32)
                        throw new CryptographicException("Stored runtime token has an invalid length.");

                    return Convert.ToBase64String(tokenBytes);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(tokenBytes);
                }
            }
            catch (Exception ex) when (ex is CryptographicException or IOException)
            {
                var corruptedPath = _tokenPath + $".corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
                File.Move(_tokenPath, corruptedPath, overwrite: true);
            }
        }

        var newTokenBytes = RandomNumberGenerator.GetBytes(32);
        try
        {
            var protectedBytes = ProtectedData.Protect(
                newTokenBytes,
                Entropy,
                DataProtectionScope.CurrentUser);
            var temporaryPath = _tokenPath + $".{Guid.NewGuid():N}.tmp";

            await File.WriteAllBytesAsync(temporaryPath, protectedBytes, cancellationToken);
            File.Move(temporaryPath, _tokenPath, overwrite: true);

            return Convert.ToBase64String(newTokenBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(newTokenBytes);
        }
    }
}

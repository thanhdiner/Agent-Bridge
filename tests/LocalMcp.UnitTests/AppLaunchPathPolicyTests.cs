using LocalMcp.Agent.Windows.Security;
using LocalMcp.BuildingBlocks.Errors;
using Microsoft.Extensions.Options;

namespace LocalMcp.UnitTests;

public sealed class AppLaunchPathPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"localmcp-app-policy-{Guid.NewGuid():N}");

    public AppLaunchPathPolicyTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void AuthorizeExecuteFile_AllowedExe_ReturnsNormalizedPath()
    {
        var executable = Path.Combine(_root, "sample.exe");
        File.WriteAllBytes(executable, [0x4D, 0x5A]);
        var policy = CreatePolicy();

        var error = policy.AuthorizeExecuteFile(executable, out var normalized);

        Assert.Null(error);
        Assert.Equal(Path.GetFullPath(executable), normalized, ignoreCase: true);
    }

    [Fact]
    public void AuthorizeExecuteFile_NonExe_ReturnsInvalidRequest()
    {
        var file = Path.Combine(_root, "sample.txt");
        File.WriteAllText(file, "test");
        var policy = CreatePolicy();

        var error = policy.AuthorizeExecuteFile(file, out _);

        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.InvalidRequest, error!.Code);
    }

    [Fact]
    public void AuthorizeExecuteFile_OutsideAllowedRoot_IsRejected()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(outside, [0x4D, 0x5A]);
        try
        {
            var policy = CreatePolicy();
            var error = policy.AuthorizeExecuteFile(outside, out _);

            Assert.NotNull(error);
            Assert.Equal(ErrorCodes.PathOutsideAllowedRoot, error!.Code);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private PathPolicy CreatePolicy() =>
        new(Options.Create(new FileAccessOptions
        {
            AllowedRoots = [_root],
            WritableRoots = [_root],
            DeniedSegments = [],
            DeniedFileNames = [],
            DeniedWriteFileNames = [],
            DeniedWriteExtensions = []
        }));
}

using System;
using System.Collections.Generic;

namespace LocalMcp.Contracts.Results;

public sealed class CreateDirectoryResult
{
    public required string Path { get; set; }
    public bool Created { get; set; }
    public required List<string> DirectoriesCreated { get; set; }
}

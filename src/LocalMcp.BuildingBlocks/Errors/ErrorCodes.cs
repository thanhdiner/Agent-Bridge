namespace LocalMcp.BuildingBlocks.Errors;

public static class ErrorCodes
{
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string InvalidPath = "INVALID_PATH";
    public const string PathOutsideAllowedRoot = "PATH_OUTSIDE_ALLOWED_ROOT";
    public const string AccessDenied = "ACCESS_DENIED";
    public const string FileNotFound = "FILE_NOT_FOUND";
    public const string FileTooLarge = "FILE_TOO_LARGE";
    public const string BinaryFileNotSupported = "BINARY_FILE_NOT_SUPPORTED";
    public const string AgentOffline = "AGENT_OFFLINE";
    public const string CommandTimeout = "COMMAND_TIMEOUT";
    public const string CommandCancelled = "COMMAND_CANCELLED";
    public const string CommandCapacityExceeded = "COMMAND_CAPACITY_EXCEEDED";
    public const string UnsupportedCommand = "UNSUPPORTED_COMMAND";
    public const string InternalError = "INTERNAL_ERROR";
    public const string DirectoryNotFound = "DIRECTORY_NOT_FOUND";
    public const string DirectoryNotEmpty = "DIRECTORY_NOT_EMPTY";
    public const string InvalidSearchMode = "INVALID_SEARCH_MODE";
    public const string SearchQueryRequired = "SEARCH_QUERY_REQUIRED";
    public const string TreeDepthLimitExceeded = "TREE_DEPTH_LIMIT_EXCEEDED";
    public const string ResultLimitExceeded = "RESULT_LIMIT_EXCEEDED";

    // Write-related error codes
    public const string WriteNotAllowed = "WRITE_NOT_ALLOWED";
    public const string WritableRootNotConfigured = "WRITABLE_ROOT_NOT_CONFIGURED";
    public const string FileAlreadyExists = "FILE_ALREADY_EXISTS";
    public const string FileConflict = "FILE_CONFLICT";
    public const string ExpectedHashRequired = "EXPECTED_HASH_REQUIRED";
    public const string HashMismatch = "HASH_MISMATCH";
    public const string PatchEditsRequired = "PATCH_EDITS_REQUIRED";
    public const string PatchTargetNotFound = "PATCH_TARGET_NOT_FOUND";
    public const string PatchTargetAmbiguous = "PATCH_TARGET_AMBIGUOUS";
    public const string PatchEditsOverlap = "PATCH_EDITS_OVERLAP";
    public const string UnsupportedTextEncoding = "UNSUPPORTED_TEXT_ENCODING";
    public const string FileReadOnly = "FILE_READ_ONLY";
    public const string AtomicReplaceFailed = "ATOMIC_REPLACE_FAILED";

    // Move / Copy error codes
    public const string ConcurrencyConflict = "CONCURRENCY_CONFLICT";
    public const string ReadError = "READ_ERROR";
    public const string WriteError = "WRITE_ERROR";
}

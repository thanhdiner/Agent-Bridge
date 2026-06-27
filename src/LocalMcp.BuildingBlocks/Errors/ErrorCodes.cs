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
    public const string InvalidSearchMode = "INVALID_SEARCH_MODE";
    public const string SearchQueryRequired = "SEARCH_QUERY_REQUIRED";
    public const string TreeDepthLimitExceeded = "TREE_DEPTH_LIMIT_EXCEEDED";
    public const string ResultLimitExceeded = "RESULT_LIMIT_EXCEEDED";
}

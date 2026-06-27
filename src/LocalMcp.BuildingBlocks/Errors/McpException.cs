namespace LocalMcp.BuildingBlocks.Errors;

public class McpException : Exception
{
    public string ErrorCode { get; }

    public McpException(string errorCode, string message) : base(message)
    {
        ErrorCode = errorCode;
    }

    public McpException(string errorCode, string message, Exception innerException) : base(message, innerException)
    {
        ErrorCode = errorCode;
    }
}

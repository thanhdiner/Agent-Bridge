using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Results;

namespace LocalMcp.Agent.Windows.UiAutomation;

public sealed partial class UiAutomationExecutor
{
    private const uint ClipboardFormatUnicodeText = 13;
    private const uint GlobalMemoryMoveable = 0x0002;
    private const int ClipboardOpenAttempts = 20;
    private const int ClipboardOpenDelayMs = 25;
    internal const int MaxClipboardCharacters = 1_048_576;

    public Task<CommandResult<ClipboardGetResult>> ClipboardGetAsync(
        int maxCharacters,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (maxCharacters is < 1 or > MaxClipboardCharacters)
        {
            return Task.FromResult(ClipboardGetFailure(
                commandId,
                ErrorCodes.InvalidRequest,
                $"maxCharacters must be between 1 and {MaxClipboardCharacters}."));
        }
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(ClipboardGetFailure(
                commandId,
                ErrorCodes.ClipboardUnavailable,
                "Clipboard access is only available on Windows agents."));
        }

        return Task.Run(
            () => ClipboardGetWindows(maxCharacters, commandId, cancellationToken),
            cancellationToken);
    }

    public Task<CommandResult<ClipboardSetResult>> ClipboardSetAsync(
        string text,
        bool verify,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (text is null)
        {
            return Task.FromResult(ClipboardSetFailure(
                commandId,
                ErrorCodes.InvalidRequest,
                "text is required. Use an empty string to clear the clipboard text."));
        }
        if (text.Length > MaxClipboardCharacters)
        {
            return Task.FromResult(ClipboardSetFailure(
                commandId,
                ErrorCodes.InvalidRequest,
                $"text must be at most {MaxClipboardCharacters} UTF-16 characters."));
        }
        if (text.Contains('\0'))
        {
            return Task.FromResult(ClipboardSetFailure(
                commandId,
                ErrorCodes.InvalidRequest,
                "text must not contain NUL characters."));
        }
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(ClipboardSetFailure(
                commandId,
                ErrorCodes.ClipboardUnavailable,
                "Clipboard access is only available on Windows agents."));
        }

        return Task.Run(
            () => ClipboardSetWindows(text, verify, commandId, cancellationToken),
            cancellationToken);
    }

    [SupportedOSPlatform("windows")]
    private static CommandResult<ClipboardGetResult> ClipboardGetWindows(
        int maxCharacters,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (!IsClipboardFormatAvailable(ClipboardFormatUnicodeText))
        {
            return new CommandResult<ClipboardGetResult>
            {
                CommandId = commandId,
                Success = true,
                Data = new ClipboardGetResult
                {
                    HasText = false,
                    Text = null,
                    CharacterCount = 0,
                    CharacterCountExact = true,
                    ReturnedCharacters = 0,
                    Truncated = false
                }
            };
        }

        var read = ReadClipboardText(cancellationToken);
        if (!read.Success)
            return ClipboardGetFailure(commandId, read.ErrorCode!, read.ErrorMessage!);

        if (read.Text is null)
        {
            return new CommandResult<ClipboardGetResult>
            {
                CommandId = commandId,
                Success = true,
                Data = new ClipboardGetResult
                {
                    HasText = false,
                    Text = null,
                    CharacterCount = 0,
                    CharacterCountExact = true,
                    ReturnedCharacters = 0,
                    Truncated = false
                }
            };
        }

        var text = read.Text;
        var truncatedByRequest = text.Length > maxCharacters;
        var returned = truncatedByRequest ? text[..maxCharacters] : text;
        return new CommandResult<ClipboardGetResult>
        {
            CommandId = commandId,
            Success = true,
            Data = new ClipboardGetResult
            {
                HasText = true,
                Text = returned,
                CharacterCount = read.CharacterCount,
                CharacterCountExact = read.CharacterCountExact,
                ReturnedCharacters = returned.Length,
                Truncated = truncatedByRequest || !read.CharacterCountExact
            }
        };
    }

    [SupportedOSPlatform("windows")]
    private static CommandResult<ClipboardSetResult> ClipboardSetWindows(
        string text,
        bool verify,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var byteCount = checked((text.Length + 1) * sizeof(char));
        var memory = GlobalAlloc(GlobalMemoryMoveable, (UIntPtr)(uint)byteCount);
        if (memory == IntPtr.Zero)
            return ClipboardSetFailure(commandId, ErrorCodes.ClipboardWriteFailed, "Windows could not allocate clipboard memory.");

        var ownershipTransferred = false;
        try
        {
            var pointer = GlobalLock(memory);
            if (pointer == IntPtr.Zero)
                return ClipboardSetFailure(commandId, ErrorCodes.ClipboardWriteFailed, "Windows could not lock clipboard memory.");

            try
            {
                if (text.Length > 0)
                    Marshal.Copy(text.ToCharArray(), 0, pointer, text.Length);
                Marshal.WriteInt16(pointer, text.Length * sizeof(char), 0);
            }
            finally
            {
                GlobalUnlock(memory);
            }

            if (!TryOpenClipboard(cancellationToken))
                return ClipboardSetFailure(commandId, ErrorCodes.ClipboardUnavailable, "The clipboard remained busy after bounded retries.");

            try
            {
                if (!EmptyClipboard())
                    return ClipboardSetFailure(commandId, ErrorCodes.ClipboardWriteFailed, "Windows could not clear the current clipboard contents.");
                if (SetClipboardData(ClipboardFormatUnicodeText, memory) == IntPtr.Zero)
                    return ClipboardSetFailure(commandId, ErrorCodes.ClipboardWriteFailed, "Windows rejected the Unicode clipboard data.");
                ownershipTransferred = true;
            }
            finally
            {
                CloseClipboard();
            }
        }
        finally
        {
            if (!ownershipTransferred && memory != IntPtr.Zero)
                GlobalFree(memory);
        }

        var verified = false;
        if (verify)
        {
            var read = ReadClipboardText(cancellationToken);
            if (!read.Success || !read.CharacterCountExact || !string.Equals(read.Text, text, StringComparison.Ordinal))
            {
                return ClipboardSetFailure(
                    commandId,
                    ErrorCodes.ClipboardVerificationFailed,
                    "Clipboard text did not match after the write completed.");
            }
            verified = true;
        }

        return new CommandResult<ClipboardSetResult>
        {
            CommandId = commandId,
            Success = true,
            Data = new ClipboardSetResult
            {
                CharacterCount = text.Length,
                Verified = verified
            }
        };
    }

    [SupportedOSPlatform("windows")]
    private static ClipboardReadAttempt ReadClipboardText(CancellationToken cancellationToken)
    {
        if (!IsClipboardFormatAvailable(ClipboardFormatUnicodeText))
            return ClipboardReadAttempt.NoText();
        if (!TryOpenClipboard(cancellationToken))
            return ClipboardReadAttempt.Failure(ErrorCodes.ClipboardUnavailable, "The clipboard remained busy after bounded retries.");

        try
        {
            var handle = GetClipboardData(ClipboardFormatUnicodeText);
            if (handle == IntPtr.Zero)
                return ClipboardReadAttempt.Failure(ErrorCodes.ClipboardReadFailed, "Windows returned no Unicode clipboard handle.");

            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
                return ClipboardReadAttempt.Failure(ErrorCodes.ClipboardReadFailed, "Windows could not lock the clipboard data.");

            try
            {
                var sizeBytes = GlobalSize(handle).ToUInt64();
                if (sizeBytes < sizeof(char))
                    return ClipboardReadAttempt.SuccessResult(string.Empty, 0, true);

                var availableCharacters = (int)Math.Min((ulong)int.MaxValue, sizeBytes / sizeof(char));
                var boundedCharacters = Math.Min(availableCharacters, MaxClipboardCharacters + 1);
                var raw = Marshal.PtrToStringUni(pointer, boundedCharacters) ?? string.Empty;
                var terminatorIndex = raw.IndexOf('\0');
                var exact = terminatorIndex >= 0;
                var text = exact ? raw[..terminatorIndex] : raw;

                if (text.Length > MaxClipboardCharacters)
                    text = text[..MaxClipboardCharacters];

                return ClipboardReadAttempt.SuccessResult(
                    text,
                    exact ? terminatorIndex : text.Length,
                    exact);
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool TryOpenClipboard(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < ClipboardOpenAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (OpenClipboard(IntPtr.Zero))
                return true;
            Thread.Sleep(ClipboardOpenDelayMs);
        }

        return false;
    }

    private static CommandResult<ClipboardGetResult> ClipboardGetFailure(Guid commandId, string code, string message) => new()
    {
        CommandId = commandId,
        Success = false,
        Error = new CommandError(code, message)
    };

    private static CommandResult<ClipboardSetResult> ClipboardSetFailure(Guid commandId, string code, string message) => new()
    {
        CommandId = commandId,
        Success = false,
        Error = new CommandError(code, message)
    };

    private readonly record struct ClipboardReadAttempt(
        bool Success,
        string? Text,
        int CharacterCount,
        bool CharacterCountExact,
        string? ErrorCode,
        string? ErrorMessage)
    {
        public static ClipboardReadAttempt SuccessResult(string text, int characterCount, bool exact) =>
            new(true, text, characterCount, exact, null, null);

        public static ClipboardReadAttempt NoText() =>
            new(true, null, 0, true, null, null);

        public static ClipboardReadAttempt Failure(string code, string message) =>
            new(false, null, 0, false, code, message);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr newOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr GlobalSize(IntPtr memory);
}

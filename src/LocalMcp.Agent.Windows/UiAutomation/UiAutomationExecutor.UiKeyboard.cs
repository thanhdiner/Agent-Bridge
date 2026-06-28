using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Interop.UIAutomationClient;
using LocalMcp.BuildingBlocks.Errors;
using LocalMcp.Contracts.Results;
namespace LocalMcp.Agent.Windows.UiAutomation;
public sealed partial class UiAutomationExecutor
{
    private const int MaxKeyChordCharacters = 64;
    private const int MaxTypedTextCharacters = 4096;
    private const uint InputKeyboard = 1;
    private const uint KeyEventExtendedKey = 0x0001;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;
    private const ushort VkBack = 0x08;
    private const ushort VkTab = 0x09;
    private const ushort VkReturn = 0x0D;
    private const ushort VkShift = 0x10;
    private const ushort VkControl = 0x11;
    private const ushort VkMenu = 0x12;
    private const ushort VkEscape = 0x1B;
    private const ushort VkSpace = 0x20;
    private const ushort VkPageUp = 0x21;
    private const ushort VkPageDown = 0x22;
    private const ushort VkEnd = 0x23;
    private const ushort VkHome = 0x24;
    private const ushort VkLeft = 0x25;
    private const ushort VkUp = 0x26;
    private const ushort VkRight = 0x27;
    private const ushort VkDown = 0x28;
    private const ushort VkInsert = 0x2D;
    private const ushort VkDelete = 0x2E;
    private const ushort VkF1 = 0x70;
    private const ushort VkF4 = 0x73;
    public async Task<CommandResult<UiPressKeyResult>> PressKeyAsync(
        string windowHandle,
        string keys,
        string? automationId,
        string? name,
        string? controlType,
        int occurrenceIndex,
        bool focusWindow,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (!TryParseWindowHandle(windowHandle, out var handle))
            return PressKeyFailure(commandId, ErrorCodes.InvalidRequest, "windowHandle must be a non-zero decimal or 0x-prefixed hexadecimal window handle.");
        if (!TryParseKeyChord(keys, out var chord, out var parseError))
            return PressKeyFailure(commandId, ErrorCodes.InvalidRequest, parseError);
        if (!ValidateKeyboardSelector(automationId, name, controlType, occurrenceIndex, requireSelector: false, out var selectorError))
            return PressKeyFailure(commandId, ErrorCodes.InvalidRequest, selectorError);
        if (!OperatingSystem.IsWindows())
            return PressKeyFailure(commandId, ErrorCodes.UiAutomationUnavailable, "Keyboard input is only available on Windows agents.");
        try
        {
            return await Task.Run(
                () => PressKeyWindows(
                    handle,
                    chord,
                    automationId?.Trim(),
                    name?.Trim(),
                    controlType?.Trim(),
                    occurrenceIndex,
                    focusWindow,
                    commandId,
                    cancellationToken),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return PressKeyFailure(commandId, ErrorCodes.CommandCancelled, "The keyboard input request was cancelled.");
        }
        catch (COMException ex)
        {
            _logger.LogWarning(ex, "UI Automation keyboard focus failure for command {CommandId}", commandId);
            return PressKeyFailure(commandId, ErrorCodes.UiKeyboardInputFailed, "Windows UI Automation could not prepare the requested keyboard target.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected keyboard input failure for command {CommandId}", commandId);
            return PressKeyFailure(commandId, ErrorCodes.InternalError, "An unexpected error occurred while sending keyboard input.");
        }
    }
    public async Task<CommandResult<UiTypeTextResult>> TypeTextAsync(
        string windowHandle,
        string text,
        string? automationId,
        string? name,
        string? controlType,
        int occurrenceIndex,
        bool focusWindow,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (!TryParseWindowHandle(windowHandle, out var handle))
            return TypeTextFailure(commandId, ErrorCodes.InvalidRequest, "windowHandle must be a non-zero decimal or 0x-prefixed hexadecimal window handle.");
        if (string.IsNullOrEmpty(text))
            return TypeTextFailure(commandId, ErrorCodes.InvalidRequest, "text is required and must not be empty.");
        if (text.Length > MaxTypedTextCharacters)
            return TypeTextFailure(commandId, ErrorCodes.InvalidRequest, $"text must be at most {MaxTypedTextCharacters} UTF-16 characters.");
        if (text.Contains('\0'))
            return TypeTextFailure(commandId, ErrorCodes.InvalidRequest, "text must not contain NUL characters.");
        if (!ValidateKeyboardSelector(automationId, name, controlType, occurrenceIndex, requireSelector: true, out var selectorError))
            return TypeTextFailure(commandId, ErrorCodes.InvalidRequest, selectorError);
        if (!OperatingSystem.IsWindows())
            return TypeTextFailure(commandId, ErrorCodes.UiAutomationUnavailable, "Keyboard input is only available on Windows agents.");
        try
        {
            return await Task.Run(
                () => TypeTextWindows(
                    handle,
                    text,
                    automationId?.Trim(),
                    name?.Trim(),
                    controlType?.Trim(),
                    occurrenceIndex,
                    focusWindow,
                    commandId,
                    cancellationToken),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return TypeTextFailure(commandId, ErrorCodes.CommandCancelled, "The text input request was cancelled.");
        }
        catch (COMException ex)
        {
            _logger.LogWarning(ex, "UI Automation text focus failure for command {CommandId}", commandId);
            return TypeTextFailure(commandId, ErrorCodes.UiKeyboardInputFailed, "Windows UI Automation could not prepare the requested text target.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected text input failure for command {CommandId}", commandId);
            return TypeTextFailure(commandId, ErrorCodes.InternalError, "An unexpected error occurred while typing text.");
        }
    }
    [SupportedOSPlatform("windows")]
    private static CommandResult<UiPressKeyResult> PressKeyWindows(
        IntPtr handle,
        ParsedKeyChord chord,
        string? automationId,
        string? name,
        string? controlType,
        int occurrenceIndex,
        bool focusWindow,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        IUIAutomation? automation = null;
        IUIAutomationTreeWalker? walker = null;
        IUIAutomationElement? root = null;
        IUIAutomationElement? match = null;
        try
        {
            var prepareError = PrepareKeyboardTarget(
                handle,
                automationId,
                name,
                controlType,
                occurrenceIndex,
                focusWindow,
                cancellationToken,
                out automation,
                out walker,
                out root,
                out match);
            if (prepareError is not null)
                return PressKeyFailure(commandId, prepareError.Code, prepareError.Message);
            cancellationToken.ThrowIfCancellationRequested();
            if (GetForegroundWindow() != handle)
                return PressKeyFailure(commandId, ErrorCodes.UiForegroundMismatch, "The foreground window changed before keyboard input could be sent.");
            var inputs = BuildChordInputs(chord);
            var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>());
            if (sent != (uint)inputs.Length)
            {
                ReleaseModifierKeys(chord.Modifiers);
                return PressKeyFailure(commandId, ErrorCodes.UiKeyboardInputFailed, $"SendInput accepted {sent} of {inputs.Length} keyboard events.");
            }
            return new CommandResult<UiPressKeyResult>
            {
                CommandId = commandId,
                Success = true,
                Data = new UiPressKeyResult
                {
                    WindowHandle = FormatWindowHandle(handle),
                    Name = match is null ? null : LimitMetadata(ReadOrDefault(() => match.CurrentName, string.Empty)),
                    AutomationId = match is null ? null : LimitMetadata(ReadOrDefault(() => match.CurrentAutomationId, string.Empty)),
                    ControlType = match is null ? null : GetControlTypeName(ReadOrDefault(() => match.CurrentControlType, 0)),
                    Bounds = match is null ? null : ReadBounds(match),
                    Keys = chord.Normalized,
                    InputsSent = (int)sent,
                    TargetedControl = match is not null,
                    OccurrenceIndex = occurrenceIndex
                }
            };
        }
        finally
        {
            if (!ReferenceEquals(match, root))
                ReleaseComObject(match);
            ReleaseComObject(root);
            ReleaseComObject(walker);
            ReleaseComObject(automation);
        }
    }
    [SupportedOSPlatform("windows")]
    private static CommandResult<UiTypeTextResult> TypeTextWindows(
        IntPtr handle,
        string text,
        string? automationId,
        string? name,
        string? controlType,
        int occurrenceIndex,
        bool focusWindow,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        IUIAutomation? automation = null;
        IUIAutomationTreeWalker? walker = null;
        IUIAutomationElement? root = null;
        IUIAutomationElement? match = null;
        try
        {
            var prepareError = PrepareKeyboardTarget(
                handle,
                automationId,
                name,
                controlType,
                occurrenceIndex,
                focusWindow,
                cancellationToken,
                out automation,
                out walker,
                out root,
                out match);
            if (prepareError is not null)
                return TypeTextFailure(commandId, prepareError.Code, prepareError.Message);
            if (match is null)
                return TypeTextFailure(commandId, ErrorCodes.UiElementNotFound, "No UI control matched the supplied selector and occurrenceIndex.");
            cancellationToken.ThrowIfCancellationRequested();
            if (GetForegroundWindow() != handle)
                return TypeTextFailure(commandId, ErrorCodes.UiForegroundMismatch, "The foreground window changed before text input could be sent.");
            var inputs = BuildUnicodeInputs(text);
            var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>());
            if (sent != (uint)inputs.Length)
                return TypeTextFailure(commandId, ErrorCodes.UiKeyboardInputFailed, $"SendInput accepted {sent} of {inputs.Length} keyboard events.");
            return new CommandResult<UiTypeTextResult>
            {
                CommandId = commandId,
                Success = true,
                Data = new UiTypeTextResult
                {
                    WindowHandle = FormatWindowHandle(handle),
                    Name = LimitMetadata(ReadOrDefault(() => match.CurrentName, string.Empty)),
                    AutomationId = LimitMetadata(ReadOrDefault(() => match.CurrentAutomationId, string.Empty)),
                    ControlType = GetControlTypeName(ReadOrDefault(() => match.CurrentControlType, 0)),
                    Bounds = ReadBounds(match),
                    CharacterCount = text.Length,
                    InputsSent = (int)sent,
                    IsPassword = ReadOrDefault(() => match.CurrentIsPassword != 0, false),
                    OccurrenceIndex = occurrenceIndex
                }
            };
        }
        finally
        {
            if (!ReferenceEquals(match, root))
                ReleaseComObject(match);
            ReleaseComObject(root);
            ReleaseComObject(walker);
            ReleaseComObject(automation);
        }
    }
    [SupportedOSPlatform("windows")]
    private static CommandError? PrepareKeyboardTarget(
        IntPtr handle,
        string? automationId,
        string? name,
        string? controlType,
        int occurrenceIndex,
        bool focusWindow,
        CancellationToken cancellationToken,
        out IUIAutomation? automation,
        out IUIAutomationTreeWalker? walker,
        out IUIAutomationElement? root,
        out IUIAutomationElement? match)
    {
        automation = null;
        walker = null;
        root = null;
        match = null;
        if (!IsWindow(handle))
            return new CommandError(ErrorCodes.WindowNotFound, "The requested window handle does not identify a live window.");
        cancellationToken.ThrowIfCancellationRequested();
        automation = CreateAutomationClient();
        root = automation.ElementFromHandle(handle);
        if (root is null)
            return new CommandError(ErrorCodes.WindowNotFound, "Windows UI Automation could not resolve the requested window.");
        if (focusWindow)
        {
            if (IsIconic(handle))
            {
                ShowWindowAsync(handle, ShowWindowRestore);
                if (!WaitForWindowState(() => !IsIconic(handle), cancellationToken))
                    return new CommandError(ErrorCodes.WindowFocusFailed, "The requested window could not be restored from its minimized state.");
            }
            RequestForegroundActivation(handle);
            root.SetFocus();
            if (!WaitForWindowState(() => GetForegroundWindow() == handle, cancellationToken))
                return new CommandError(ErrorCodes.WindowFocusFailed, "Windows did not grant foreground activation to the requested window.");
        }
        else if (GetForegroundWindow() != handle)
        {
            return new CommandError(ErrorCodes.UiForegroundMismatch, "The requested window is not currently foreground and focusWindow is false.");
        }
        var hasSelector = !string.IsNullOrEmpty(automationId) || !string.IsNullOrEmpty(name);
        if (hasSelector)
        {
            walker = automation.ControlViewWalker;
            var seen = 0;
            var visited = 0;
            match = FindTarget(
                root,
                walker,
                automationId,
                name,
                controlType,
                occurrenceIndex,
                ref seen,
                ref visited,
                cancellationToken);
            if (match is null)
                return new CommandError(ErrorCodes.UiElementNotFound, "No UI control matched the supplied selector and occurrenceIndex.");
            var matchedElement = match;
            if (ReadOrDefault(() => matchedElement.CurrentIsEnabled == 0, true))
                return new CommandError(ErrorCodes.UiKeyboardInputFailed, "The matched control is disabled.");
            matchedElement.SetFocus();
        }
        if (GetForegroundWindow() != handle)
            return new CommandError(ErrorCodes.UiForegroundMismatch, "The foreground window changed while preparing the keyboard target.");
        return null;
    }
    internal static bool ValidateKeyboardSelector(
        string? automationId,
        string? name,
        string? controlType,
        int occurrenceIndex,
        bool requireSelector,
        out string error)
    {
        error = string.Empty;
        var hasSelector = !string.IsNullOrWhiteSpace(automationId) || !string.IsNullOrWhiteSpace(name);
        if (requireSelector && !hasSelector)
        {
            error = "automationId or name is required.";
            return false;
        }
        if (!hasSelector && !string.IsNullOrWhiteSpace(controlType))
        {
            error = "controlType requires automationId or name.";
            return false;
        }
        if (occurrenceIndex is < 0 or > 1000)
        {
            error = "occurrenceIndex must be between 0 and 1000.";
            return false;
        }
        if (!hasSelector && occurrenceIndex != 0)
        {
            error = "occurrenceIndex must be 0 when no control selector is supplied.";
            return false;
        }
        if (automationId?.Length > 1024 || name?.Length > 1024 || controlType?.Length > 128)
        {
            error = "Selector values exceed their maximum lengths.";
            return false;
        }
        return true;
    }
    internal static bool TryParseKeyChord(string? keys, out ParsedKeyChord chord, out string error)
    {
        chord = default;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(keys))
        {
            error = "keys is required.";
            return false;
        }
        if (keys.Length > MaxKeyChordCharacters || keys.Any(char.IsControl))
        {
            error = $"keys must be at most {MaxKeyChordCharacters} characters and contain no control characters.";
            return false;
        }
        var tokens = keys.Split('+', StringSplitOptions.TrimEntries);
        if (tokens.Length is < 1 or > 4 || tokens.Any(string.IsNullOrEmpty))
        {
            error = "keys must be one key or a chord of up to four '+'-separated tokens.";
            return false;
        }
        var hasControl = false;
        var hasShift = false;
        var hasAlt = false;
        ushort key = 0;
        string? keyName = null;
        foreach (var rawToken in tokens)
        {
            var token = rawToken.ToUpperInvariant();
            switch (token)
            {
                case "CTRL":
                case "CONTROL":
                    if (hasControl) { error = "CTRL may only appear once."; return false; }
                    hasControl = true;
                    continue;
                case "SHIFT":
                    if (hasShift) { error = "SHIFT may only appear once."; return false; }
                    hasShift = true;
                    continue;
                case "ALT":
                    if (hasAlt) { error = "ALT may only appear once."; return false; }
                    hasAlt = true;
                    continue;
                case "WIN":
                case "WINDOWS":
                case "META":
                    error = "Windows-key shortcuts are not allowed.";
                    return false;
            }
            if (key != 0)
            {
                error = "A key chord must contain exactly one non-modifier key.";
                return false;
            }
            if (!TryMapVirtualKey(token, out key, out keyName))
            {
                error = $"Unsupported key token: {rawToken}.";
                return false;
            }
        }
        if (key == 0 || keyName is null)
        {
            error = "A key chord must contain one non-modifier key.";
            return false;
        }
        if ((hasAlt && key == VkF4)
            || (hasControl && hasAlt && key == VkDelete)
            || (hasAlt && key is VkTab or VkEscape)
            || (hasControl && key == VkEscape)
            || (hasControl && hasShift && key == VkEscape))
        {
            error = "This shortcut is blocked because it can close, switch away from, or escape the target window.";
            return false;
        }
        var modifiers = new List<ushort>(3);
        var names = new List<string>(4);
        if (hasControl) { modifiers.Add(VkControl); names.Add("CTRL"); }
        if (hasShift) { modifiers.Add(VkShift); names.Add("SHIFT"); }
        if (hasAlt) { modifiers.Add(VkMenu); names.Add("ALT"); }
        names.Add(keyName);
        chord = new ParsedKeyChord(modifiers.ToArray(), key, string.Join('+', names));
        return true;
    }
    private static bool TryMapVirtualKey(string token, out ushort key, out string? normalized)
    {
        key = 0;
        normalized = null;
        if (token.Length == 1 && (token[0] is >= 'A' and <= 'Z' or >= '0' and <= '9'))
        {
            key = token[0];
            normalized = token;
            return true;
        }
        (key, normalized) = token switch
        {
            "ENTER" or "RETURN" => (VkReturn, "ENTER"),
            "ESC" or "ESCAPE" => (VkEscape, "ESC"),
            "TAB" => (VkTab, "TAB"),
            "BACKSPACE" or "BACK" => (VkBack, "BACKSPACE"),
            "DELETE" or "DEL" => (VkDelete, "DELETE"),
            "INSERT" or "INS" => (VkInsert, "INSERT"),
            "HOME" => (VkHome, "HOME"),
            "END" => (VkEnd, "END"),
            "PAGEUP" or "PGUP" => (VkPageUp, "PAGEUP"),
            "PAGEDOWN" or "PGDN" => (VkPageDown, "PAGEDOWN"),
            "LEFT" => (VkLeft, "LEFT"),
            "UP" => (VkUp, "UP"),
            "RIGHT" => (VkRight, "RIGHT"),
            "DOWN" => (VkDown, "DOWN"),
            "SPACE" => (VkSpace, "SPACE"),
            _ => (0, null)
        };
        if (key != 0)
            return true;
        if (token.Length >= 2
            && token[0] == 'F'
            && int.TryParse(token[1..], out var functionNumber)
            && functionNumber is >= 1 and <= 12)
        {
            key = (ushort)(VkF1 + functionNumber - 1);
            normalized = $"F{functionNumber}";
            return true;
        }
        return false;
    }
    private static NativeInput[] BuildChordInputs(ParsedKeyChord chord)
    {
        var inputs = new NativeInput[(chord.Modifiers.Length * 2) + 2];
        var index = 0;
        foreach (var modifier in chord.Modifiers)
            inputs[index++] = BuildVirtualKeyInput(modifier, keyUp: false);
        inputs[index++] = BuildVirtualKeyInput(chord.Key, keyUp: false);
        inputs[index++] = BuildVirtualKeyInput(chord.Key, keyUp: true);
        for (var modifierIndex = chord.Modifiers.Length - 1; modifierIndex >= 0; modifierIndex--)
            inputs[index++] = BuildVirtualKeyInput(chord.Modifiers[modifierIndex], keyUp: true);
        return inputs;
    }
    private static NativeInput[] BuildUnicodeInputs(string text)
    {
        var inputs = new NativeInput[text.Length * 2];
        var index = 0;
        foreach (var character in text)
        {
            inputs[index++] = BuildUnicodeInput(character, keyUp: false);
            inputs[index++] = BuildUnicodeInput(character, keyUp: true);
        }
        return inputs;
    }
    private static NativeInput BuildVirtualKeyInput(ushort key, bool keyUp) => new()
    {
        Type = InputKeyboard,
        Data = new NativeInputUnion
        {
            Keyboard = new NativeKeyboardInput
            {
                VirtualKey = key,
                ScanCode = 0,
                Flags = (keyUp ? KeyEventKeyUp : 0) | (IsExtendedKey(key) ? KeyEventExtendedKey : 0),
                Time = 0,
                ExtraInfo = UIntPtr.Zero
            }
        }
    };
    private static NativeInput BuildUnicodeInput(char character, bool keyUp) => new()
    {
        Type = InputKeyboard,
        Data = new NativeInputUnion
        {
            Keyboard = new NativeKeyboardInput
            {
                VirtualKey = 0,
                ScanCode = character,
                Flags = KeyEventUnicode | (keyUp ? KeyEventKeyUp : 0),
                Time = 0,
                ExtraInfo = UIntPtr.Zero
            }
        }
    };
    private static bool IsExtendedKey(ushort key) => key is
        VkInsert or VkDelete or VkHome or VkEnd or VkPageUp or VkPageDown or VkLeft or VkUp or VkRight or VkDown;
    [SupportedOSPlatform("windows")]
    private static void ReleaseModifierKeys(IReadOnlyList<ushort> modifiers)
    {
        if (modifiers.Count == 0)
            return;
        var releases = new NativeInput[modifiers.Count];
        for (var index = 0; index < modifiers.Count; index++)
            releases[index] = BuildVirtualKeyInput(modifiers[modifiers.Count - 1 - index], keyUp: true);
        SendInput((uint)releases.Length, releases, Marshal.SizeOf<NativeInput>());
    }
    private static CommandResult<UiPressKeyResult> PressKeyFailure(Guid commandId, string code, string message) => new()
    {
        CommandId = commandId,
        Success = false,
        Error = new CommandError(code, message)
    };
    private static CommandResult<UiTypeTextResult> TypeTextFailure(Guid commandId, string code, string message) => new()
    {
        CommandId = commandId,
        Success = false,
        Error = new CommandError(code, message)
    };
    internal readonly record struct ParsedKeyChord(ushort[] Modifiers, ushort Key, string Normalized);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public uint Type;
        public NativeInputUnion Data;
    }
    [StructLayout(LayoutKind.Explicit)]
    private struct NativeInputUnion
    {
        [FieldOffset(0)]
        public NativeMouseInput Mouse;

        [FieldOffset(0)]
        public NativeKeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeKeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, NativeInput[] inputs, int inputSize);
}
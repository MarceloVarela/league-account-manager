using System.Security.Cryptography;
using LAM.Core.Vault;

namespace LAM.Core.Login.Input;

/// <summary>
/// Types text and presses keys through <c>SendInput</c>.
///
/// Characters are sent as Unicode code units rather than virtual key codes. That matters for two
/// reasons: it is independent of the active keyboard layout, so a password typed under a Portuguese
/// layout arrives the same as under a US one; and it handles characters that have no key at all,
/// which a virtual-key approach silently mangles.
///
/// Every keystroke is gated on <see cref="FocusGuard"/>, so the sequence stops the instant the
/// target window loses focus.
/// </summary>
public sealed class InputSender
{
    private readonly FocusGuard _guard;
    private readonly int _minDelayMs;
    private readonly int _maxDelayMs;

    public InputSender(FocusGuard guard, int minDelayMs = 12, int maxDelayMs = 28)
    {
        _guard = guard;
        _minDelayMs = minDelayMs;
        _maxDelayMs = Math.Max(minDelayMs, maxDelayMs);
    }

    /// <summary>
    /// Types a secret without ever materialising it as a string.
    ///
    /// The bytes are decoded one character at a time and the scratch buffer is wiped afterwards, so
    /// the password does not linger in the heap as an immutable string the GC may copy around.
    /// </summary>
    public async Task TypeSecretAsync(SecretBuffer secret, CancellationToken cancellationToken)
    {
        var characters = System.Text.Encoding.UTF8.GetChars(secret.Bytes);
        try
        {
            foreach (var character in characters)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _guard.Verify();
                SendCharacter(character);
                await DelayAsync(cancellationToken);
            }
        }
        finally
        {
            Array.Clear(characters);
        }
    }

    public async Task TypeAsync(string text, CancellationToken cancellationToken)
    {
        foreach (var character in text)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _guard.Verify();
            SendCharacter(character);
            await DelayAsync(cancellationToken);
        }
    }

    public async Task PressAsync(ushort virtualKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _guard.Verify();
        SendVirtualKey(virtualKey, keyUp: false);
        SendVirtualKey(virtualKey, keyUp: true);
        await DelayAsync(cancellationToken);
    }

    public Task PressTabAsync(CancellationToken cancellationToken) => PressAsync(NativeInput.VkTab, cancellationToken);

    public Task PressEnterAsync(CancellationToken cancellationToken) => PressAsync(NativeInput.VkReturn, cancellationToken);

    public Task PressSpaceAsync(CancellationToken cancellationToken) => PressAsync(NativeInput.VkSpace, cancellationToken);

    /// <summary>
    /// Ctrl+A then Delete. Clears a field that the client may have pre-filled with a remembered
    /// username — appending to it would produce a garbled login and a wasted failed attempt.
    /// </summary>
    public async Task ClearFieldAsync(CancellationToken cancellationToken)
    {
        _guard.Verify();
        SendVirtualKey(NativeInput.VkControl, keyUp: false);
        SendVirtualKey(NativeInput.VkA, keyUp: false);
        SendVirtualKey(NativeInput.VkA, keyUp: true);
        SendVirtualKey(NativeInput.VkControl, keyUp: true);
        await DelayAsync(cancellationToken);

        const ushort vkDelete = 0x2E;
        await PressAsync(vkDelete, cancellationToken);
    }

    /// <summary>
    /// A small randomised gap between keystrokes.
    ///
    /// Not an attempt to look human — it is that Electron's input handling drops characters when
    /// they arrive faster than its renderer can process them, which produces the maddening failure
    /// where a password is *almost* right.
    /// </summary>
    private async Task DelayAsync(CancellationToken cancellationToken)
    {
        var delay = _minDelayMs == _maxDelayMs
            ? _minDelayMs
            : RandomNumberGenerator.GetInt32(_minDelayMs, _maxDelayMs + 1);
        await Task.Delay(delay, cancellationToken);
    }

    private static void SendCharacter(char value)
    {
        var character = (ushort)value;
        // A UTF-16 surrogate half is sent as its own event; Windows recombines the pair.
        Send(
            NewKeyboardInput(0, character, NativeInput.KeyEventUnicode),
            NewKeyboardInput(0, character, NativeInput.KeyEventUnicode | NativeInput.KeyEventKeyUp));
    }

    private static void SendVirtualKey(ushort virtualKey, bool keyUp)
    {
        var flags = keyUp ? NativeInput.KeyEventKeyUp : 0u;
        Send(NewKeyboardInput(virtualKey, 0, flags));
    }

    private static NativeInput.Input NewKeyboardInput(ushort virtualKey, ushort scan, uint flags) => new()
    {
        Type = NativeInput.InputKeyboard,
        Union = new NativeInput.InputUnion
        {
            Keyboard = new NativeInput.KeyboardInput
            {
                VirtualKey = virtualKey,
                ScanCode = scan,
                Flags = flags,
                Time = 0,
                ExtraInfo = nint.Zero,
            },
        },
    };

    private static void Send(params NativeInput.Input[] inputs)
    {
        var sent = NativeInput.SendInput(
            (uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<NativeInput.Input>());

        if (sent != inputs.Length)
        {
            // The usual cause is a more-privileged window holding the foreground: UIPI silently
            // discards input from a lower integrity level. Saying so beats a silent half-typed login.
            throw new InputBlockedException(
                "Windows refused to deliver keystrokes to the Riot Client. This normally means a " +
                "window running as administrator has focus. Close it, or run the account manager " +
                "at the same privilege level, and try again.");
        }
    }
}

public sealed class InputBlockedException : Exception
{
    public InputBlockedException(string message) : base(message) { }
}

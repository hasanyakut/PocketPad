using PocketPad.Shared.Protocol;

namespace PocketPad.Shared.Input;

/// <summary>
/// Live typing: turns a change of the phone's text field into the keystrokes that make the PC match it —
/// Backspaces for the removed tail, then the newly inserted text. Handles typing, deleting and the keyboard
/// replacing a word (autocorrect / composing text) the same way.
/// </summary>
public static class TypingDiff
{
    public static (int Backspaces, string Insert) Compute(string? oldText, string? newText)
    {
        oldText ??= string.Empty;
        newText ??= string.Empty;

        var common = 0;
        var max = Math.Min(oldText.Length, newText.Length);
        while (common < max && oldText[common] == newText[common]) common++;

        // Never split a surrogate pair (emoji): back up to its first half.
        if (common > 0 && char.IsHighSurrogate(oldText[common - 1]) && common < oldText.Length) common--;

        return (CountTextElements(oldText, common), newText[common..]);
    }

    /// <summary>Messages to send for a change, in order. Text is chunked to the protocol limit.</summary>
    public static IEnumerable<ProtocolMessage> ToMessages(string? oldText, string? newText)
    {
        var (backspaces, insert) = Compute(oldText, newText);
        for (var i = 0; i < backspaces; i++)
            yield return new KeyboardKeyMessage { Key = KeyCode.Backspace };
        for (var i = 0; i < insert.Length; i += ProtocolInfo.MaxTextLength)
            yield return new KeyboardTextMessage { Text = insert.Substring(i, Math.Min(ProtocolInfo.MaxTextLength, insert.Length - i)) };
    }

    /// <summary>One Backspace per visible character removed (a surrogate pair is one character on the PC).</summary>
    private static int CountTextElements(string text, int from)
    {
        var count = 0;
        for (var i = from; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) i++;
            count++;
        }
        return count;
    }
}

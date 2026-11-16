using PocketPad.Shared.Input;
using PocketPad.Shared.Protocol;

namespace PocketPad.Tests.Input;

public class TypingDiffTests
{
    [Theory]
    [InlineData("", "a", 0, "a")]            // typing
    [InlineData("merh", "merha", 0, "a")]
    [InlineData("merha", "merh", 1, "")]     // backspace
    [InlineData("merhaba", "mer", 4, "")]    // several deleted at once
    [InlineData("teh", "the ", 2, "he ")]    // autocorrect replaced the tail
    [InlineData("abc", "", 3, "")]
    [InlineData("ğüş", "ğüşıöç", 0, "ıöç")]  // Turkish letters
    [InlineData("same", "same", 0, "")]
    public void Compute_ProducesMinimalBackspacesAndInsert(string oldText, string newText, int backspaces, string insert) =>
        Assert.Equal((backspaces, insert), TypingDiff.Compute(oldText, newText));

    [Fact]
    public void Emoji_IsDeletedWithOneBackspace_AndNeverSplit()
    {
        Assert.Equal((1, ""), TypingDiff.Compute("hi😀", "hi"));
        // Different emoji sharing the same high surrogate: replace the whole emoji, not half of it.
        var (bs, insert) = TypingDiff.Compute("😀", "😁");
        Assert.Equal(1, bs);
        Assert.Equal("😁", insert);
    }

    [Fact]
    public void ToMessages_SendsBackspacesFirst_ThenText()
    {
        var messages = TypingDiff.ToMessages("teh", "the").ToList();

        Assert.Collection(messages,
            m => Assert.Equal(new KeyboardKeyMessage { Key = KeyCode.Backspace }, m),
            m => Assert.Equal(new KeyboardKeyMessage { Key = KeyCode.Backspace }, m),
            m => Assert.Equal(new KeyboardTextMessage { Text = "he" }, m));
        Assert.All(messages, m => Assert.Null(m.Validate()));
    }

    [Fact]
    public void ToMessages_ChunksLongPastes_ToProtocolLimit()
    {
        var paste = new string('x', ProtocolInfo.MaxTextLength * 2 + 5);
        var messages = TypingDiff.ToMessages("", paste).Cast<KeyboardTextMessage>().ToList();

        Assert.Equal(3, messages.Count);
        Assert.Equal(paste, string.Concat(messages.Select(m => m.Text)));
        Assert.All(messages, m => Assert.Null(m.Validate()));
    }

    [Fact]
    public void NoChange_SendsNothing() => Assert.Empty(TypingDiff.ToMessages("abc", "abc"));
}

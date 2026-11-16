using System.Runtime.InteropServices;
using PocketPad.Desktop.Input;
using PocketPad.Shared.Input;
using PocketPad.Shared.Protocol;
using PocketPad.Tests.Integration;

namespace PocketPad.Tests.Input;

public class MotionCoalescerTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(16);

    [Fact]
    public void ConsecutiveMoves_AreMergedIntoOne()
    {
        var c = new MotionCoalescer(Interval);
        c.AddMove(1, 2);
        c.AddMove(3, -1);
        c.AddMove(0.5, 0.5);

        var msg = Assert.IsType<MouseMoveMessage>(c.TryTake(0, out _));
        Assert.Equal(4.5, msg.Dx);
        Assert.Equal(1.5, msg.Dy);
        Assert.Null(c.TryTake(0, out var wait));
        Assert.Null(wait);
    }

    [Fact]
    public void Moves_AreRateLimited_AndKeepMergingWhileWaiting()
    {
        var c = new MotionCoalescer(Interval);
        c.AddMove(1, 0);
        Assert.NotNull(c.TryTake(0, out _));

        c.AddMove(2, 0);
        Assert.Null(c.TryTake(5, out var wait));
        Assert.Equal(16, wait);

        c.AddMove(3, 0); // arrives while waiting: merged
        var msg = Assert.IsType<MouseMoveMessage>(c.TryTake(16, out _));
        Assert.Equal(5, msg.Dx);
    }

    [Fact]
    public void Clicks_AreNeverMerged_AndKeepOrderWithMoves()
    {
        var c = new MotionCoalescer(Interval);
        c.AddMove(1, 1);
        c.Add(new MouseClickMessage { Button = MouseButton.Left });
        c.AddMove(2, 2);
        c.Add(new MouseClickMessage { Button = MouseButton.Left });
        c.Add(new MouseClickMessage { Button = MouseButton.Right });

        var sent = new List<ProtocolMessage>();
        for (long t = 0; t < 1000 && c.Count > 0; t += 4)
            while (c.TryTake(t, out _) is { } m) sent.Add(m);

        Assert.Collection(sent,
            m => Assert.Equal(new MouseMoveMessage { Dx = 1, Dy = 1 }, m),
            m => Assert.Equal(new MouseClickMessage { Button = MouseButton.Left }, m),
            m => Assert.Equal(new MouseMoveMessage { Dx = 2, Dy = 2 }, m),
            m => Assert.Equal(new MouseClickMessage { Button = MouseButton.Left }, m),
            m => Assert.Equal(new MouseClickMessage { Button = MouseButton.Right }, m));
    }

    [Fact]
    public void ZeroAndNonFiniteMoves_AreIgnored()
    {
        var c = new MotionCoalescer(Interval);
        c.AddMove(0, 0);
        c.AddMove(double.NaN, 1);
        c.AddMove(double.PositiveInfinity, 0);
        Assert.Equal(0, c.Count);
    }

    [Fact]
    public void MergedMove_IsClampedToProtocolLimit()
    {
        var c = new MotionCoalescer(Interval);
        for (var i = 0; i < 100; i++) c.AddMove(500, -500);
        var msg = Assert.IsType<MouseMoveMessage>(c.TryTake(0, out _));
        Assert.Null(msg.Validate());
        Assert.Equal(ProtocolInfo.MaxPointerDelta, msg.Dx);
    }
}

public class InputSenderTests
{
    [Fact]
    public async Task ManyMoves_AreCoalesced_ToAbout60Hz_WithoutLosingDistance()
    {
        var sent = new List<ProtocolMessage>();
        await using var sender = new InputSender(m => { lock (sent) sent.Add(m); return ValueTask.FromResult(true); }, () => true);

        for (var i = 0; i < 200; i++)
        {
            sender.Move(1, 0.5);
            if (i % 20 == 0) await Task.Delay(5);
        }
        await TestExtensions.WaitUntilAsync(() => { lock (sent) return sent.OfType<MouseMoveMessage>().Sum(m => m.Dx) >= 200; });

        lock (sent)
        {
            Assert.True(sent.Count < 50, $"Expected coalescing, got {sent.Count} messages.");
            Assert.Equal(200, sent.OfType<MouseMoveMessage>().Sum(m => m.Dx), 6);
            Assert.Equal(100, sent.OfType<MouseMoveMessage>().Sum(m => m.Dy), 6);
        }
    }

    [Fact]
    public async Task Clicks_AreAllDelivered_InOrder()
    {
        var sent = new List<ProtocolMessage>();
        await using var sender = new InputSender(m => { lock (sent) sent.Add(m); return ValueTask.FromResult(true); }, () => true);

        for (var i = 0; i < 20; i++)
        {
            sender.Move(1, 1);
            sender.Send(new MouseClickMessage { Button = i % 2 == 0 ? MouseButton.Left : MouseButton.Right });
        }

        await TestExtensions.WaitUntilAsync(() => { lock (sent) return sent.OfType<MouseClickMessage>().Count() == 20; });
        lock (sent)
        {
            var clicks = sent.OfType<MouseClickMessage>().Select(c => c.Button).ToList();
            Assert.Equal(Enumerable.Range(0, 20).Select(i => i % 2 == 0 ? MouseButton.Left : MouseButton.Right), clicks);
        }
    }

    [Fact]
    public async Task NothingIsSent_WhenNotConnected()
    {
        var connected = false;
        var sent = 0;
        await using var sender = new InputSender(_ => { Interlocked.Increment(ref sent); return ValueTask.FromResult(true); }, () => connected);

        Assert.False(sender.Move(10, 10));
        Assert.False(sender.Send(new MouseClickMessage { Button = MouseButton.Left }));
        await Task.Delay(100);
        Assert.Equal(0, sent);

        connected = true; // input from before the connection must not replay
        Assert.True(sender.Send(new MouseClickMessage { Button = MouseButton.Right }));
        await TestExtensions.WaitUntilAsync(() => Volatile.Read(ref sent) == 1);
        await Task.Delay(100);
        Assert.Equal(1, sent);
    }

    [Fact]
    public async Task FailedSend_DropsQueuedInput()
    {
        var calls = 0;
        await using var sender = new InputSender(_ => { Interlocked.Increment(ref calls); return ValueTask.FromResult(false); }, () => true);
        sender.Send(new MouseClickMessage { Button = MouseButton.Left });
        sender.Send(new MouseClickMessage { Button = MouseButton.Left });
        sender.Send(new MouseClickMessage { Button = MouseButton.Left });

        await TestExtensions.WaitUntilAsync(() => Volatile.Read(ref calls) >= 1);
        await Task.Delay(100);
        Assert.Equal(1, calls);
    }
}

public class WindowsInputTests
{
    [Fact]
    public void InputStruct_MatchesNativeLayout()
    {
        var expected = Environment.Is64BitProcess ? 40 : 28;
        Assert.Equal(expected, Marshal.SizeOf<NativeMethods.INPUT>());
        Assert.Equal(Environment.Is64BitProcess ? 8 : 4, (int)Marshal.OffsetOf<NativeMethods.INPUT>(nameof(NativeMethods.INPUT.U)));
        Assert.Equal(Environment.Is64BitProcess ? 32 : 24, Marshal.SizeOf<NativeMethods.MOUSEINPUT>());
    }

    [Theory]
    [InlineData(MouseButton.Left, MouseClickAction.Click, new uint[] { 0x2, 0x4 })]
    [InlineData(MouseButton.Right, MouseClickAction.Click, new uint[] { 0x8, 0x10 })]
    [InlineData(MouseButton.Left, MouseClickAction.DoubleClick, new uint[] { 0x2, 0x4, 0x2, 0x4 })]
    [InlineData(MouseButton.Left, MouseClickAction.Down, new uint[] { 0x2 })]
    [InlineData(MouseButton.Right, MouseClickAction.Up, new uint[] { 0x10 })]
    public void BuildClick_ProducesExpectedEventSequence(MouseButton button, MouseClickAction action, uint[] flags)
    {
        var inputs = WindowsInputController.BuildClick(button, action);

        Assert.Equal(flags, inputs.Select(i => i.U.mi.dwFlags));
        Assert.All(inputs, i => Assert.Equal(NativeMethods.INPUT_MOUSE, i.type));
        Assert.All(inputs, i => Assert.Equal(0, i.U.mi.dx));
    }

    [Fact]
    public void MoveInput_IsRelative()
    {
        var input = WindowsInputController.CreateMouseInput(NativeMethods.MOUSEEVENTF_MOVE, -5, 7);
        Assert.Equal(NativeMethods.MOUSEEVENTF_MOVE, input.U.mi.dwFlags); // no MOUSEEVENTF_ABSOLUTE
        Assert.Equal(-5, input.U.mi.dx);
        Assert.Equal(7, input.U.mi.dy);
    }

    [Fact]
    public void Dispatcher_CarriesFractions_AndResets()
    {
        var controller = new RecordingInputController();
        var dispatcher = new InputDispatcher(controller);

        dispatcher.Dispatch(new MouseMoveMessage { Dx = 0.4, Dy = -0.4 });
        dispatcher.Dispatch(new MouseMoveMessage { Dx = 0.4, Dy = -0.4 });
        dispatcher.Dispatch(new MouseMoveMessage { Dx = 0.4, Dy = -0.4 });
        dispatcher.Reset();
        dispatcher.Dispatch(new MouseMoveMessage { Dx = 0.4, Dy = 0.4 });

        Assert.Equal(["move 0,0", "move 0,0", "move 1,-1", "move 0,0"], controller.Calls);
    }

    [Fact]
    public void Dispatcher_ReportsControllerFailure()
    {
        var controller = new RecordingInputController { Result = false };
        var dispatcher = new InputDispatcher(controller);

        Assert.False(dispatcher.Dispatch(new MouseClickMessage { Button = MouseButton.Left }));
        Assert.False(dispatcher.Dispatch(new KeyboardTextMessage { Text = "x" }));
        Assert.Equal(2, controller.Calls.Count);
    }

    [Fact]
    public void Dispatcher_RoutesKeyboardAndMedia()
    {
        var controller = new RecordingInputController();
        var dispatcher = new InputDispatcher(controller);

        Assert.True(dispatcher.Dispatch(new KeyboardTextMessage { Text = "Merhaba ğüş" }));
        Assert.True(dispatcher.Dispatch(new KeyboardKeyMessage { Key = KeyCode.Enter }));
        Assert.True(dispatcher.Dispatch(new KeyboardKeyMessage { Key = KeyCode.Tab, Modifiers = KeyModifiers.Alt }));
        Assert.True(dispatcher.Dispatch(new MediaCommandMessage { Command = MediaCommand.VolumeUp }));

        Assert.Equal(["text Merhaba ğüş", "key Press Enter None", "key Press Tab Alt", "media VolumeUp"], controller.Calls);
    }

    [Fact]
    public void Dispatcher_Reset_ReleasesHeldKeysAndButtons()
    {
        var controller = new RecordingInputController();
        var dispatcher = new InputDispatcher(controller);

        dispatcher.Dispatch(new KeyboardKeyMessage { Key = KeyCode.Left, Modifiers = KeyModifiers.Shift, Action = KeyAction.Down });
        dispatcher.Dispatch(new KeyboardKeyMessage { Key = KeyCode.F5, Action = KeyAction.Down });
        dispatcher.Dispatch(new KeyboardKeyMessage { Key = KeyCode.F5, Action = KeyAction.Up });
        dispatcher.Dispatch(new MouseClickMessage { Button = MouseButton.Left, Action = MouseClickAction.Down });
        var before = controller.Calls.Count;

        dispatcher.Reset();
        dispatcher.Reset(); // idempotent

        Assert.Equal(["key Up Left Shift", "Up Left"], controller.Calls.Skip(before));
    }

    [Fact]
    public void BuildText_UsesUnicodeEvents_AndRealEnterAndTab()
    {
        var inputs = WindowsInputController.BuildText("aĞ\r\nb\t😀");

        // a, Ğ: unicode down/up
        Assert.All(inputs.Take(4), i => Assert.Equal(NativeMethods.INPUT_KEYBOARD, i.type));
        Assert.Equal('a', (char)inputs[0].U.ki.wScan);
        Assert.Equal(NativeMethods.KEYEVENTF_UNICODE, inputs[0].U.ki.dwFlags);
        Assert.Equal(NativeMethods.KEYEVENTF_UNICODE | NativeMethods.KEYEVENTF_KEYUP, inputs[1].U.ki.dwFlags);
        Assert.Equal('Ğ', (char)inputs[2].U.ki.wScan);
        // \r\n → a single Enter press
        Assert.Equal(NativeMethods.VK_RETURN, inputs[4].U.ki.wVk);
        Assert.Equal(NativeMethods.VK_RETURN, inputs[5].U.ki.wVk);
        Assert.Equal('b', (char)inputs[6].U.ki.wScan);
        Assert.Equal(NativeMethods.VK_TAB, inputs[8].U.ki.wVk);
        // emoji = surrogate pair = 2 units × down/up
        Assert.Equal(14, inputs.Length);
        Assert.True(char.IsHighSurrogate((char)inputs[10].U.ki.wScan));
        Assert.True(char.IsLowSurrogate((char)inputs[12].U.ki.wScan));
    }

    [Fact]
    public void BuildText_SkipsOtherControlCharacters() =>
        Assert.Empty(WindowsInputController.BuildText("\u0007\u001b"));

    [Fact]
    public void BuildKey_WrapsKeyInModifiers_InOrder()
    {
        var inputs = WindowsInputController.BuildKey(KeyCode.Left, KeyModifiers.Control | KeyModifiers.Shift, KeyAction.Press);

        Assert.Equal(
            [(NativeMethods.VK_CONTROL, false), (NativeMethods.VK_SHIFT, false), ((ushort)0x25, false),
             ((ushort)0x25, true), (NativeMethods.VK_SHIFT, true), (NativeMethods.VK_CONTROL, true)],
            inputs.Select(i => (i.U.ki.wVk, (i.U.ki.dwFlags & NativeMethods.KEYEVENTF_KEYUP) != 0)));
        // Arrow keys must be "extended", otherwise they behave like numpad keys.
        Assert.Equal(NativeMethods.KEYEVENTF_EXTENDEDKEY, inputs[2].U.ki.dwFlags & NativeMethods.KEYEVENTF_EXTENDEDKEY);
        Assert.Equal(0u, inputs[0].U.ki.dwFlags & NativeMethods.KEYEVENTF_EXTENDEDKEY);
    }

    [Fact]
    public void BuildKey_DownAndUp_AreSplit()
    {
        Assert.Equal(2, WindowsInputController.BuildKey(KeyCode.Tab, KeyModifiers.Alt, KeyAction.Down).Length);
        Assert.Equal(2, WindowsInputController.BuildKey(KeyCode.Tab, KeyModifiers.Alt, KeyAction.Up).Length);
    }

    [Fact]
    public void AllKeysAndMediaCommands_HaveVirtualKeys()
    {
        foreach (var key in Enum.GetValues<KeyCode>()) Assert.NotEqual(0, WindowsInputController.VirtualKey(key));
        Assert.Equal(0x70, WindowsInputController.VirtualKey(KeyCode.F1));
        Assert.Equal(0x7B, WindowsInputController.VirtualKey(KeyCode.F12));
        foreach (var cmd in Enum.GetValues<MediaCommand>())
            Assert.True(WindowsInputController.IsExtended(WindowsInputController.MediaVirtualKey(cmd)));
        Assert.Equal(0xB3, WindowsInputController.MediaVirtualKey(MediaCommand.PlayPause));
    }
}

public class ScrollPipelineTests
{
    [Fact]
    public void Coalescer_MergesConsecutiveScrolls_SeparatelyFromMoves_AndKeepsOrder()
    {
        var c = new MotionCoalescer(TimeSpan.FromMilliseconds(16));
        c.AddScroll(0, 30);
        c.AddScroll(0, 45);                 // merged
        c.AddMove(5, 5);                    // different kind: new message
        c.AddScroll(0, -10);                // new message (tail is a move)
        c.Add(new MouseClickMessage { Button = MouseButton.Left });

        var sent = new List<ProtocolMessage>();
        for (long t = 0; t < 1000 && c.Count > 0; t += 4)
            while (c.TryTake(t, out _) is { } m) sent.Add(m);

        Assert.Collection(sent,
            m => Assert.Equal(new MouseScrollMessage { Dx = 0, Dy = 75 }, m),
            m => Assert.Equal(new MouseMoveMessage { Dx = 5, Dy = 5 }, m),
            m => Assert.Equal(new MouseScrollMessage { Dx = 0, Dy = -10 }, m),
            m => Assert.IsType<MouseClickMessage>(m));
    }

    [Fact]
    public void Coalescer_RateLimitsScrolls_AndMergesWhileWaiting()
    {
        var c = new MotionCoalescer(TimeSpan.FromMilliseconds(16));
        c.AddScroll(0, 10);
        Assert.NotNull(c.TryTake(0, out _));
        c.AddScroll(0, 10);
        Assert.Null(c.TryTake(8, out var wait));
        Assert.Equal(16, wait);
        c.AddScroll(0, 10);
        Assert.Equal(new MouseScrollMessage { Dy = 20 }, c.TryTake(16, out _));
    }

    [Fact]
    public void DiscardMotion_DropsMovesAndScrolls_ButKeepsClicks()
    {
        var c = new MotionCoalescer(TimeSpan.FromMilliseconds(16));
        c.AddMove(5, 5);
        c.Add(new MouseClickMessage { Button = MouseButton.Right });
        c.AddScroll(0, 40);

        c.DiscardMotion();

        Assert.Equal(1, c.Count);
        Assert.IsType<MouseClickMessage>(c.TryTake(0, out _));
    }

    [Fact]
    public async Task Sender_CoalescesManyScrolls_WithoutLosingDistance()
    {
        var sent = new List<ProtocolMessage>();
        await using var sender = new InputSender(m => { lock (sent) sent.Add(m); return ValueTask.FromResult(true); }, () => true);

        for (var i = 0; i < 150; i++)
        {
            sender.Scroll(0, 2);
            if (i % 30 == 0) await Task.Delay(5);
        }
        await TestExtensions.WaitUntilAsync(() => { lock (sent) return sent.OfType<MouseScrollMessage>().Sum(m => m.Dy) >= 300; });

        lock (sent)
        {
            Assert.True(sent.Count < 40, $"Expected coalescing, got {sent.Count} messages.");
            Assert.Equal(300, sent.OfType<MouseScrollMessage>().Sum(m => m.Dy), 6);
        }
    }

    [Fact]
    public void BuildWheel_UsesWheelFlags_AndSignedMouseData()
    {
        var up = WindowsInputController.BuildWheel(120, horizontal: false);
        var down = WindowsInputController.BuildWheel(-45, horizontal: false);
        var right = WindowsInputController.BuildWheel(60, horizontal: true);

        Assert.Equal(NativeMethods.MOUSEEVENTF_WHEEL, up.U.mi.dwFlags);
        Assert.Equal(120u, up.U.mi.mouseData);
        Assert.Equal(-45, unchecked((int)down.U.mi.mouseData));
        Assert.Equal(NativeMethods.MOUSEEVENTF_HWHEEL, right.U.mi.dwFlags);
        Assert.Equal(0, up.U.mi.dx);
    }

    [Fact]
    public void Dispatcher_CarriesFractionalWheel_AndResets()
    {
        var controller = new RecordingInputController();
        var dispatcher = new InputDispatcher(controller);

        dispatcher.Dispatch(new MouseScrollMessage { Dy = 10.6 });   // 10, carry .6
        dispatcher.Dispatch(new MouseScrollMessage { Dy = 0.6 });    // 1 (1.2), carry .2
        dispatcher.Dispatch(new MouseScrollMessage { Dy = -0.5 });   // -0.3 → nothing sent
        dispatcher.Dispatch(new MouseScrollMessage { Dy = -1 });     // -1.3 → -1, carry -.3
        dispatcher.Dispatch(new MouseScrollMessage { Dx = 2.5, Dy = 0 }); // horizontal 2, carry .5
        dispatcher.Reset();
        dispatcher.Dispatch(new MouseScrollMessage { Dy = 0.9 });    // remainder was reset → nothing

        Assert.Equal(["wheel 10 v", "wheel 1 v", "wheel -1 v", "wheel 2 h"], controller.Calls);
    }
}

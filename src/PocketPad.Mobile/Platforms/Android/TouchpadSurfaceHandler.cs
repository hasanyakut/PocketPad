using Android.Views;
using Microsoft.Maui.Handlers;
using PocketPad.Mobile.Controls;
using PocketPad.Shared.Input;
using AView = Android.Views.View;

namespace PocketPad.Mobile.Handlers;

/// <summary>Forwards raw Android multi-touch events (converted from px to dp) to <see cref="TouchpadSurface"/>.</summary>
public sealed class TouchpadSurfaceHandler() : ViewHandler<TouchpadSurface, AView>(ViewMapper)
{
    private readonly TouchPoint[] _moveBuffer = new TouchPoint[10];
    private float _density = 1;

    protected override AView CreatePlatformView() => new(Context);

    protected override void ConnectHandler(AView platformView)
    {
        base.ConnectHandler(platformView);
        _density = platformView.Resources?.DisplayMetrics?.Density ?? 1;
        platformView.Touch += OnTouch;
    }

    protected override void DisconnectHandler(AView platformView)
    {
        platformView.Touch -= OnTouch;
        VirtualView?.OnCancel();
        base.DisconnectHandler(platformView);
    }

    private void OnTouch(object? sender, AView.TouchEventArgs e)
    {
        var ev = e.Event;
        var surface = VirtualView;
        if (ev is null || surface is null)
        {
            e.Handled = false;
            return;
        }

        e.Handled = true;
        // Keep parents (Shell, scroll containers) from stealing the gesture mid-move.
        PlatformView.Parent?.RequestDisallowInterceptTouchEvent(true);

        // Pointer *indexes* are only valid within one MotionEvent and shift when other fingers lift;
        // fingers are therefore always identified by GetPointerId(index), which stays stable for a finger's lifetime.
        switch (ev.ActionMasked)
        {
            case MotionEventActions.Down:
            case MotionEventActions.PointerDown:
            {
                var i = ev.ActionIndex;
                surface.OnPointerDown(ev.GetPointerId(i), ev.GetX(i) / _density, ev.GetY(i) / _density);
                break;
            }
            case MotionEventActions.Move:
            {
                // All fingers of one move event go in one batch so the two-finger centroid is computed once.
                var count = Math.Min(ev.PointerCount, _moveBuffer.Length);
                for (var i = 0; i < count; i++)
                    _moveBuffer[i] = new TouchPoint(ev.GetPointerId(i), ev.GetX(i) / _density, ev.GetY(i) / _density);
                surface.OnPointersMoved(_moveBuffer.AsSpan(0, count));
                break;
            }
            case MotionEventActions.Up:
            case MotionEventActions.PointerUp:
            {
                var i = ev.ActionIndex;
                surface.OnPointerUp(ev.GetPointerId(i), ev.GetX(i) / _density, ev.GetY(i) / _density);
                break;
            }
            case MotionEventActions.Cancel:
                surface.OnCancel();
                break;
        }
    }
}

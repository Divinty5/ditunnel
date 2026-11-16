using Avalonia;
using DiTunnel.App.ViewModels;

namespace DiTunnel.App.Controls;

internal sealed class ProfileTouchTaps
{
    private (ServerItemViewModel Row, int Pointer, Point Position, ulong Time)? press;
    private (ServerItemViewModel Row, Point Position, ulong Time)? previousTap;

    internal void Press(ServerItemViewModel? row, int pointer, Point position, ulong time)
    {
        if (row is null) Reset();
        else press = (row, pointer, position, time);
    }

    internal void Move(int pointer, Point position, Size tolerance)
    {
        if (press is { } current && current.Pointer == pointer && !Near(position, current.Position, tolerance))
            Reset();
    }

    internal ServerItemViewModel? Release(ServerItemViewModel? row, int pointer, Point position, ulong time,
        Size tapTolerance, Size doubleTapTolerance, TimeSpan doubleTapInterval)
    {
        var started = press;
        press = null;
        if (row is null || started is not { } current || current.Row != row || current.Pointer != pointer ||
            time - current.Time > doubleTapInterval.TotalMilliseconds || !Near(position, current.Position, tapTolerance))
        {
            previousTap = null;
            return null;
        }
        if (previousTap is { } first && first.Row == row && current.Time - first.Time <= doubleTapInterval.TotalMilliseconds &&
            Near(current.Position, first.Position, doubleTapTolerance))
        {
            previousTap = null;
            return row;
        }
        previousTap = (row, current.Position, time);
        return null;
    }

    internal void Reset()
    {
        press = null;
        previousTap = null;
    }

    private static bool Near(Point a, Point b, Size tolerance) =>
        Math.Abs(a.X - b.X) <= tolerance.Width && Math.Abs(a.Y - b.Y) <= tolerance.Height;
}

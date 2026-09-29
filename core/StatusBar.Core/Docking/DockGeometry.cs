namespace StatusBar.Core.Docking;

/// <summary>Dock position for a compact status strip.</summary>
public enum DockAnchor
{
    BottomRight,
    BottomLeft,
    TopRight,
    TopLeft,
}

/// <summary>A width and height expressed in device-independent pixels.</summary>
public readonly record struct Size(double Width, double Height);

/// <summary>A rectangle expressed in device-independent pixels.</summary>
public readonly record struct Rect(double X, double Y, double Width, double Height)
{
    /// <summary>Left edge.</summary>
    public double Left => X;

    /// <summary>Top edge.</summary>
    public double Top => Y;

    /// <summary>Right edge.</summary>
    public double Right => X + Width;

    /// <summary>Bottom edge.</summary>
    public double Bottom => Y + Height;
}

/// <summary>A strip position relative to the available movement inside a work area.</summary>
public readonly record struct RelativePosition(double X, double Y);

/// <summary>Calculates strip and pane rectangles without depending on WPF or screen APIs.</summary>
public static class DockGeometry
{
    /// <summary>Places a strip at an anchor and clamps it inside the monitor work area.</summary>
    public static Rect Place(Rect workAreaDip, Size stripDip, DockAnchor anchor, double margin)
    {
        ValidateRect(workAreaDip, nameof(workAreaDip));
        ValidateSize(stripDip, nameof(stripDip));
        if (!double.IsFinite(margin) || margin < 0)
            throw new ArgumentOutOfRangeException(nameof(margin));

        var width = Math.Min(stripDip.Width, workAreaDip.Width);
        var height = Math.Min(stripDip.Height, workAreaDip.Height);
        var left = anchor switch
        {
            DockAnchor.BottomRight or DockAnchor.TopRight => workAreaDip.Right - width - margin,
            DockAnchor.BottomLeft or DockAnchor.TopLeft => workAreaDip.Left + margin,
            _ => throw new ArgumentOutOfRangeException(nameof(anchor)),
        };
        var top = anchor switch
        {
            DockAnchor.BottomRight or DockAnchor.BottomLeft => workAreaDip.Bottom - height - margin,
            DockAnchor.TopRight or DockAnchor.TopLeft => workAreaDip.Top + margin,
            _ => throw new ArgumentOutOfRangeException(nameof(anchor)),
        };

        left = Math.Clamp(left, workAreaDip.Left, workAreaDip.Right - width);
        top = Math.Clamp(top, workAreaDip.Top, workAreaDip.Bottom - height);
        return new Rect(left, top, width, height);
    }

    /// <summary>Places a strip at a saved relative position, clamped inside the work area.</summary>
    public static Rect PlaceRelative(Rect workAreaDip, Size stripDip, RelativePosition position)
    {
        ValidateRect(workAreaDip, nameof(workAreaDip));
        ValidateSize(stripDip, nameof(stripDip));
        if (!double.IsFinite(position.X) || !double.IsFinite(position.Y))
            throw new ArgumentOutOfRangeException(nameof(position));

        var width = Math.Min(stripDip.Width, workAreaDip.Width);
        var height = Math.Min(stripDip.Height, workAreaDip.Height);
        return new Rect(
            workAreaDip.Left + Math.Clamp(position.X, 0, 1) * (workAreaDip.Width - width),
            workAreaDip.Top + Math.Clamp(position.Y, 0, 1) * (workAreaDip.Height - height),
            width,
            height);
    }

    /// <summary>Captures a moved strip's position so a changed work area can restore it.</summary>
    public static RelativePosition CaptureRelative(Rect workAreaDip, Size stripDip, double left, double top)
    {
        ValidateRect(workAreaDip, nameof(workAreaDip));
        ValidateSize(stripDip, nameof(stripDip));
        if (!double.IsFinite(left))
            throw new ArgumentOutOfRangeException(nameof(left));
        if (!double.IsFinite(top))
            throw new ArgumentOutOfRangeException(nameof(top));

        var availableX = Math.Max(0, workAreaDip.Width - stripDip.Width);
        var availableY = Math.Max(0, workAreaDip.Height - stripDip.Height);
        return new RelativePosition(
            availableX == 0 ? 0 : Math.Clamp((left - workAreaDip.Left) / availableX, 0, 1),
            availableY == 0 ? 0 : Math.Clamp((top - workAreaDip.Top) / availableY, 0, 1));
    }

    /// <summary>Right-aligns a pane above the strip, falling below when the top edge has no room.</summary>
    public static Rect PlacePaneAbove(Rect strip, Size paneDip, Rect workAreaDip, double gap)
    {
        ValidateRect(strip, nameof(strip));
        ValidateSize(paneDip, nameof(paneDip));
        ValidateRect(workAreaDip, nameof(workAreaDip));
        if (!double.IsFinite(gap) || gap < 0)
            throw new ArgumentOutOfRangeException(nameof(gap));

        var width = Math.Min(paneDip.Width, workAreaDip.Width);
        var height = Math.Min(paneDip.Height, workAreaDip.Height);
        var left = Math.Clamp(strip.Right - width, workAreaDip.Left, workAreaDip.Right - width);
        var above = strip.Top - gap - height;
        double top;
        if (above >= workAreaDip.Top)
        {
            top = above;
        }
        else
        {
            var below = strip.Bottom + gap;
            top = below + height <= workAreaDip.Bottom
                ? below
                : Math.Clamp(above, workAreaDip.Top, workAreaDip.Bottom - height);
        }

        return new Rect(left, top, width, height);
    }

    static void ValidateRect(Rect value, string parameterName)
    {
        if (!double.IsFinite(value.X) || !double.IsFinite(value.Y) ||
            !double.IsFinite(value.Width) || !double.IsFinite(value.Height) ||
            value.Width < 0 || value.Height < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    static void ValidateSize(Size value, string parameterName)
    {
        if (!double.IsFinite(value.Width) || !double.IsFinite(value.Height) ||
            value.Width < 0 || value.Height < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}

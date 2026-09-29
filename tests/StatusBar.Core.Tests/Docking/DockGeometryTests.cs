using DockingRect = StatusBar.Core.Docking.Rect;
using DockingSize = StatusBar.Core.Docking.Size;
using StatusBar.Core.Docking;

namespace StatusBar.Core.Tests.Docking;

public sealed class DockGeometryTests
{
    [Fact]
    public void Bottom_right_uses_the_work_area_and_margin()
    {
        var workArea = new DockingRect(0, 0, 1920, 1040);

        var placed = DockGeometry.Place(workArea, new DockingSize(250, 30), DockAnchor.BottomRight, 16);

        Assert.Equal(new DockingRect(1654, 994, 250, 30), placed);
    }

    [Theory]
    [InlineData(48, 0, 1872, 1080, DockAnchor.BottomLeft, 12, 60, 1038)]
    [InlineData(0, 48, 1920, 1032, DockAnchor.TopRight, 12, 1658, 60)]
    [InlineData(0, 0, 1872, 1080, DockAnchor.BottomRight, 12, 1610, 1038)]
    public void Anchors_follow_work_area_offsets_for_taskbar_positions(
        double x,
        double y,
        double width,
        double height,
        DockAnchor anchor,
        double margin,
        double expectedX,
        double expectedY)
    {
        var placed = DockGeometry.Place(
            new DockingRect(x, y, width, height),
            new DockingSize(250, 30),
            anchor,
            margin);

        Assert.Equal(expectedX, placed.X);
        Assert.Equal(expectedY, placed.Y);
    }

    [Fact]
    public void Negative_coordinate_secondary_monitor_is_supported()
    {
        var placed = DockGeometry.Place(
            new DockingRect(-1600, 0, 1600, 860),
            new DockingSize(250, 30),
            DockAnchor.BottomRight,
            12);

        Assert.Equal(new DockingRect(-262, 818, 250, 30), placed);
    }

    [Fact]
    public void Strip_wider_than_work_area_is_clamped_to_it()
    {
        var placed = DockGeometry.Place(
            new DockingRect(20, 40, 200, 100),
            new DockingSize(300, 40),
            DockAnchor.BottomRight,
            16);

        Assert.Equal(new DockingRect(20, 84, 200, 40), placed);
    }

    [Fact]
    public void Dragged_position_round_trips_and_stays_in_work_area_after_resize()
    {
        var work = new DockingRect(-1600, 0, 1600, 860);
        var size = new DockingSize(250, 30);
        var position = DockGeometry.CaptureRelative(work, size, -1200, 100);

        var restored = DockGeometry.PlaceRelative(work, size, position);
        Assert.InRange(restored.Left, -1200.001, -1199.999);
        Assert.InRange(restored.Top, 99.999, 100.001);
        var resized = DockGeometry.PlaceRelative(new DockingRect(-1200, 0, 1200, 700), size, position);
        Assert.InRange(resized.Left, -1200, -250);
        Assert.InRange(resized.Top, 0, 670);
    }

    [Fact]
    public void Dragged_position_is_clamped_when_strip_exceeds_work_area()
    {
        var work = new DockingRect(20, 40, 200, 100);
        var size = new DockingSize(300, 40);
        var saved = DockGeometry.CaptureRelative(work, size, 500, -100);

        Assert.Equal(new RelativePosition(0, 0), saved);
        Assert.Equal(new DockingRect(20, 40, 200, 40),
            DockGeometry.PlaceRelative(work, size, saved));
    }

    [Fact]
    public void Pane_is_right_aligned_above_the_strip_when_space_allows()
    {
        var placed = DockGeometry.PlacePaneAbove(
            new DockingRect(700, 700, 250, 30),
            new DockingSize(400, 500),
            new DockingRect(0, 0, 1000, 800),
            8);

        Assert.Equal(new DockingRect(550, 192, 400, 500), placed);
    }

    [Fact]
    public void Pane_falls_below_when_there_is_no_room_above()
    {
        var placed = DockGeometry.PlacePaneAbove(
            new DockingRect(700, 10, 250, 30),
            new DockingSize(400, 300),
            new DockingRect(0, 0, 1000, 800),
            8);

        Assert.Equal(new DockingRect(550, 48, 400, 300), placed);
    }
}

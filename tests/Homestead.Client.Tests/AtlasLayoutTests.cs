using Homestead.Client;
using Microsoft.Xna.Framework;

namespace Homestead.Client.Tests;

public class AtlasLayoutTests
{
    private static Rectangle Rect(AtlasLayout.Placement placement) =>
        new(placement.X, placement.Y, placement.Width, placement.Height);

    [Fact]
    public void Padding_1_places_images_like_the_previous_packing()
    {
        // Hand-computed against the shelf packing as it was before the padding became a parameter.
        var layout = new AtlasLayout(padding: 1);

        Assert.Equal(Rect(new(0, 1, 1, 100, 50)), Rect(layout.Place(100, 50)!.Value));
        // Too wide for the rest of the shelf: a new shelf at y 52.
        Assert.Equal(Rect(new(0, 1, 53, 4000, 50)), Rect(layout.Place(4000, 50)!.Value));
        // Again too wide: the next shelf at y 104.
        Assert.Equal(Rect(new(0, 1, 105, 100, 50)), Rect(layout.Place(100, 50)!.Value));
        Assert.Equal(1, layout.PageCount);
    }

    [Fact]
    public void Padding_2_keeps_a_two_pixel_border()
    {
        var layout = new AtlasLayout(padding: 2);

        Assert.Equal(Rect(new(0, 2, 2, 4000, 50)), Rect(layout.Place(4000, 50)!.Value));
        // The shelf is exhausted; the next image starts on the shelf below, 2 px under the
        // first shelf's 54 px padded height.
        Assert.Equal(Rect(new(0, 2, 56, 100, 50)), Rect(layout.Place(100, 50)!.Value));
    }

    [Fact]
    public void Images_do_not_bleed_into_each_other_up_to_the_padding()
    {
        var layout = new AtlasLayout(padding: 1);
        var placements = new List<AtlasLayout.Placement>();
        int[] sizes = [17, 4090, 3, 500, 3000, 120, 250, 4095, 64, 1000];
        foreach (int size in sizes)
            if (layout.Place(size, size / 2 + 1) is { } placement)
                placements.Add(placement);

        int padding = layout.Padding;
        foreach (var group in placements.GroupBy(placement => placement.Page).ToList())
        {
            var placed = group.ToList();
            for (int i = 0; i < placed.Count; i++)
            {
                var grown = new Rectangle(placed[i].X - padding, placed[i].Y - padding,
                    placed[i].Width + 2 * padding, placed[i].Height + 2 * padding);
                for (int j = 0; j < placed.Count; j++)
                    Assert.False(i != j && grown.Intersects(Rect(placed[j])),
                        $"{placed[i]} and {placed[j]} are closer than the {padding} px border");
            }
        }
    }

    [Fact]
    public void Images_larger_than_a_page_get_no_placement()
    {
        var layout = new AtlasLayout(padding: 1);

        Assert.Null(layout.Place(4096, 10));
        Assert.Null(layout.Place(4095, 10));
        Assert.NotNull(layout.Place(4094, 10));
    }

    [Fact]
    public void A_full_page_starts_the_next_one()
    {
        var layout = new AtlasLayout(padding: 1);

        // Padded 2050x4092: the image fills the shelf's height, so nothing fits below it.
        Assert.Equal(0, layout.Place(2048, 4090)!.Value.Page);
        var second = layout.Place(2048, 4090)!.Value;
        Assert.Equal(1, second.Page);
        Assert.Equal(Rect(new(1, 1, 1, 2048, 4090)), Rect(second));
        Assert.Equal(2, layout.PageCount);
    }
}

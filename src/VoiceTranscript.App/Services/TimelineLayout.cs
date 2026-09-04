namespace VoiceTranscript.App.Services;

/// <summary>
/// Where each line sits when the conversation is drawn against the clock instead of as a list.
///
/// The chat view puts one line under the next, which is the right shape for reading and the wrong
/// shape for two people talking at once. On one call in this archive both voices are going for 130
/// of its 1129 seconds; a single column has to choose an order for speech that had none, and
/// whichever it chooses is wrong for some of it.
///
/// Against a clock there is nothing to choose. Each line is placed at the moment it was said, mine
/// on the right and theirs on the left, and speech that happened together is drawn side by side
/// because that is where the time puts it. Nothing is cut, nothing is reordered, and the shape of
/// the conversation — who held the floor, where the gaps were, who came in over whom — is visible
/// at a glance in a way a list cannot show.
///
/// The one thing a clock cannot do is honour text. A four-word line and a sixty-word line take the
/// same two seconds and need very different heights, so pure time placement would overlap them.
/// Hence the rule below: a line is placed at its moment unless the line above it in the same
/// column has not finished being drawn, in which case it goes under that one. Time is the
/// preference; legibility is the constraint. Both columns are resolved independently, because a
/// speaker cannot overlap themselves — each channel is one recording, and its lines are in order.
/// </summary>
public static class TimelineLayout
{
    /// <summary>
    /// One line to place: when it was said, whose it is, how tall it draws, and when it finished.
    ///
    /// The end is carried for the drawing rather than the placement. How long somebody held the
    /// floor is the one thing this view can show that a list cannot, and it is not the same as how
    /// much room their words need: a sentence read out in two seconds and a sentence read out in
    /// twelve occupy the same three lines of text.
    /// </summary>
    public readonly record struct Item(int StartMs, bool IsMe, double Height, int EndMs = 0);

    /// <summary>How far apart two lines in the same column are pushed when time cannot separate them.</summary>
    public const double GapPx = 6;

    /// <summary>
    /// The top edge of every line, in the order they were given.
    ///
    /// Pure, and separate from the panel that draws it, because "does the timeline overlap itself"
    /// is a question worth answering without a window open.
    /// </summary>
    public static double[] Tops(IReadOnlyList<Item> items, double pixelsPerSecond, double gap = GapPx)
    {
        var tops = new double[items.Count];

        // The bottom of the last line placed in each column: [0] theirs, [1] mine.
        var bottom = new[] { double.NegativeInfinity, double.NegativeInfinity };

        // Walked in time order so the column's bottom is always the line directly above this one.
        var order = Enumerable.Range(0, items.Count).OrderBy(i => items[i].StartMs).ToList();

        foreach (var index in order)
        {
            var item = items[index];
            var column = item.IsMe ? 1 : 0;
            var wanted = item.StartMs / 1000.0 * pixelsPerSecond;

            var top = double.IsNegativeInfinity(bottom[column])
                ? wanted
                : Math.Max(wanted, bottom[column] + gap);

            tops[index] = top;
            bottom[column] = top + item.Height;
        }

        return tops;
    }

    /// <summary>How tall the whole drawing is, so the scroller knows what it is scrolling.</summary>
    public static double Height(IReadOnlyList<Item> items, double[] tops)
    {
        double bottom = 0;

        for (var i = 0; i < items.Count; i++) bottom = Math.Max(bottom, tops[i] + items[i].Height);

        return bottom;
    }

    /// <summary>
    /// Pixels per second for a call of this length in a window of this height.
    ///
    /// Scaled rather than fixed: a two-minute call at the density that suits nineteen minutes is a
    /// postage stamp, and nineteen minutes at the density that suits two is a mile of empty paper.
    /// The bounds keep both ends usable — dense enough that a pause reads as a pause, open enough
    /// that a long call does not become a scroll with nothing in it.
    /// </summary>
    public static double PixelsPerSecond(int durationMs, double viewportHeight)
    {
        if (durationMs <= 0) return 30;

        var seconds = durationMs / 1000.0;

        // Roughly six screenfuls for any call, then clamped.
        var scaled = viewportHeight * 6 / seconds;

        return Math.Clamp(scaled, 8, 60);
    }
}

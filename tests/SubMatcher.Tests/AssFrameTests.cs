using SubMatcher.Core;
using Xunit;

namespace SubMatcher.Tests;

public class AssFrameTests
{
    const string Head = "[Script Info]\nScriptType: v4.00+\nPlayResX: 1920\nPlayResY: 816\nLayoutResY: 816\n\n[V4+ Styles]\n"
        + "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n"
        + "Style: Bottom,Arial,60,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,0,2,20,20,30,1\n"
        + "Style: Top,Arial,60,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,0,8,20,20,10,1\n"
        + "Style: Mid,Arial,60,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,0,5,20,20,10,1\n\n"
        + "[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n";

    static string Ev(string style, string text, string margins = "0,0,0") => $"Dialogue: 0,0:00:01.00,0:00:02.00,{style},,{margins},,{text}\n";

    // ChaO: typeset on a 1920×816 picture, played on a letterboxed 1920×1080 BD (132 px bars top and bottom)
    static readonly Crop ChaO = new(1920, 816, 0, 132, 1920, 1080);

    [Fact]
    public void FitsWebCanvasOntoLetterboxedBd()
    {
        var (c, _) = AssFrame.Fit(Head, (1920, 1080), ChaO);
        Assert.NotNull(c);
        Assert.Equal((1920.0, 1080.0, 0.0, 132.0), (c!.NewResX, c.NewResY, c.Ox, c.Oy));
    }

    [Fact]
    public void LeavesScriptsThatAlreadyMatchTheFrame()
    {
        Assert.Null(AssFrame.Fit(Head.Replace("PlayResY: 816", "PlayResY: 1080"), (1920, 1080), ChaO).Canvas); // made for the full BD frame
        Assert.Null(AssFrame.Fit(Head, (1920, 816), null).Canvas);                                              // video has no bars
        Assert.Null(AssFrame.Fit(Head.Replace("PlayResY: 816\n", ""), (1920, 1080), ChaO).Canvas);               // unknown canvas
    }

    [Fact]
    public void BdScriptOntoWebMovesUp()
    {
        // typeset on the letterboxed BD frame, target is a 1920×816 WEB without bars
        var c = AssFrame.Map((1920, 1080), (1920, 1080), ChaO, (1920, 816), null);
        Assert.Equal((816.0, -132.0), (c.NewResY, c.Oy));
    }

    [Fact]
    public void ShiftsEveryCoordinateButNotDrawings()
    {
        var c = new Canvas(1920, 816, 1920, 1080, 0, 132);
        var t = AssFrame.ShiftText(@"{\pos(724,605)\org(10,20)\move(1,2,3,4,0,500)\clip(0,0,1920,400)\iclip(2,m 0 0 l 100 100)\p1}m 0 0 l 10 10{\p0}", c, out bool positioned);
        Assert.True(positioned);
        Assert.Equal(@"{\pos(724,737)\org(10,152)\move(1,134,3,136,0,500)\clip(0,132,1920,532)\iclip(2,m 0 264 l 100 364)\p1}m 0 0 l 10 10{\p0}", t);
    }

    [Fact]
    public void MarginsFollowAlignment()
    {
        var c = new Canvas(1920, 816, 1920, 1080, 0, 100); // bars: 100 top, 164 bottom
        var outp = AssFrame.Apply(Head + Ev("Bottom", "plain") + Ev("Bottom", @"{\an8}moved to top") + Ev("Top", @"{\a6}legacy top") + Ev("Bottom", "own", "0,0,50"), c);
        Assert.Contains("PlayResY: 1080", outp);
        Assert.Contains("LayoutResY: 1080", outp);
        Assert.Contains(",2,20,20,194,1", outp);  // bottom style: +164
        Assert.Contains(",8,20,20,110,1", outp);  // top style: +100
        Assert.Contains(",5,20,20,10,1", outp);   // middle: unchanged
        Assert.Contains("Bottom,,0,0,0,,plain", outp);                  // style margins already right
        Assert.Contains(@"Bottom,,20,20,130,,{\an8}moved to top", outp); // top-aligned on a bottom style: spelled out
        Assert.Contains(@"Top,,0,0,0,,{\a6}legacy top", outp);          // \a6 = top centre, same as the style
        Assert.Contains("Bottom,,20,20,214,,own", outp);                 // its own margin + bottom bar
    }
}

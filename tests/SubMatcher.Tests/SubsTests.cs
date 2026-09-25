using SubMatcher.Core;
using Xunit;

namespace SubMatcher.Tests;

public class SubsTests
{
    [Theory]
    [InlineData("CHT_果然我的青春戀愛喜劇搞錯了完_第7話_YouTube.srt", "CHT", "果然我的青春戀愛喜劇搞錯了完", "YouTube")]
    [InlineData("CHS 膽大黨第2季普通話版 第15集_iQIYI.srt", "CHS", "膽大黨第2季普通話版", "iQIYI")]
    [InlineData("CHS_遭到流放的转生重骑士凭借游戏知识大开无双_EP13_meWATCH.srt", "CHS", "遭到流放的转生重骑士凭借游戏知识大开无双", "meWATCH")]
    [InlineData("CHT_梅比烏斯之塵 12_Crunchyroll.ass", "CHT", "梅比烏斯之塵", "Crunchyroll")]
    [InlineData("CHS 終末起點第二季  05_Bilibili.srt", "CHS", "終末起點第二季", "Bilibili")]
    [InlineData("CHS 尼古喵喵 EP12_iQIYI.srt", "CHS", "尼古喵喵", "iQIYI")]
    [InlineData("某番_第03集.srt", "", "某番", "")]
    [InlineData("葬送的芙莉莲_EP14.BG.zh-Hans.srt", "CHS", "葬送的芙莉莲", "BG")]
    [InlineData("葬送的芙莉莲_EP09.BG.zh-Hant.srt", "CHT", "葬送的芙莉莲", "BG")]
    [InlineData("葬送的芙莉莲 12_简体中文.srt", "CHS", "葬送的芙莉莲", "")]
    [InlineData("葬送的芙莉莲 12_繁体中文.srt", "CHT", "葬送的芙莉莲", "")]
    [InlineData("CHS 葬送的芙莉莲2 05_meWATCH.srt", "CHS", "葬送的芙莉莲2", "meWATCH")]
    [InlineData("CHS_片名_12.5.srt", "CHS", "片名", "")]
    [InlineData("CHS_Dr.STONE_第1集_Bilibili.srt", "CHS", "Dr.STONE", "Bilibili")]
    public void ParsesChannelFileNames(string name, string lang, string title, string platform)
        => Assert.Equal((lang, title, platform), TgSubs.Parse(name));

    [Fact]
    public void GroupsByShowAndPlatformIgnoringEpisodes()
    {
        var g = TgSubs.Group([
            new(1, "CHS_遭到流放的转生重骑士凭借游戏知识大开无双_第12集_Viu.srt", 1),
            new(2, "CHT_遭到流放的轉生重騎士憑藉遊戲知識大開無雙_第12集_Viu.srt", 1),
            new(3, "CHS_遭到流放的转生重骑士凭借游戏知识大开无双_第13集_Viu.srt", 1),
            new(4, "CHS_遭到流放的转生重骑士凭借游戏知识大开无双_第13集_Viu.srt", 1), // repost: newest wins
            new(5, "CHS_遭到流放的转生重骑士凭借游戏知识大开无双_EP13_meWATCH.srt", 1),
        ]);
        Assert.Equal(2, g.Count);
        var viu = Assert.Single(g, x => x.Platform == "Viu");
        Assert.Equal([1, 4], viu.Chs.Select(f => f.Id));
        Assert.Equal([2], viu.Cht.Select(f => f.Id)); // 繁 title merged into the 简 one
        Assert.Contains(g, x => x.Platform == "meWATCH" && x.Chs.Count == 1);
    }

    [Fact]
    public void EpisodeOrderAndGaps()
    {
        List<TgFile> files = [.. new[] { 10, 2, 1, 3, 12, 11, 5, 4, 6, 7, 9 }.Select(e => new TgFile(e, $"CHS_片名_第{e}集_Viu.srt", 1))];
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 9, 10, 11, 12], TgSubs.ByEpisode(files).Select(f => f.Id));
        Assert.Equal("第 1–7、9–12 集", TgSubs.EpisodeSummary(files));
        Assert.Equal("第 5 集", TgSubs.EpisodeSummary([new(1, "葬送的芙莉莲_EP05.BG.zh-Hans.srt", 1)]));
        Assert.Equal("", TgSubs.EpisodeSummary([new(1, "合集.zip", 1)]));
    }

    [Fact]
    public void EpisodeNumbersInChannelNames()
    {
        Assert.Equal("13", Sync.EpisodeOf("CHS_遭到流放的转生重骑士凭借游戏知识大开无双_EP13_meWATCH.srt"));
        Assert.Equal("12", Sync.EpisodeOf("CHT_梅比烏斯之塵 12_Crunchyroll.ass"));
        Assert.Equal("15", Sync.EpisodeOf("CHS 膽大黨第2季普通話版 第15集_iQIYI.srt"));
    }

    [Theory]
    [InlineData("CHS_片名_第1集_Viu.srt", "sc")]
    [InlineData("[Grp] Show - 01.tc.ass", "tc")]
    [InlineData("[Grp] Show - 01 [简日双语].ass", null)]
    [InlineData("[Grp] Show - 01 [简体].ass", "sc")]
    [InlineData("Show 01.chs_jp.ass", "sc")]
    [InlineData("Show 01.zh-Hant.srt", "tc")]
    [InlineData("Show 01.ass", null)]
    public void DetectsLanguage(string name, string? lang) => Assert.Equal(lang, Renamer.LangOf(name));

    [Fact]
    public void RenamesToVideoNamesKeepsBackupsAndPrefersSubset()
    {
        var dir = Directory.CreateTempSubdirectory("submatcher-ren").FullName;
        string F(string n, string text = "x") { var p = Path.Combine(dir, n); File.WriteAllText(p, text); return p; }
        var v1 = F("[BD] Show - 01 [1080p].mkv");
        var v2 = F("[BD] Show - 02 [1080p].mkv");
        F("CHS_Show_第1集_Viu.srt", "sc1");
        F("CHT_Show_第1集_Viu.srt", "tc1");
        F("[BD] Show - 02 [1080p].sc.ass", "synced");
        F("[BD] Show - 02 [1080p].sc.subset.ass", "subset");

        var (videos, subs) = Renamer.Collect([dir]);
        var plan = Renamer.Plan(videos, subs);
        Assert.Equal(3, plan.Count);
        Assert.Equal(3, Renamer.Apply(plan, copy: false, backup: true));

        string R(string n) => File.ReadAllText(Path.Combine(dir, n));
        Assert.Equal("sc1", R("[BD] Show - 01 [1080p].sc.srt"));
        Assert.Equal("tc1", R("[BD] Show - 01 [1080p].tc.srt"));
        Assert.Equal("subset", R("[BD] Show - 02 [1080p].sc.ass"));
        // nothing lost: the moved originals and the overwritten synced file are in 字幕备份
        Assert.Equal("sc1", R("字幕备份/CHS_Show_第1集_Viu.srt"));
        Assert.Equal("synced", R("字幕备份/[BD] Show - 02 [1080p].sc.ass"));
        Assert.True(Renamer.Plan(videos.Where(File.Exists), Renamer.Collect([dir]).Subs).All(p => p.NoOp || p.Sub.Contains(".subset.")));
    }
}

public class ZhConvertTests
{
    [Theory]
    [InlineData("CHS_片名_第1集_Viu.srt", "tc", "CHT_片名_第1集_Viu.srt")]
    [InlineData("[BD] Show - 01.sc.ass", "tc", "[BD] Show - 01.tc.ass")]
    [InlineData("[BD] Show - 01.TC.ass", "sc", "[BD] Show - 01.SC.ass")]
    [InlineData("Show 01 [简体].ass", "tc", "Show 01 [繁體].ass")]
    [InlineData("Show 01.zh-Hant.srt", "sc", "Show 01.zh-Hans.srt")]
    [InlineData("Show 01.ass", "tc", "Show 01.tc.ass")]
    [InlineData("Show 01.tc.ass", "tc", "Show 01.tc.tc.ass")] // already that language: never overwrite the source
    public void OutputNameSwapsLanguageTag(string input, string tag, string expected)
        => Assert.Equal(expected, Path.GetFileName(ZhConvert.OutputFor(input, tag)));

    [Fact]
    public void ModesMapToConverters()
    {
        Assert.Equal("Taiwan", ZhConvert.Mode("tw").Converter);
        Assert.Equal("sc", ZhConvert.Mode("China").Tag);
        Assert.Throws<ArgumentException>(() => ZhConvert.Mode("xx"));
    }
}

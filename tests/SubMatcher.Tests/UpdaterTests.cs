using System.IO.Compression;
using SubMatcher.Gui;
using Xunit;

namespace SubMatcher.Tests;

public class UpdaterTests
{
    [Fact]
    public void ApplyReplacesAppFilesKeepsUserFilesAndFfmpeg()
    {
        var app = Directory.CreateTempSubdirectory("submatcher-app").FullName;
        File.WriteAllText(Path.Combine(app, "SubMatcher.exe"), "old");
        File.WriteAllText(Path.Combine(app, "settings.json"), "mine");
        Directory.CreateDirectory(Path.Combine(app, "ffmpeg"));
        File.WriteAllText(Path.Combine(app, "ffmpeg", "ffmpeg.exe"), "user ffmpeg");

        var pkg = Directory.CreateTempSubdirectory("submatcher-pkg").FullName;
        var top = Directory.CreateDirectory(Path.Combine(pkg, "SubMatcher-v9.9.9-win-x64")).FullName;
        File.WriteAllText(Path.Combine(top, "SubMatcher.exe"), "new");
        File.WriteAllText(Path.Combine(top, "submatcher-cli.exe"), "new cli");
        Directory.CreateDirectory(Path.Combine(top, "ffmpeg"));
        File.WriteAllText(Path.Combine(top, "ffmpeg", "ffmpeg.exe"), "bundled");
        var zip = Path.Combine(Directory.CreateTempSubdirectory().FullName, "u.zip");
        ZipFile.CreateFromDirectory(pkg, zip);

        Updater.Apply(zip, app);

        Assert.Equal("new", File.ReadAllText(Path.Combine(app, "SubMatcher.exe")));
        Assert.Equal("new cli", File.ReadAllText(Path.Combine(app, "submatcher-cli.exe")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(app, "SubMatcher.exe.old")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(app, "settings.json")));
        Assert.Equal("user ffmpeg", File.ReadAllText(Path.Combine(app, "ffmpeg", "ffmpeg.exe")));
        Assert.False(Directory.Exists(Path.Combine(app, "update-tmp")));
    }
}

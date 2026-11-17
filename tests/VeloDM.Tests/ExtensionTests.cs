using System.Text.Json;
using System.Text.RegularExpressions;

namespace VeloDM.Tests;

public class ExtensionTests
{
    private static string ExtensionFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VeloDM.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "extension");
    }

    /// <summary>
    /// The background script reloads the extension when the folder's manifest names another
    /// version than the script does, so the two must be raised together.
    /// </summary>
    [Fact]
    public void Background_script_and_manifest_name_the_same_version()
    {
        string folder = ExtensionFolder();
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json")));
        string? inManifest = manifest.RootElement.GetProperty("version").GetString();

        var inScript = Regex.Match(File.ReadAllText(Path.Combine(folder, "background.js")),
            @"^const SCRIPT_VERSION = ""([\d.]+)"";", RegexOptions.Multiline);

        Assert.True(inScript.Success, "background.js does not declare SCRIPT_VERSION");
        Assert.Equal(inManifest, inScript.Groups[1].Value);
    }
}

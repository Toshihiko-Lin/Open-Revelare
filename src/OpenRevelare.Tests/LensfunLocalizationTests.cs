using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Platform;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class LensfunLocalizationTests
{
    [Fact]
    public void Bundled_languages_have_identical_keys_and_lensfun_format_arguments()
    {
        Dictionary<string, string> Read(string language)
        {
            var loader = new StandardAssetLoader(typeof(OpenRevelare.Gui.Views.ImportDialog).Assembly);
            using var stream = loader.Open(new Uri($"avares://OpenRevelare/Assets/i18n/{language}.json"));
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
        }
        var english = Read("en");
        var chinese = Read("zh-CN");
        Assert.Equal(english.Keys.Order(), chinese.Keys.Order());
        foreach (var key in english.Keys.Where(k => k.Contains("Lensfun") || k.Contains("首张") || k.Contains("暗角")))
        {
            Assert.False(string.IsNullOrWhiteSpace(english[key]));
            Assert.Equal(key, chinese[key]);
            string[] Arguments(string text) => Regex.Matches(text, @"\{\d+(?:[^{}]*)\}")
                .Select(m => m.Value).Order().ToArray();
            Assert.Equal(Arguments(key), Arguments(english[key]));
        }
    }
}

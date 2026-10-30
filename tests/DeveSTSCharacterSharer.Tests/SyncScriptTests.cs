using System.Text;
using DeveSTSCharacterSharer.Services;

namespace DeveSTSCharacterSharer.Tests;

public class SyncScriptTests
{
    [Theory]
    [InlineData("sts.example.com", "https://sts.example.com")]
    [InlineData("https://sts.example.com/", "https://sts.example.com")]
    [InlineData("192.168.1.10:8080", "http://192.168.1.10:8080")]
    [InlineData("localhost:8080", "http://localhost:8080")]
    [InlineData("nas:8080", "http://nas:8080")]
    [InlineData("  ", null)]
    public void NormalizePublicUrl(string raw, string? expected) =>
        Assert.Equal(expected, SharerOptions.NormalizePublicUrl(raw));

    [Fact]
    public void Render_BakesInTheServer_WithWindowsLineEndings()
    {
        var script = Encoding.ASCII.GetString(new SyncScript().Render("http://192.168.1.10:8080"));

        Assert.Contains("set \"SERVER=http://192.168.1.10:8080\"\r\n", script);
        Assert.DoesNotContain("__SERVER_URL__", script);
        Assert.DoesNotMatch("[^\r]\n", script);
        Assert.StartsWith("@echo off\r\n", script);
    }

    [Theory]
    [InlineData("http://evil\" & del *")]
    [InlineData("http://host/%TEMP%")]
    [InlineData("http://host/a(b)")]
    public void Render_RefusesUrlsThatCmdWouldInterpret(string url) =>
        Assert.Throws<InvalidOperationException>(() => new SyncScript().Render(url));
}

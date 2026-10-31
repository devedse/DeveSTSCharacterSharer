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
        var script = Encoding.ASCII.GetString(new SyncScript().RenderCmd("http://192.168.1.10:8080"));

        Assert.Contains("set \"SERVER=http://192.168.1.10:8080\"\r\n", script);
        Assert.Contains($"set \"MARKER={SyncScript.MarkerFileName}\"\r\n", script);
        Assert.DoesNotContain("__", script);
        Assert.DoesNotMatch("[^\r]\n", script);
        Assert.StartsWith("@echo off\r\n", script);
    }

    [Fact]
    public void RenderShell_BakesInTheServer_WithUnixLineEndings()
    {
        var script = Encoding.ASCII.GetString(new SyncScript().RenderShell("https://sts.example.com"));

        Assert.StartsWith("#!/bin/sh\n", script);
        Assert.Contains("SERVER=\"https://sts.example.com\"\n", script);
        Assert.Contains($"MARKER=\"{SyncScript.MarkerFileName}\"\n", script);
        Assert.Contains($"\"$SERVER/{SyncScript.ShellFileName}\"", script);
        Assert.DoesNotContain("__", script);
        Assert.DoesNotContain("\r", script);
    }

    [Theory]
    [InlineData("http://evil\" & del *")]
    [InlineData("http://host/%TEMP%")]
    [InlineData("http://host/a(b)")]
    [InlineData("http://host/$(reboot)")]
    [InlineData("http://host/`reboot`")]
    public void Render_RefusesUrlsThatCmdOrShWouldInterpret(string url)
    {
        Assert.Throws<InvalidOperationException>(() => new SyncScript().RenderCmd(url));
        Assert.Throws<InvalidOperationException>(() => new SyncScript().RenderShell(url));
    }
}

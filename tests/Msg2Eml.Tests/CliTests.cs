using MimeKit;
using CliProgram = Msg2Eml.Cli.Program;

namespace Msg2Eml.Tests;

public class CliTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "msg2eml-cli-" + Guid.NewGuid().ToString("N"));

    public CliTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static (int Code, string Out, string Err, byte[] Raw) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var raw = new MemoryStream();
        var code = CliProgram.Run(args, stdout, stderr, () => raw);
        return (code, stdout.ToString(), stderr.ToString(), raw.ToArray());
    }

    private string Copy(string fixture, string? name = null)
    {
        var dest = Path.Combine(_dir, name ?? fixture);
        File.Copy(Fixtures.Synthetic(fixture), dest);
        return dest;
    }

    [Fact]
    public void NoArguments_IsUsageError()
    {
        var r = Run();
        Assert.Equal(2, r.Code);
        Assert.Contains("no input", r.Err);
    }

    [Fact]
    public void Help_And_Version()
    {
        Assert.Equal(0, Run("--help").Code);
        Assert.Contains("usage:", Run("--help").Out);
        Assert.Matches(@"^\d+\.\d+\.\d+", Run("--version").Out.Trim());
    }

    [Fact]
    public void UnknownOption_IsUsageError()
    {
        var r = Run("--bogus", "x.msg");
        Assert.Equal(2, r.Code);
        Assert.Contains("unknown option", r.Err);
    }

    [Fact]
    public void SingleFile_WritesEmlNextToInput()
    {
        var msg = Copy("native-html.msg");
        var r = Run(msg);

        Assert.Equal(0, r.Code);
        var eml = Path.ChangeExtension(msg, ".eml");
        Assert.True(File.Exists(eml));
        Assert.Contains($"{msg} -> {eml}", r.Out);
        Assert.Equal("Native HTML message", MimeMessage.Load(eml).Subject);
    }

    [Fact]
    public void SingleFile_OutputToExplicitEmlPath()
    {
        var msg = Copy("plain-text.msg");
        var target = Path.Combine(_dir, "sub", "renamed.eml");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        Assert.Equal(0, Run(msg, "-o", target).Code);
        Assert.True(File.Exists(target));
    }

    [Fact]
    public void Directory_ConvertsEveryMsg_IntoOutputDir()
    {
        Copy("plain-text.msg");
        Copy("inline-images.msg");
        Copy("attachments.msg");
        var outDir = Path.Combine(_dir, "out");

        var r = Run(_dir, "-o", outDir, "-q");

        Assert.Equal(0, r.Code);
        Assert.Equal(["attachments.eml", "inline-images.eml", "plain-text.eml"],
            Directory.GetFiles(outDir).Select(Path.GetFileName).Order());
        Assert.Equal("", r.Out);
    }

    [Fact]
    public void Failures_AreReportedOnStderr_WithExitCode1_AndOthersStillConvert()
    {
        Copy("plain-text.msg");
        Copy("appointment.msg");
        File.WriteAllText(Path.Combine(_dir, "garbage.msg"), "not a msg");

        var r = Run(_dir);

        Assert.Equal(1, r.Code);
        Assert.Contains("appointment.msg: UnsupportedMessageType", r.Err);
        Assert.Contains("garbage.msg: InvalidMsgFile", r.Err);
        Assert.True(File.Exists(Path.Combine(_dir, "plain-text.eml")));
        Assert.Contains("1/3 converted", r.Out);
    }

    [Fact]
    public void ExistingOutput_IsNotOverwritten_WithoutForce()
    {
        var msg = Copy("plain-text.msg");
        var eml = Path.ChangeExtension(msg, ".eml");
        File.WriteAllText(eml, "keep me");

        Assert.Equal(1, Run(msg).Code);
        Assert.Equal("keep me", File.ReadAllText(eml));

        Assert.Equal(0, Run(msg, "--force").Code);
        Assert.NotEqual("keep me", File.ReadAllText(eml));
    }

    [Fact]
    public void Stdout_WritesRawEml()
    {
        var msg = Copy("plain-text.msg");
        var r = Run(msg, "--stdout");

        Assert.Equal(0, r.Code);
        Assert.NotEmpty(r.Raw);
        Assert.Equal("Plain text message", MimeMessage.Load(new MemoryStream(r.Raw)).Subject);
    }

    [Fact]
    public void Verbose_PrintsStats()
    {
        var msg = Copy("inline-images.msg");
        var r = Run(msg, "-v");
        Assert.Contains("inline=2", r.Out);
        Assert.Contains("headers=transport", r.Out);
    }

    [Fact]
    public void Limits_AreApplied()
    {
        var msg = Copy("inline-images.msg");
        var r = Run(msg, "--max-bytes", "10");
        Assert.Equal(1, r.Code);
        Assert.Contains("FileTooLarge", r.Err);
    }
}

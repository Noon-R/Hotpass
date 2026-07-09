using System.Diagnostics;
using System.Text;

namespace Hotpass.Adapters.Pix;

public sealed record PixToolResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
}

/// <summary>pixtool.exe のサブプロセス実行。</summary>
public sealed class PixToolRunner
{
    public string PixToolPath { get; }

    public PixToolRunner(string pixToolPath)
    {
        PixToolPath = pixToolPath;
    }

    public static PixToolRunner CreateDefault()
    {
        var path = PixToolLocator.Find()
            ?? throw new InvalidOperationException(
                "pixtool.exe が見つかりません。Microsoft PIX をインストールするか、環境変数 HOTPASS_PIXTOOL でパスを指定してください。");
        return new PixToolRunner(path);
    }

    public async Task<PixToolResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = PixToolPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"pixtool の起動に失敗: {PixToolPath}");
        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        return new PixToolResult(proc.ExitCode, await stdout, await stderr);
    }
}

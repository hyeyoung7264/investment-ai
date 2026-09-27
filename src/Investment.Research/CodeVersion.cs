using System.Diagnostics;

namespace Investment.Research;

public sealed record CodeVersion(string Commit, bool Dirty)
{
    /// <summary>Git commit of the working tree. Dirty = uncommitted changes (results may not be reproducible from the commit alone).</summary>
    public static CodeVersion Detect(string? workingDirectory = null)
    {
        try
        {
            var commit = Git("rev-parse HEAD", workingDirectory).Trim();
            var dirty = Git("status --porcelain --untracked-files=no", workingDirectory).Trim().Length > 0;
            return new CodeVersion(commit.Length == 40 ? commit : "unknown", dirty);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new CodeVersion("unknown", true);
        }
    }

    private static string Git(string args, string? cwd)
    {
        using var p = Process.Start(new ProcessStartInfo("git", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = cwd ?? Environment.CurrentDirectory,
        }) ?? throw new InvalidOperationException("git not started");
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException("git failed");
        return output;
    }
}

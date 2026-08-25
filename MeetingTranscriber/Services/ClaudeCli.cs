using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MeetingTranscriber.Services;

/// <summary>
/// Answers a question about a meeting by shelling out to the Claude Code
/// CLI in one-shot "print" mode, following the documented
/// <c>cat file | claude -p "question"</c> pattern: the transcript (and any
/// earlier turns in the conversation) is piped in on stdin, and the question
/// itself is passed as the -p argument. Each call is an independent process
/// - conversation continuity is the caller's job, by re-sending prior turns
/// as part of the piped context.
/// </summary>
public static class ClaudeCli
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static async Task<string> AskAsync(string context, string question, CancellationToken cancel = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeoutCts.CancelAfter(Timeout);

        var (exitCode, stdOut, stdErr) = await RunAsync(context, question, timeoutCts.Token).ConfigureAwait(false);

        if (exitCode != 0)
        {
            var message = string.IsNullOrWhiteSpace(stdErr) ? stdOut : stdErr;
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(message) ? $"Claude exited with code {exitCode}." : message.Trim());
        }
        return stdOut.Trim();
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string context, string question, CancellationToken cancel)
    {
        ProcessStartInfo BuildPsi(bool viaCmd)
        {
            var psi = new ProcessStartInfo
            {
                FileName = viaCmd ? "cmd.exe" : "claude",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Utf8NoBom,
                StandardErrorEncoding = Utf8NoBom,
                StandardInputEncoding = Utf8NoBom,
            };
            if (viaCmd)
            {
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add("claude");
                psi.ArgumentList.Add("-p");
                // cmd.exe expands %...% even inside a quoted argument; doubling
                // it neutralises that without touching anything else.
                psi.ArgumentList.Add(question.Replace("%", "%%"));
            }
            else
            {
                psi.ArgumentList.Add("-p");
                psi.ArgumentList.Add(question);
            }
            return psi;
        }

        static Process? StartOrNull(ProcessStartInfo psi)
        {
            try { return Process.Start(psi); }
            catch (Win32Exception) { return null; }
        }

        // The Claude CLI is a real .exe on some installs and a .cmd shim (from
        // a global npm install) on others; CreateProcess can launch the
        // former directly but the latter only via cmd.exe, so try direct
        // first and fall back rather than guessing which this machine has.
        using var process = StartOrNull(BuildPsi(viaCmd: false)) ?? StartOrNull(BuildPsi(viaCmd: true))
            ?? throw new FileNotFoundException(
                "Could not find the Claude CLI (\"claude\"). Make sure it's installed and on PATH.");

        await process.StandardInput.WriteAsync(context).ConfigureAwait(false);
        process.StandardInput.Close();

        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(cancel).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { }
            throw new TimeoutException("Claude did not respond in time.");
        }

        return (process.ExitCode, await stdOutTask.ConfigureAwait(false), await stdErrTask.ConfigureAwait(false));
    }
}

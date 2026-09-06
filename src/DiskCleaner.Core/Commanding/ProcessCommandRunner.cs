using System.Diagnostics;
using System.Text;

namespace DiskCleaner.Core.Commanding;

public sealed class ProcessCommandRunner : ICommandRunner
{
    private static readonly Encoding ConsoleEncoding = Encoding.UTF8;

    public async Task<CommandResult> RunAsync(
        CommandDefinition command,
        CancellationToken cancellationToken = default)
    {
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, command.TimeoutSec)));
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        var startInfo = new ProcessStartInfo
        {
            FileName = command.FileName,
            Arguments = command.Arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = ConsoleEncoding,
            StandardErrorEncoding = ConsoleEncoding,
            WorkingDirectory = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile)
        };

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new CommandResult(-1, $"Не удалось запустить '{command.FileName}': {ex.Message}", false);
        }

        var outputTask = ReadAsync(process.StandardOutput);
        var errorTask = ReadAsync(process.StandardError);

        try
        {
            await process.WaitForExitAsync(linkedSource.Token);
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            await Task.WhenAll(outputTask, errorTask);
            return new CommandResult(-1, "Команда превысила допустимое время ожидания и была остановлена", true);
        }

        var output = (await outputTask) + (await errorTask).TrimEnd();
        return new CommandResult(process.ExitCode, output.Trim(), false);
    }

    private static async Task<string> ReadAsync(StreamReader reader)
    {
        using var buffer = new StringWriter();
        var chunk = new char[8192];
        int read;
        while ((read = await reader.ReadAsync(chunk, 0, chunk.Length)) > 0)
        {
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToString();
    }
}

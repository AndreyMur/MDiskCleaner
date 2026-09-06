namespace DiskCleaner.Core.Commanding;

public interface ICommandRunner
{
    Task<CommandResult> RunAsync(CommandDefinition command, CancellationToken cancellationToken = default);
}

namespace DiskCleaner.Core.Elevated;

public interface IElevatedRunner
{
    Task<ElevatedJournal> RunAsync(
        ElevatedScenario scenario,
        CancellationToken cancellationToken = default);
}

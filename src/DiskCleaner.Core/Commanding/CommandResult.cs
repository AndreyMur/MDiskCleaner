namespace DiskCleaner.Core.Commanding;

public sealed record CommandResult(int ExitCode, string Output, bool TimedOut);

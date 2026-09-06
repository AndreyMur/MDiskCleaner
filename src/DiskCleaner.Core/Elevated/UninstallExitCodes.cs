namespace DiskCleaner.Core.Elevated;

public enum ProcessExitMeaning
{
    Success,
    RebootRequired,
    NotInstalled,
    Failed,
    TimedOut
}

public static class UninstallExitCodes
{
    public const int Success = 0;
    public const int RebootRequired = 3010;
    public const int ProductNotInstalled = 1605;
    public const int InstallSourceAbsent = 1612;

    public static ProcessExitMeaning Classify(int exitCode) => exitCode switch
    {
        Success => ProcessExitMeaning.Success,
        RebootRequired => ProcessExitMeaning.RebootRequired,
        ProductNotInstalled or InstallSourceAbsent => ProcessExitMeaning.NotInstalled,
        _ => ProcessExitMeaning.Failed
    };

    public static bool IsOk(int exitCode) => exitCode switch
    {
        Success or RebootRequired or ProductNotInstalled or InstallSourceAbsent => true,
        _ => false
    };
}

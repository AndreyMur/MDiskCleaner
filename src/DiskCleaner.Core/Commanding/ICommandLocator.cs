namespace DiskCleaner.Core.Commanding;

public interface ICommandLocator
{
    bool IsAvailable(string commandName);
}

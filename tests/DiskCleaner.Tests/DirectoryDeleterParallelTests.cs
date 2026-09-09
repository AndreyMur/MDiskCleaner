using DiskCleaner.Core.Cleaning;
using DiskCleaner.Core.Models;

namespace DiskCleaner.Tests;

/// <summary>
/// Тест на рекурсивное удаление каталога параллельными партиями: гейт
/// (<see cref="DeletionState"/>-семафор) не должен приводить к взаимоблокировке на деревьях,
/// где каждый каталог содержит вложенные каталоги. Регрессионный тест GUI-зависания
/// «выполнить план» (DirectoryDeleter застревает на SemaphoreSlim при прямом удалении остатка).
/// </summary>
public class DirectoryDeleterParallelTests
{
    [Fact]
    public async Task DeletePathAsync_DeepWideTree_DoesNotDeadlock_AndDeletesAll()
    {
        using var root = new TempRoot();
        var tree = root.Combine("deep-tree");

        long expectedFiles = 0;
        for (var a = 0; a < 10; a++)
        {
            for (var b = 0; b < 10; b++)
            {
                for (var c = 0; c < 10; c++)
                {
                    for (var f = 0; f < 5; f++)
                    {
                        root.CreateFile($"deep-tree\\d{a}\\e{b}\\f{c}\\file{f}.bin", 64);
                        expectedFiles++;
                    }
                }
            }
        }

        var deleter = new DirectoryDeleter();

        var delete = deleter.DeletePathAsync(tree, CleanupTarget.Directory);
        var completed = await Task.WhenAny(delete, Task.Delay(TimeSpan.FromSeconds(60)));

        Assert.True(completed == delete, "Прямое удаление глубокого/широкого дерева каталогов не должно взаимоблокироваться.");
        var outcome = await delete;

        Assert.True(outcome.FullyDeleted);
        Assert.False(Directory.Exists(tree));
        Assert.Equal(expectedFiles, outcome.DeletedFiles);
    }
}

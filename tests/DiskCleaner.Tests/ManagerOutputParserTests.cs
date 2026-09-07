using DiskCleaner.Core.Caches;

namespace DiskCleaner.Tests;

/// <summary>
/// Unit-тесты разбора выходных данных штатных команд менеджеров (фаза 3 модуля 02):
/// «Total reclaimed space: 2GB» (docker), кириллический вывод и отсутствие объёма.
/// </summary>
public class ManagerOutputParserTests
{
    [Fact]
    public void TryParseReclaimedBytes_NullOutput_ReturnsNull()
    {
        Assert.Null(ManagerOutputParser.TryParseReclaimedBytes(null));
    }

    [Fact]
    public void TryParseReclaimedBytes_EmptyOutput_ReturnsNull()
    {
        Assert.Null(ManagerOutputParser.TryParseReclaimedBytes(string.Empty));
        Assert.Null(ManagerOutputParser.TryParseReclaimedBytes("   \n \t "));
    }

    [Fact]
    public void TryParseReclaimedBytes_DockerGbOutput_ReturnsBytes()
    {
        var bytes = ManagerOutputParser.TryParseReclaimedBytes("Total reclaimed space: 2GB");

        Assert.Equal(2L * 1024 * 1024 * 1024, bytes);
    }

    [Fact]
    public void TryParseReclaimedBytes_DockerDecimalOutput_ReturnsBytes()
    {
        var bytes = ManagerOutputParser.TryParseReclaimedBytes("Total reclaimed space: 1.5GB");

        Assert.Equal((long)(1.5 * 1024 * 1024 * 1024), bytes);
    }

    [Fact]
    public void TryParseReclaimedBytes_MultilineDockerOutput_PicksReclaimedLine()
    {
        var output = string.Join(Environment.NewLine,
            "Deleted Containers: 2",
            "Deleted Images: 5",
            "Deleted Networks: 1",
            "Total reclaimed space: 3GB");

        var bytes = ManagerOutputParser.TryParseReclaimedBytes(output);

        Assert.Equal(3L * 1024 * 1024 * 1024, bytes);
    }

    [Fact]
    public void TryParseReclaimedBytes_CountsWithoutSize_ReturnNull()
    {
        Assert.Null(ManagerOutputParser.TryParseReclaimedBytes("Deleted Containers: 2"));
    }

    [Fact]
    public void TryParseReclaimedBytes_KibibyteUnit_ReturnsBytes()
    {
        Assert.Equal(4L * 1024 * 1024, ManagerOutputParser.TryParseReclaimedBytes("Reclaimed: 4MiB"));
    }

    [Fact]
    public void TryParseReclaimedBytes_CyrillicOutput_ReturnsBytes()
    {
        Assert.Equal(2L * 1024 * 1024 * 1024, ManagerOutputParser.TryParseReclaimedBytes("Освобождено: 2 ГБ"));
    }

    [Fact]
    public void TryParseReclaimedBytes_CyrillicCommaDecimal_ReturnsBytes()
    {
        Assert.Equal((long)(1.5 * 1024 * 1024 * 1024), ManagerOutputParser.TryParseReclaimedBytes("Освобождено: 1,5 ГБ"));
    }

    [Fact]
    public void TryParseReclaimedBytes_NoReclaimKeyword_ReturnsNull()
    {
        Assert.Null(ManagerOutputParser.TryParseReclaimedBytes("nothing to do here"));
    }
}

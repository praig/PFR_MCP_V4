using System.Text;
using Pfr.Core.Nios.Backup;

namespace Pfr.Core.Tests.Nios;

public class BackupSourceTests
{
    [Theory]
    [InlineData("./onedb.xml")]
    [InlineData("onedb.xml")]
    [InlineData("storage/onedb.xml")]
    public void Opens_onedb_inside_gzip_tar(string entryName)
    {
        using var stream = BackupSource.OpenOneDb(OneDbFixture.GzipTarBackup(entryName));
        var inv = BackupParser.Parse(stream);
        Assert.Equal("Lab Grid", inv.GridName);
        Assert.Equal(3, inv.Members.Count);
    }

    [Fact]
    public void Opens_bare_onedb_xml()
    {
        using var stream = BackupSource.OpenOneDb(OneDbFixture.XmlStream());
        Assert.Equal("Lab Grid", BackupParser.Parse(stream).GridName);
    }

    [Fact]
    public void Parses_backup_file_from_disk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pfr-test-{Guid.NewGuid():N}.bak");
        try
        {
            using (var file = File.Create(path))
            {
                OneDbFixture.GzipTarBackup().CopyTo(file);
            }

            var inv = BackupParser.ParseFile(path);
            Assert.Equal(4, inv.Zones.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Rejects_unknown_file()
    {
        var junk = new MemoryStream(Encoding.ASCII.GetBytes("this is not a backup"));
        Assert.Throws<InvalidDataException>(() => BackupSource.OpenOneDb(junk));
    }
}

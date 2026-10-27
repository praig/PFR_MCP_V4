using Pfr.Core.Nios.Backup;

namespace Pfr.Core.Tests.Nios;

/// <summary>
/// Runs against a real grid backup when PFR_SAMPLE_BACKUP points at one
/// (keep real backups in samples/private/, which git ignores).
/// Skipped otherwise, so CI never needs client data.
/// </summary>
public class RealBackupTests
{
    private static string? SamplePath => Environment.GetEnvironmentVariable("PFR_SAMPLE_BACKUP");

    [Fact]
    public void Parses_real_backup_when_available()
    {
        if (SamplePath is not { } path || !File.Exists(path))
        {
            return; // no sample configured
        }

        var inv = BackupParser.ParseFile(path);

        Assert.False(string.IsNullOrEmpty(inv.NiosVersion));
        Assert.NotEmpty(inv.Members);
        Assert.Single(inv.Members, m => m.IsGridMaster);
        Assert.All(inv.Members, m => Assert.False(string.IsNullOrEmpty(m.HostName)));
        Assert.All(inv.Zones, z => Assert.False(string.IsNullOrEmpty(z.Fqdn)));
    }
}

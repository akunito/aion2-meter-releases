namespace AionMeter.Tests;

/// <summary>The login recording the replay tests read. Fixtures/pcaps/ is gitignored (a real capture of the maintainer's characters):
/// a clone without it skips these tests instead of failing them.</summary>
public static class PcapFixture
{
    public static string Login => Path.Combine(AppContext.BaseDirectory, "Fixtures", "pcaps", "login-20261009.pcap");
}

public sealed class PcapFactAttribute : FactAttribute
{
    public PcapFactAttribute()
    {
        if (!File.Exists(PcapFixture.Login)) Skip = "needs Fixtures/pcaps/login-20261009.pcap (not in the repository)";
    }
}

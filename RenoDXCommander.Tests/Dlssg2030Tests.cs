using RenoDXCommander.Services;
using Xunit;

namespace RenoDXCommander.Tests;

/// <summary>
/// 0.4.x factory INIs no longer contain a [Compatibility] section, but RHI's GPU-generation
/// picker still has to be able to pin Router=SM86/SM75.
/// </summary>
public class Dlssg2030Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rhi-dlssg-" + Guid.NewGuid().ToString("N"));

    public Dlssg2030Tests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* temp folder; ignore */ }
    }

    private string WriteIni(string contents)
    {
        var path = Path.Combine(_dir, Dlssg20_30Service.IniFileName);
        File.WriteAllText(path, contents);
        return path;
    }

    [Fact]
    public void MissingCompatibilitySection_IsCreatedWithRouter()
    {
        var path = WriteIni("[General]\nEnabled=1\n[Runtime]\nMode=Bundled\n");

        Dlssg20_30Service.ApplyRouterToIniPublic(path, Dlssg20_30Service.GpuGenRtx20);

        var lines = File.ReadAllLines(path);
        Assert.Contains("[Compatibility]", lines);
        Assert.Contains("Router=SM75", lines);
    }

    [Fact]
    public void ExistingRouter_IsReplaced()
    {
        var path = WriteIni("[General]\nEnabled=1\n[Compatibility]\nRouter=SM86\nImagePatches=1\n");

        Dlssg20_30Service.ApplyRouterToIniPublic(path, Dlssg20_30Service.GpuGenRtx20);

        var lines = File.ReadAllLines(path);
        Assert.Single(lines.Where(l => l.StartsWith("Router=", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains("Router=SM75", lines);
    }

    [Fact]
    public void Rtx30_WritesSm86()
    {
        var path = WriteIni("[Compatibility]\nImagePatches=1\n");

        Dlssg20_30Service.ApplyRouterToIniPublic(path, Dlssg20_30Service.GpuGenRtx30);

        Assert.Contains("Router=SM86", File.ReadAllLines(path));
    }
}

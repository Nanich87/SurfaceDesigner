using SurfaceDesigner.Core.IO;

namespace SurfaceDesigner.Core.Tests;

public sealed class AutosaveStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sd-autosave-" + Guid.NewGuid().ToString("N"));

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException) { }
    }

    private static string Json(string name)
    {
        var p = new Project();
        p.Surfaces.Add(new Surfaces.Surface(name));
        return ProjectSerializer.ToJson(p);
    }

    [Fact]
    public void CopyOfEndedSession_IsOfferedWithOriginalPath()
    {
        using (var crashed = new AutosaveStore(_dir))
        {
            Assert.True(crashed.Save(Json("Терен"), @"C:\proj\a.sdproj", crashed.Generation));
        }

        // Dispose без Clear = сесията е прекъсната
        using var next = new AutosaveStore(_dir);
        var entry = Assert.Single(next.FindRecoverable());
        Assert.Equal(@"C:\proj\a.sdproj", entry.OriginalPath);
        Assert.Equal("Терен", ProjectSerializer.Load(entry.ProjectFile).Surfaces[0].Name);

        next.Discard(entry);
        Assert.Empty(next.FindRecoverable());
    }

    [Fact]
    public void CopyOfRunningSession_IsNotOffered()
    {
        using var running = new AutosaveStore(_dir);
        running.Save(Json("A"), null, running.Generation);

        using var other = new AutosaveStore(_dir);
        Assert.Empty(other.FindRecoverable());

        other.Discard(new AutosaveEntry(Path.Combine(_dir, running.SessionId + ".sdproj"), null, DateTime.UtcNow));
        Assert.True(File.Exists(Path.Combine(_dir, running.SessionId + ".sdproj")));
    }

    [Fact]
    public void ClearedSession_LeavesNothingToRecover()
    {
        using (var s = new AutosaveStore(_dir))
        {
            s.Save(Json("A"), null, s.Generation);
            s.Clear();
        }

        using var next = new AutosaveStore(_dir);
        Assert.Empty(next.FindRecoverable());
    }

    [Fact]
    public void SaveStartedBeforeClear_IsDropped()
    {
        using var s = new AutosaveStore(_dir);
        int gen = s.Generation;
        s.Clear();

        Assert.False(s.Save(Json("A"), null, gen));
        Assert.Empty(Directory.GetFiles(_dir, "*.sdproj"));
    }

    [Fact]
    public void LeftoverLockFile_IsTreatedAsEndedSession()
    {
        string id;
        using (var s = new AutosaveStore(_dir))
        {
            id = s.SessionId;
            s.Save(Json("A"), null, s.Generation);
        }

        // Изключване на тока: заключващият файл може да остане, но никой не го държи отворен
        File.WriteAllText(Path.Combine(_dir, id + ".lock"), "");

        using var next = new AutosaveStore(_dir);
        Assert.Single(next.FindRecoverable());
    }

    [Fact]
    public void NewestCopyComesFirst()
    {
        foreach (var name in new[] { "Стара", "Нова" })
        {
            using var s = new AutosaveStore(_dir);
            s.Save(Json(name), null, s.Generation);
            Thread.Sleep(20);
        }

        using var next = new AutosaveStore(_dir);
        var entries = next.FindRecoverable();
        Assert.Equal(2, entries.Count);
        Assert.Equal("Нова", ProjectSerializer.Load(entries[0].ProjectFile).Surfaces[0].Name);
    }
}

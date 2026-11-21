using SurfaceDesigner.Core.Geometry;
using SurfaceDesigner.Core.IO;
using SurfaceDesigner.Core.Surfaces;

namespace SurfaceDesigner.Core.Tests;

public sealed class ProjectSaveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sd-save-" + Guid.NewGuid().ToString("N"));

    public ProjectSaveTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static Project MakeProject(string surfaceName)
    {
        var p = new Project();
        p.Surfaces.Add(new Surface(surfaceName, new[] { new Point3(0, 0, 1), new Point3(10, 0, 2), new Point3(0, 10, 3) }));
        return p;
    }

    [Fact]
    public void Save_NewFile_WritesProjectWithoutBackupOrTempFiles()
    {
        string path = Path.Combine(_dir, "a.sdproj");

        ProjectSerializer.Save(MakeProject("Терен"), path);

        Assert.Equal("Терен", ProjectSerializer.Load(path).Surfaces[0].Name);
        Assert.False(File.Exists(ProjectSerializer.BackupPath(path)));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public void Save_ExistingFile_KeepsPreviousVersionAsBackup()
    {
        string path = Path.Combine(_dir, "a.sdproj");

        ProjectSerializer.Save(MakeProject("Първа"), path);
        ProjectSerializer.Save(MakeProject("Втора"), path);

        Assert.Equal("Втора", ProjectSerializer.Load(path).Surfaces[0].Name);
        Assert.Equal("Първа", ProjectSerializer.Load(ProjectSerializer.BackupPath(path)).Surfaces[0].Name);
        Assert.Equal(2, Directory.GetFiles(_dir).Length); // без останали временни файлове
    }

    [Fact]
    public void Save_WhenWriteFails_LeavesOriginalIntact()
    {
        string path = Path.Combine(_dir, "a.sdproj");
        ProjectSerializer.Save(MakeProject("Оригинал"), path);

        // Целта е заключена от друг процес – подмяната трябва да се провали, без да повреди файла
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<Exception>(() => ProjectSerializer.Save(MakeProject("Нова"), path));
        }

        Assert.Equal("Оригинал", ProjectSerializer.Load(path).Surfaces[0].Name);
        Assert.DoesNotContain(Directory.GetFiles(_dir), f => f.EndsWith(".tmp"));
    }
}

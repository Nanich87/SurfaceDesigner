using System.Text.Json;

namespace SurfaceDesigner.Core.IO;

/// <summary>Автоматично копие, останало от сесия, която не е затворена нормално (напр. срив).</summary>
/// <param name="ProjectFile">Файлът с копието на проекта.</param>
/// <param name="OriginalPath">Пътят на проекта, от който е копието, или null за незаписан проект.</param>
/// <param name="SavedAtUtc">Кога е направено копието.</param>
public sealed record AutosaveEntry(string ProjectFile, string? OriginalPath, DateTime SavedAtUtc)
{
    internal string SessionId => Path.GetFileNameWithoutExtension(ProjectFile);
}

/// <summary>
/// Автоматични копия на проекта в отделна папка (по подразбиране %LOCALAPPDATA%\SurfaceDesigner\Autosave).
/// </summary>
/// <remarks>
/// Всяка работеща сесия държи отворен заключващ файл <c>&lt;id&gt;.lock</c>, който се изтрива от Windows при
/// затваряне на процеса – включително при срив. Копие без жива сесия значи, че програмата не е затворена
/// нормално, и може да се предложи за възстановяване. Така няколко едновременно отворени прозореца не си
/// пречат: копията на работеща сесия никога не се предлагат.
/// </remarks>
public sealed class AutosaveStore : IDisposable
{
    private const string ProjectExt = ".sdproj";
    private const string MetaExt = ".meta.json";
    private const string LockExt = ".lock";

    private readonly object _sync = new();
    private readonly FileStream _lock;
    private int _generation;

    public AutosaveStore(string directory)
    {
        Directory.CreateDirectory(directory);
        DirectoryPath = directory;
        SessionId = Guid.NewGuid().ToString("N");
        _lock = new FileStream(
            Path.Combine(directory, SessionId + LockExt),
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            1,
            FileOptions.DeleteOnClose);
    }

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SurfaceDesigner", "Autosave");

    public string DirectoryPath { get; }

    public string SessionId { get; }

    /// <summary>
    /// Брояч, който се увеличава при всяко <see cref="Clear"/>. Вземете го преди да подготвите копието и го
    /// подайте на <see cref="Save"/> – така запис, започнал преди изчистването, не възкресява старо копие.
    /// </summary>
    public int Generation => Volatile.Read(ref _generation);

    /// <summary>Записва копие на текущата сесия. Връща false, ако междувременно е извикан <see cref="Clear"/>.</summary>
    public bool Save(string projectJson, string? originalPath, int generation)
    {
        lock (_sync)
        {
            if (generation != _generation) return false;
            ProjectSerializer.WriteAtomic(ProjectFile(SessionId), projectJson, backupPath: null);
            var meta = new Meta { OriginalPath = originalPath, SavedAtUtc = DateTime.UtcNow };
            ProjectSerializer.WriteAtomic(MetaFile(SessionId), JsonSerializer.Serialize(meta), backupPath: null);
            return true;
        }
    }

    /// <summary>Изтрива копието на текущата сесия (след запис на проекта или отказ от промените).</summary>
    public void Clear()
    {
        lock (_sync)
        {
            _generation++;
            DeleteFiles(SessionId);
        }
    }

    /// <summary>Копия от сесии, които вече не работят – най-новото първо.</summary>
    public IReadOnlyList<AutosaveEntry> FindRecoverable()
    {
        var result = new List<AutosaveEntry>();
        foreach (var file in Directory.EnumerateFiles(DirectoryPath, "*" + ProjectExt))
        {
            string id = Path.GetFileNameWithoutExtension(file);
            if (id == SessionId || IsSessionAlive(id)) continue;

            string? original = null;
            DateTime savedAt = File.GetLastWriteTimeUtc(file);
            try
            {
                var meta = JsonSerializer.Deserialize<Meta>(File.ReadAllText(MetaFile(id)));
                if (meta != null)
                {
                    original = meta.OriginalPath;
                    savedAt = meta.SavedAtUtc;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // Без метаданни копието пак е използваемо – като незаписан проект
            }

            result.Add(new AutosaveEntry(file, original, savedAt));
        }

        return result.OrderByDescending(e => e.SavedAtUtc).ToList();
    }

    /// <summary>Изтрива копие от друга (неработеща) сесия.</summary>
    public void Discard(AutosaveEntry entry)
    {
        if (entry.SessionId == SessionId || IsSessionAlive(entry.SessionId)) return;
        DeleteFiles(entry.SessionId);
    }

    /// <summary>Освобождава сесията. Копието остава, ако не е извикан <see cref="Clear"/>.</summary>
    public void Dispose() => _lock.Dispose();

    private bool IsSessionAlive(string id)
    {
        string lockFile = Path.Combine(DirectoryPath, id + LockExt);
        if (!File.Exists(lockFile)) return false;
        try
        {
            // Заключващ файл, който може да се отвори, е останал от прекъснат процес
            using (new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            File.Delete(lockFile);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private void DeleteFiles(string id)
    {
        foreach (var f in new[] { ProjectFile(id), MetaFile(id) })
        {
            try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private string ProjectFile(string id) => Path.Combine(DirectoryPath, id + ProjectExt);

    private string MetaFile(string id) => Path.Combine(DirectoryPath, id + MetaExt);

    private sealed class Meta
    {
        public string? OriginalPath { get; set; }
        public DateTime SavedAtUtc { get; set; }
    }
}

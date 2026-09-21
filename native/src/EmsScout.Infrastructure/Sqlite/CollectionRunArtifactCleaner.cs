using System.Text.Json;
using EmsScout.Application.Collection;
using Microsoft.Data.Sqlite;

namespace EmsScout.Infrastructure.Sqlite;

public sealed class CollectionRunArtifactCleaner(Func<string> databasePathResolver)
{
    public IReadOnlyList<RunArtifactCandidate> FindCandidates(CollectionRunRecord run)
    {
        var root = DatabaseDirectory();
        var candidates = new Dictionary<string, RunArtifactCandidate>(StringComparer.OrdinalIgnoreCase);

        AddReferenced(candidates, root, run.JsonPath, "json", HasMatchingIdentity, run);
        AddReferenced(candidates, root, run.DbSnapshotPath, "sqlite", HasMatchingSqliteIdentity, run);

        var generated = new[]
        {
            ($"quality_report_run{run.Id}.json", "quality-report"),
            ($"quality_report_run{run.Id}.txt", "quality-report-text"),
            ($"collection_manifest_{run.Id}.json", "manifest"),
            ($"realtime_all_buildings_batch_summary_run{run.Id}.json", "realtime-summary"),
            ($"realtime_all_buildings_batch_failure_run{run.Id}.json", "realtime-failure"),
            ($"realtime_quality_classified_run{run.Id}.json", "realtime-quality"),
            ($"realtime_quality_classified_{run.Id}.json", "realtime-quality"),
        };

        foreach (var (name, kind) in generated)
        {
            var path = Path.Combine(root, name);
            if (!File.Exists(path))
            {
                continue;
            }

            var identityVerified = kind is "quality-report-text"
                ? HasMatchingIdentity(Path.Combine(root, $"quality_report_run{run.Id}.json"), run)
                : HasMatchingIdentity(path, run);
            AddPath(candidates, root, path, kind, identityVerified);

            if (kind == "manifest" && identityVerified)
            {
                AddManifestFiles(candidates, root, path, run);
            }
        }

        if (Directory.Exists(root))
        {
            foreach (var path in Directory.EnumerateFiles(root, "realtime_quality_classified_*.json"))
            {
                if (HasMatchingIdentity(path, run))
                {
                    AddPath(candidates, root, path, "realtime-quality", identityVerified: true);
                }
            }
        }

        return candidates.Values.ToArray();
    }

    public ArtifactCleanupResult Cleanup(
        CollectionRunRecord run,
        IReadOnlyList<RunArtifactCandidate> candidates)
    {
        var root = DatabaseDirectory();
        var deleted = 0;
        var pendingPaths = new List<string>();
        var pendingReasons = new List<string>();
        foreach (var candidate in candidates.Where(item => !item.IsShared))
        {
            if (!candidate.IdentityVerified)
            {
                pendingPaths.Add(candidate.RelativePath);
                pendingReasons.Add("文件缺少可验证的批次身份，未自动删除");
                continue;
            }

            var path = Path.GetFullPath(Path.Combine(root, candidate.RelativePath));
            if (!IsInside(root, path) || string.Equals(path, Path.GetFullPath(databasePathResolver()), StringComparison.OrdinalIgnoreCase))
            {
                pendingPaths.Add(candidate.RelativePath);
                pendingReasons.Add("文件路径不在本地数据目录内，未删除");
                continue;
            }

            if (ContainsReparsePoint(root, path))
            {
                pendingPaths.Add(candidate.RelativePath);
                pendingReasons.Add("产物路径包含符号链接、junction 或其他重解析点，未删除");
                continue;
            }

            try
            {
                if (Directory.Exists(path))
                {
                    pendingPaths.Add(candidate.RelativePath);
                    pendingReasons.Add("产物路径是目录，禁止递归删除");
                    continue;
                }

                if (File.Exists(path))
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    {
                        pendingPaths.Add(candidate.RelativePath);
                        pendingReasons.Add("文件是重解析点，未删除");
                        continue;
                    }

                    if (string.Equals(Path.GetExtension(path), ".db", StringComparison.OrdinalIgnoreCase))
                    {
                        SqliteConnection.ClearAllPools();
                    }

                    File.Delete(path);
                    deleted++;
                }
                else
                {
                    pendingPaths.Add(candidate.RelativePath);
                    pendingReasons.Add("产物在清理前已消失，未确认删除结果");
                }
            }
            catch (IOException ex)
            {
                pendingPaths.Add(candidate.RelativePath);
                pendingReasons.Add("文件被占用或暂时不可写：" + ex.GetType().Name);
            }
            catch (UnauthorizedAccessException)
            {
                pendingPaths.Add(candidate.RelativePath);
                pendingReasons.Add("没有删除文件的权限");
            }
        }

        return new ArtifactCleanupResult(deleted, pendingPaths, pendingReasons);
    }

    private void AddManifestFiles(
        IDictionary<string, RunArtifactCandidate> candidates,
        string root,
        string manifestPath,
        CollectionRunRecord run)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!document.RootElement.TryGetProperty("resultFiles", out var files) ||
                files.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var item in files.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var value = item.GetString();
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var childPath = Path.IsPathRooted(value) ? value : Path.Combine(root, value);
                if (string.Equals(Path.GetExtension(childPath), ".log", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var identityVerified = HasMatchingIdentity(childPath, run) ||
                    HasMatchingNdjsonIdentity(childPath, run);
                AddPath(candidates, root, childPath, "realtime-detail", identityVerified);
            }
        }
        catch
        {
            // The manifest itself remains visible as an unverified artifact.
        }
    }

    private static void AddReferenced(
        IDictionary<string, RunArtifactCandidate> candidates,
        string root,
        string? value,
        string kind,
        Func<string, CollectionRunRecord, bool> identityVerifier,
        CollectionRunRecord run)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var path = Path.IsPathRooted(value) ? value : Path.Combine(root, value);
        AddPath(candidates, root, path, kind, identityVerifier(path, run));
    }

    private static void AddPath(
        IDictionary<string, RunArtifactCandidate> candidates,
        string root,
        string path,
        string kind,
        bool identityVerified)
    {
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(root, fullPath);
        if (IsProtectedArtifact(relative, root))
        {
            return;
        }

        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            return;
        }

        var normalized = relative.Replace(Path.DirectorySeparatorChar, '/');
        var candidate = new RunArtifactCandidate(normalized, kind, false, identityVerified);
        if (!candidates.TryGetValue(normalized, out var existing) ||
            !existing.IdentityVerified && identityVerified)
        {
            candidates[normalized] = candidate;
        }
    }

    private static bool HasMatchingIdentity(string path, CollectionRunRecord run)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var runId = ReadLong(root, "runId") ?? ReadLong(root, "run_id");
            if (runId != run.Id)
            {
                return false;
            }

            var batchUid = ReadString(root, "batchUid") ?? ReadString(root, "batch_uid");
            var runKey = ReadString(root, "runKey") ?? ReadString(root, "run_key");
            return !string.IsNullOrWhiteSpace(run.BatchUid) &&
                   !string.IsNullOrWhiteSpace(run.RunKey) &&
                   !string.IsNullOrWhiteSpace(batchUid) &&
                   string.Equals(batchUid, run.BatchUid, StringComparison.Ordinal) &&
                   !string.IsNullOrWhiteSpace(runKey) &&
                   string.Equals(runKey, run.RunKey, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static bool HasMatchingNdjsonIdentity(string path, CollectionRunRecord run)
    {
        if (!File.Exists(path) ||
            !string.Equals(Path.GetExtension(path), ".ndjson", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var foundRecord = false;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(line);
                if (!HasMatchingIdentity(document.RootElement, run))
                {
                    return false;
                }

                foundRecord = true;
            }
        }
        catch
        {
            return false;
        }

        return foundRecord;
    }

    private static bool HasMatchingIdentity(JsonElement root, CollectionRunRecord run)
    {
        var runId = ReadLong(root, "runId") ?? ReadLong(root, "run_id");
        if (runId != run.Id)
        {
            return false;
        }

        var batchUid = ReadString(root, "batchUid") ?? ReadString(root, "batch_uid");
        var runKey = ReadString(root, "runKey") ?? ReadString(root, "run_key");
        return !string.IsNullOrWhiteSpace(run.BatchUid) &&
               !string.IsNullOrWhiteSpace(run.RunKey) &&
               !string.IsNullOrWhiteSpace(batchUid) &&
               string.Equals(batchUid, run.BatchUid, StringComparison.Ordinal) &&
               !string.IsNullOrWhiteSpace(runKey) &&
               string.Equals(runKey, run.RunKey, StringComparison.Ordinal);
    }

    private string DatabaseDirectory() =>
        Path.GetFullPath(Path.GetDirectoryName(databasePathResolver()) ?? Directory.GetCurrentDirectory());

    private static bool IsInside(string root, string path) =>
        string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsReparsePoint(string root, string path)
    {
        if (!IsInside(root, path))
        {
            return true;
        }

        var current = root;
        if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
        {
            return true;
        }

        var relative = Path.GetRelativePath(root, path);
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (string.IsNullOrWhiteSpace(segment) || segment == ".")
            {
                continue;
            }

            current = Path.Combine(current, segment);
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }

        return File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    }

    private static bool IsProtectedArtifact(string relativePath, string root)
    {
        var name = Path.GetFileName(relativePath);
        return string.Equals(relativePath, ".", StringComparison.OrdinalIgnoreCase) ||
               name.Equals(Path.GetFileName(root), StringComparison.OrdinalIgnoreCase) ||
               name.Equals("ac.db", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("enum_full_v5.json", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("quality_report.json", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("quality_report.txt", StringComparison.OrdinalIgnoreCase) ||
               (name.StartsWith("realtime_", StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith("_latest.json", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasMatchingSqliteIdentity(string path, CollectionRunRecord run)
    {
        if (!File.Exists(path) || !string.Equals(Path.GetExtension(path), ".db", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT batch_uid, run_key FROM collection_runs WHERE id = $id LIMIT 1";
            command.Parameters.AddWithValue("$id", run.Id);
            using var reader = command.ExecuteReader();
            return reader.Read() &&
                   !reader.IsDBNull(0) &&
                   !reader.IsDBNull(1) &&
                   string.Equals(reader.GetString(0), run.BatchUid, StringComparison.Ordinal) &&
                   string.Equals(reader.GetString(1), run.RunKey, StringComparison.Ordinal);
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static long? ReadLong(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out var text)
            ? text
            : null;
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

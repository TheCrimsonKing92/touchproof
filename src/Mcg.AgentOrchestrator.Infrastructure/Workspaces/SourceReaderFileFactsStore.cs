using System.Text.Json;
using System.Text.Json.Serialization;
using static Mcg.AgentOrchestrator.Infrastructure.SourceReaderAnalysisCache;

namespace Mcg.AgentOrchestrator.Infrastructure;

// One record per content hash. A publication pins each record's semantic facts to the tree
// that produced them, since the same source text can bind differently in another tree.
internal sealed class SourceReaderFileFactsStore(string? directory)
{
    internal const int RetainedPublications = RetainedGenerations;
    private readonly string? _root = directory is null ? null : Path.Combine(directory, "file-facts");

    internal string? RecordPath(string contentHash) =>
        _root is null ? null : Path.Combine(_root, "records", contentHash + ".json");

    internal string? PublicationPath(string key) =>
        _root is null ? null : Path.Combine(_root, "publications", key + ".json");

    internal SourceReaderFileFacts[]? FindBaseline(
        IReadOnlyList<(string Path, string ContentHash)> candidate,
        out Dictionary<SourceReaderFileFacts, string> loadedDigests)
    {
        loadedDigests = new(ReferenceEqualityComparer.Instance);
        if (candidate.Count == 0 || _root is null) return null;
        var hashes = new Dictionary<string, string>(candidate.Count, StringComparer.Ordinal);
        foreach (var (filePath, hash) in candidate)
            if (string.IsNullOrWhiteSpace(filePath) || !hashes.TryAdd(filePath, hash)) return null;

        try
        {
            var publications = ListPublications(new DirectoryInfo(Path.Combine(_root, "publications")))
                .Take(RetainedPublications).Select(ReadPublication).OfType<Publication>()
                .Select(publication => (publication.Entries,
                    Overlap: publication.Entries.Count(entry => hashes.GetValueOrDefault(entry.Path) == entry.ContentHash)))
                .Where(publication => publication.Overlap > 0 && (long)publication.Overlap * 2 >= candidate.Count)
                .OrderByDescending(publication => publication.Overlap);
            foreach (var publication in publications)
            {
                loadedDigests.Clear();
                foreach (var entry in publication.Entries)
                {
                    if (TryGet(entry.ContentHash, entry.Path, entry.Digest, out var facts))
                        loadedDigests.Add(facts, entry.Digest);
                    // Plan needs old facts for changed and removed files to seed invalidation.
                    // An unchanged file whose record is absent can enter Plan as an addition.
                    else if (hashes.GetValueOrDefault(entry.Path) != entry.ContentHash)
                    {
                        loadedDigests.Clear();
                        break;
                    }
                }
                var baseline = loadedDigests.Keys.ToArray();
                if (baseline.Length != 0 && SourceReaderFileFacts.Valid(baseline)) return baseline;
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException) { }

        loadedDigests.Clear();
        return null;
    }

    internal bool TryGet(string contentHash, string path, string expectedDigest, out SourceReaderFileFacts facts)
    {
        facts = null!;
        if (!IsHash(contentHash) || RecordPath(contentHash) is not { } recordPath) return false;
        try
        {
            var record = Read<FileRecord>(recordPath);
            if (record is null || record.FormatVersion != FormatVersion ||
                record.ContentHash != contentHash || record.Path != path || record.Digest != expectedDigest ||
                record.Digest != RecordDigest(contentHash, path, record.Facts)) return false;
            if (record.Facts.Deserialize<SourceReaderFileFacts>() is not { } stored ||
                stored.Path != path || stored.ContentHash != contentHash ||
                !SourceReaderFileFacts.WellFormed(stored)) return false;
            facts = stored;
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return false; }
    }

    internal void Publish(string key, SourceReaderFileFacts[] files,
        IReadOnlyDictionary<SourceReaderFileFacts, string> loadedDigests)
    {
        if (_root is null || !SourceReaderFileFacts.Valid(files)) return;

        var staged = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var root = Directory.CreateDirectory(_root);
            if ((root.Attributes & FileAttributes.ReparsePoint) != 0) return;
            var records = root.CreateSubdirectory("records");
            var publications = root.CreateSubdirectory("publications");
            if (((records.Attributes | publications.Attributes) & FileAttributes.ReparsePoint) != 0)
                return;

            // Multiple paths can share content. Publish just one record per hash and keep each
            // path's expected digest in the inventory, so lookups can detect a displaced record.
            var recordOwners = files.GroupBy(file => file.ContentHash, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
            var entries = new PublicationEntry[files.Length];
            for (var index = 0; index < files.Length; index++)
            {
                var file = files[index];
                if (!loadedDigests.TryGetValue(file, out var digest))
                {
                    var record = CreateRecord(file);
                    digest = record.Digest;
                    if (ReferenceEquals(file, recordOwners[file.ContentHash]))
                        staged.Add(file.ContentHash, Stage(RecordPath(file.ContentHash)!, record));
                }
                entries[index] = new(file.Path, file.ContentHash, digest);
            }

            using var lease = AcquirePublicationLease(root);
            var publicationPath = PublicationPath(key)!;
            string? published = null;
            try
            {
                foreach (var (hash, file) in recordOwners)
                {
                    var destination = RecordPath(hash)!;
                    if (staged.TryGetValue(hash, out var temporary))
                        File.Move(temporary, destination, overwrite: true);
                    else if (!File.Exists(destination))
                        Write(destination, CreateRecord(file));
                }
                Write(publicationPath, new Publication(FormatVersion, key, entries, PublicationDigest(key, entries)));
                published = publicationPath;
            }
            finally
            {
                Prune(records, publications, published);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException) { }
        finally
        {
            foreach (var temporary in staged.Values) Delete(temporary);
        }
    }

    private static FileRecord CreateRecord(SourceReaderFileFacts file)
    {
        var facts = JsonSerializer.SerializeToElement(file);
        return new(FormatVersion, file.ContentHash, file.Path, facts, RecordDigest(file.ContentHash, file.Path, facts));
    }

    private static string RecordDigest(string contentHash, string path, JsonElement facts) =>
        SourceReaderFileFacts.Hash(FormattableString.Invariant($"{FormatVersion}:{contentHash}:") +
            JsonSerializer.Serialize(path) + facts.GetRawText());

    private static string PublicationDigest(string key, PublicationEntry[] entries) =>
        SourceReaderFileFacts.Hash(key + JsonSerializer.Serialize(entries));

    private static bool IsHash(string? hash) =>
        hash is { Length: 64 } && hash.AsSpan().IndexOfAnyExcept("0123456789abcdef") < 0;

    private static IOrderedEnumerable<FileInfo> ListPublications(DirectoryInfo publications) =>
        NewestFirst(publications.EnumerateFiles("*.json").Where(IsOwnedGeneration),
            file => file.LastWriteTimeUtc, file => file.Name);

    private static Publication? ReadPublication(FileInfo file)
    {
        try
        {
            var publication = Read<Publication>(file.FullName);
            if (publication is null || publication.FormatVersion != FormatVersion ||
                publication.Key != Path.GetFileNameWithoutExtension(file.Name) || publication.Entries is null ||
                publication.Entries.Any(entry => entry is null || string.IsNullOrWhiteSpace(entry.Path) ||
                    !IsHash(entry.ContentHash) || !IsHash(entry.Digest)) ||
                publication.Entries.Select(entry => entry.Path).Distinct(StringComparer.Ordinal).Count() != publication.Entries.Length ||
                publication.Digest != PublicationDigest(publication.Key, publication.Entries)) return null;
            return publication;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return null; }
    }

    private static void Prune(DirectoryInfo records, DirectoryInfo publications, string? published)
    {
        var newest = ListPublications(publications).ToArray();
        var retainedHashes = new HashSet<string>(StringComparer.Ordinal);
        var retainedCount = 0;
        // Reserve a slot for the just-published inventory even when timestamps tie.
        foreach (var file in newest.OrderByDescending(file => file.FullName == published))
        {
            if (retainedCount < RetainedPublications && ReadPublication(file) is { } publication)
            {
                retainedCount++;
                retainedHashes.UnionWith(publication.Entries.Select(entry => entry.ContentHash));
            }
            else Delete(file.FullName);
        }
        foreach (var file in records.EnumerateFiles("*.json").Where(IsOwnedGeneration))
            if (!retainedHashes.Contains(Path.GetFileNameWithoutExtension(file.Name))) Delete(file.FullName);
    }

    private static T? Read<T>(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return JsonSerializer.Deserialize<T>(stream);
    }

    private static string Stage<T>(string destination, T payload)
    {
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var stream = File.Create(temporary);
            JsonSerializer.Serialize(stream, payload);
            return temporary;
        }
        catch
        {
            Delete(temporary);
            throw;
        }
    }

    private static void Write<T>(string destination, T payload)
    {
        var temporary = Stage(destination, payload);
        try { File.Move(temporary, destination, overwrite: true); }
        finally { Delete(temporary); }
    }

    private static void Delete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { /* Retry eviction on the next publication. */ }
        catch (UnauthorizedAccessException) { /* A locked entry must not prevent other evictions. */ }
    }

    private sealed record FileRecord(
        [property: JsonRequired] int FormatVersion,
        [property: JsonRequired] string ContentHash,
        [property: JsonRequired] string Path,
        [property: JsonRequired] JsonElement Facts,
        [property: JsonRequired] string Digest);

    private sealed record Publication(int FormatVersion, string Key, PublicationEntry[] Entries, string Digest);

    private sealed record PublicationEntry(string Path, string ContentHash, string Digest);
}

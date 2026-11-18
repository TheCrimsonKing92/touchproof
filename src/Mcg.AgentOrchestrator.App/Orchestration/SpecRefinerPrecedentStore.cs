using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record SpecRefinerPrecedent(
    string ForkKind,
    string Choice,
    string Rationale,
    DateTimeOffset RecordedAt,
    string? OriginItemId = null,
    string? OriginGoalId = null,
    string? OriginAnswerId = null,
    int? OriginBriefVersion = null)
{
    internal bool HasAnyOriginReference =>
        OriginItemId is not null ||
        OriginGoalId is not null ||
        OriginAnswerId is not null ||
        OriginBriefVersion is not null;

    internal bool HasCompleteOriginReference =>
        !string.IsNullOrWhiteSpace(OriginItemId) &&
        !string.IsNullOrWhiteSpace(OriginGoalId) &&
        !string.IsNullOrWhiteSpace(OriginAnswerId);
}

internal sealed class SpecRefinerPrecedentStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _filePath;

    public SpecRefinerPrecedentStore(string filePath)
    {
        _filePath = filePath;
    }

    public async Task<SpecRefinerPrecedent?> TryGetPrecedentAsync(
        string forkKind,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var all = await LoadAllAsync(cancellationToken);
            return all.FirstOrDefault(p =>
                string.Equals(p.ForkKind, forkKind, StringComparison.OrdinalIgnoreCase));
        }
        catch (SpecRefinerPrecedentStoreReadException)
        {
            return null;
        }
    }

    public async Task RecordPrecedentAsync(
        string forkKind,
        string choice,
        string rationale,
        CancellationToken cancellationToken = default,
        string? originItemId = null,
        string? originGoalId = null,
        string? originAnswerId = null,
        int? originBriefVersion = null)
    {
        var all = (await LoadAllAsync(cancellationToken)).ToList();
        all.RemoveAll(p => string.Equals(p.ForkKind, forkKind, StringComparison.OrdinalIgnoreCase));
        all.Add(new SpecRefinerPrecedent(
            forkKind,
            choice,
            rationale,
            DateTimeOffset.UtcNow,
            originItemId,
            originGoalId,
            originAnswerId,
            originBriefVersion));
        await SaveAllAsync(all, cancellationToken);
    }

    private async Task<IReadOnlyList<SpecRefinerPrecedent>> LoadAllAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var json = await File.ReadAllTextAsync(_filePath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(json))
                return [];

            var precedents = JsonSerializer.Deserialize<List<SpecRefinerPrecedent>>(json, SerializerOptions);
            if (precedents is null || precedents.Any(p => p is null))
                throw new JsonException("The precedent snapshot must contain a non-null array of entries.");
            return precedents;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FileNotFoundException)
        {
            return [];
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
        catch (Exception ex)
        {
            throw new SpecRefinerPrecedentStoreReadException(_filePath, ex);
        }
    }

    private async Task SaveAllAsync(
        IReadOnlyList<SpecRefinerPrecedent> precedents,
        CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(precedents, SerializerOptions);
        var tmp = _filePath + ".tmp";
        var bak = _filePath + ".bak";
        try
        {
            await File.WriteAllTextAsync(tmp, json, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(_filePath))
                File.Replace(tmp, _filePath, bak);
            else
                File.Move(tmp, _filePath);
        }
        catch
        {
            try
            {
                File.Delete(tmp);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            throw;
        }
    }
}

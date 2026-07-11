using System.Text.Json;
using System.Text.Json.Serialization;
using EmsScout.Application.Devices;

namespace EmsScout.Infrastructure.Importing;

public sealed class CollectionSnapshotReader
{
    private const long MaxSnapshotBytes = 512L * 1024 * 1024;
    private static readonly HashSet<string> ScopeModes = new(StringComparer.Ordinal)
    {
        "full", "building", "sub_area", "recapture", "append",
    };
    private static readonly HashSet<string> QualityDecisions = new(StringComparer.Ordinal)
    {
        "accepted", "accepted_with_findings", "rejected",
    };
    private static readonly HashSet<string> FindingSeverities = new(StringComparer.Ordinal)
    {
        "info", "warning", "error",
    };
    private static readonly HashSet<string> RetryOutcomes = new(StringComparer.Ordinal)
    {
        "recovered", "unchanged", "failed",
    };
    private static readonly HashSet<string> CommunicationStates = new(StringComparer.Ordinal)
    {
        "开机", "关机", "离线", "未知",
    };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = false,
        NumberHandling = JsonNumberHandling.Strict,
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public async Task<CollectionSnapshotReadResult> ReadAsync(
        string snapshotPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(snapshotPath))
        {
            throw new ArgumentException("Snapshot path is required.", nameof(snapshotPath));
        }

        var fullPath = Path.GetFullPath(snapshotPath);
        var info = new FileInfo(fullPath);
        if (!info.Exists) throw new FileNotFoundException("CollectionSnapshot file not found.", fullPath);
        if (info.Length > MaxSnapshotBytes)
        {
            throw new CollectionSnapshotContractException(
                $"CollectionSnapshot exceeds the {MaxSnapshotBytes} byte safety limit.");
        }

        var json = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        return Read(fullPath, json);
    }

    public CollectionSnapshotReadResult Read(string sourcePath, ReadOnlyMemory<byte> utf8Json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8Json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 128,
            });
        }
        catch (JsonException error)
        {
            throw new CollectionSnapshotContractException("CollectionSnapshot is not valid strict JSON.", error);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new CollectionSnapshotContractException("CollectionSnapshot root must be an object.");
            }
            EnsureNoDuplicateProperties(document.RootElement, "$", recursive: true);

            CollectionSnapshotV1 snapshot;
            try
            {
                snapshot = JsonSerializer.Deserialize<CollectionSnapshotV1>(document.RootElement, JsonOptions)
                           ?? throw new CollectionSnapshotContractException("CollectionSnapshot deserialized to null.");
            }
            catch (JsonException error)
            {
                throw new CollectionSnapshotContractException(
                    "CollectionSnapshot does not match the canonical v1 field shape: " + error.Message,
                    error);
            }

            var canonicalPayload = document.RootElement.TryGetProperty("buildings", out var buildingsElement)
                ? CollectionSnapshotCanonicalJson.SerializeBuildings(buildingsElement)
                : throw new CollectionSnapshotContractException("CollectionSnapshot is missing buildings.");
            var computedHash = CollectionSnapshotCanonicalJson.ComputeSha256(canonicalPayload);
            var verification = new SnapshotArtifactVerification(
                snapshot.Artifact.HashScope,
                snapshot.Artifact.Sha256,
                computedHash,
                snapshot.Artifact.Bytes,
                canonicalPayload.LongLength,
                snapshot.Artifact.HashScope == CollectionSnapshotContractV1.ArtifactHashScope &&
                snapshot.Artifact.Bytes == canonicalPayload.LongLength &&
                snapshot.Artifact.Sha256.Equals(computedHash, StringComparison.Ordinal));

            Validate(snapshot, verification);
            return new(Path.GetFullPath(sourcePath), snapshot, verification);
        }
    }

    private static void Validate(CollectionSnapshotV1 snapshot, SnapshotArtifactVerification artifact)
    {
        RequireEqual(snapshot.ContractVersion, CollectionSnapshotContractV1.Version, "contractVersion");
        RequireIdentifier(snapshot.WorkflowId, 128, allowColon: true, "workflowId");
        if (!artifact.IsValid)
        {
            throw new CollectionSnapshotContractException(
                $"CollectionSnapshot artifact mismatch: declared {artifact.DeclaredBytes} bytes/{artifact.DeclaredSha256}, " +
                $"computed {artifact.ComputedBytes} bytes/{artifact.ComputedSha256}.");
        }

        ValidateScope(snapshot.Scope);
        ValidateLineage(snapshot.Lineage, snapshot.Scope.Mode);
        ValidateVersions(snapshot.Versions);
        ValidateQuality(snapshot.Quality);
        ValidateNonNegative(snapshot.Counts.BuildingCount, "counts.buildingCount");
        ValidateNonNegative(snapshot.Counts.SubAreaCount, "counts.subAreaCount");
        ValidateNonNegative(snapshot.Counts.PageCount, "counts.pageCount");
        ValidateNonNegative(snapshot.Counts.RawCardCount, "counts.rawCardCount");
        ValidateNonNegative(snapshot.Counts.UniqueCardCount, "counts.uniqueCardCount");

        var sourceKeys = new HashSet<string>(StringComparer.Ordinal);
        var deviceUids = new HashSet<string>(StringComparer.Ordinal);
        var buildingNames = new HashSet<string>(StringComparer.Ordinal);
        var subAreaCount = 0;
        var pageCount = 0;
        var rawCardCount = 0;
        var uniqueCardCount = 0;

        foreach (var building in snapshot.Buildings)
        {
            RequireSourceKey(building.SourceKey, sourceKeys, "building.sourceKey");
            RequireNonEmpty(building.Building, "building.building");
            if (!buildingNames.Add(building.Building))
            {
                throw new CollectionSnapshotContractException($"Duplicate building '{building.Building}'.");
            }
            ValidateNullableNormalizedString(building.MenuClicked, "building.menuClicked");
            ValidateNonNegative(building.SubAreaCount, $"building[{building.Building}].subAreaCount");
            if (building.SubAreaCount != building.SubAreas.Count)
            {
                throw new CollectionSnapshotContractException(
                    $"Building {building.Building} subAreaCount={building.SubAreaCount}, records={building.SubAreas.Count}.");
            }

            subAreaCount += building.SubAreas.Count;
            foreach (var subArea in building.SubAreas)
            {
                RequireSourceKey(subArea.SourceKey, sourceKeys, "subArea.sourceKey");
                ValidateNonNegative(subArea.Idx, $"subArea[{subArea.SourceKey}].idx");
                ValidateFinite(subArea.Floor, $"subArea[{subArea.SourceKey}].floor");
                ValidateNullableNormalizedString(subArea.FloorLabel, $"subArea[{subArea.SourceKey}].floorLabel");
                RequireNonEmpty(subArea.Text, $"subArea[{subArea.SourceKey}].text");
                ValidateFinite(subArea.X, $"subArea[{subArea.SourceKey}].x");
                ValidateFinite(subArea.Y, $"subArea[{subArea.SourceKey}].y");
                ValidateSourceScalar(subArea.SourceEvidence.Err, $"subArea[{subArea.SourceKey}].sourceEvidence.err");

                pageCount += subArea.Pages.Count;
                foreach (var page in subArea.Pages)
                {
                    RequireSourceKey(page.SourceKey, sourceKeys, "page.sourceKey");
                    RequireNonEmpty(page.Page, $"page[{page.SourceKey}].page");
                    ValidateNonNegative(page.RawCount, $"page[{page.SourceKey}].rawCount");
                    ValidateNonNegative(page.UniqueCount, $"page[{page.SourceKey}].uniqueCount");
                    ValidateNullableNormalizedString(page.Layout, $"page[{page.SourceKey}].layout");
                    ValidateDecision(page.Quality.Decision, $"page[{page.SourceKey}].quality.decision");
                    RequireNonEmpty(page.Quality.Reason, $"page[{page.SourceKey}].quality.reason");
                    if (page.Quality.Attempts < 1)
                    {
                        throw new CollectionSnapshotContractException(
                            $"Page {page.SourceKey} quality attempts must be at least 1.");
                    }
                    ValidatePageEvidence(page);

                    if (page.UniqueCount != page.Cards.Count || page.RawCount < page.UniqueCount)
                    {
                        throw new CollectionSnapshotContractException(
                            $"Page {page.SourceKey} counts raw={page.RawCount}, unique={page.UniqueCount}, records={page.Cards.Count}.");
                    }
                    ValidateDuplicates(building, subArea, page);

                    rawCardCount += page.RawCount;
                    uniqueCardCount += page.Cards.Count;
                    foreach (var card in page.Cards)
                    {
                        var identity = new DeviceSourceIdentity(
                            building.Building,
                            subArea.Idx,
                            page.Page,
                            card.Name,
                            1);
                        var expectedSourceKey = DeviceIdentityKeyBuilder.BuildSourceKey(identity);
                        if (!card.SourceKey.Equals(expectedSourceKey, StringComparison.Ordinal))
                        {
                            throw new CollectionSnapshotContractException(
                                $"Card {card.Name} sourceKey mismatch: declared {card.SourceKey}, computed {expectedSourceKey}.");
                        }
                        ValidateCard(card, sourceKeys, deviceUids);
                    }
                }
            }
        }

        RequireCount(snapshot.Counts.BuildingCount, snapshot.Buildings.Count, "buildingCount");
        RequireCount(snapshot.Counts.SubAreaCount, subAreaCount, "subAreaCount");
        RequireCount(snapshot.Counts.PageCount, pageCount, "pageCount");
        RequireCount(snapshot.Counts.RawCardCount, rawCardCount, "rawCardCount");
        RequireCount(snapshot.Counts.UniqueCardCount, uniqueCardCount, "uniqueCardCount");

        var scopedBuildings = new HashSet<string>(snapshot.Scope.Buildings, StringComparer.Ordinal);
        if (!scopedBuildings.SetEquals(buildingNames))
        {
            throw new CollectionSnapshotContractException(
                "scope.buildings must exactly match the buildings payload: " +
                $"scope=[{string.Join(',', scopedBuildings)}], payload=[{string.Join(',', buildingNames)}].");
        }
        ValidateEvidenceReferences(snapshot, sourceKeys);
    }

    private static void ValidateScope(SnapshotScope scope)
    {
        if (!ScopeModes.Contains(scope.Mode))
        {
            throw new CollectionSnapshotContractException($"Unknown scope.mode '{scope.Mode}'.");
        }
        ValidateUniqueStrings(scope.Buildings, "scope.buildings");
        ValidateUniqueStrings(scope.Targets, "scope.targets");
        if (scope.Buildings.Count == 0)
        {
            throw new CollectionSnapshotContractException("scope.buildings cannot be empty.");
        }
        if (scope.Mode is "full" or "building" or "append" && scope.Targets.Count != 0)
        {
            throw new CollectionSnapshotContractException($"scope.targets must be empty for {scope.Mode} scope.");
        }
        if (scope.Mode is "sub_area" or "recapture" && scope.Targets.Count == 0)
        {
            throw new CollectionSnapshotContractException($"scope.targets is required for {scope.Mode} scope.");
        }
    }

    private static void ValidateLineage(SnapshotLineage lineage, string scopeMode)
    {
        ValidateNullableHash(lineage.BaseArtifactSha256, "lineage.baseArtifactSha256");
        if (lineage.ParentWorkflowId is not null)
        {
            RequireIdentifier(lineage.ParentWorkflowId, 128, allowColon: true, "lineage.parentWorkflowId");
        }
        if ((lineage.BaseArtifactSha256 is null) != (lineage.ParentWorkflowId is null))
        {
            throw new CollectionSnapshotContractException(
                "lineage.baseArtifactSha256 and lineage.parentWorkflowId must both be null or both be set.");
        }
        if (scopeMode is "append" or "recapture" && lineage.BaseArtifactSha256 is null)
        {
            throw new CollectionSnapshotContractException($"{scopeMode} scope requires lineage metadata.");
        }
    }

    private static void ValidateVersions(SnapshotVersions versions)
    {
        RequireNonEmpty(versions.Collector, "versions.collector");
        RequireNonEmpty(versions.Playwright, "versions.playwright");
        RequireNonEmpty(versions.Rules, "versions.rules");
        RequireNonEmpty(versions.DatabaseSchema, "versions.databaseSchema");
        RequireNonEmpty(versions.SourceRevision, "versions.sourceRevision");
    }

    private static void ValidateQuality(SnapshotQuality quality)
    {
        ValidateDecision(quality.Decision, "quality.decision");
        foreach (var finding in quality.Findings)
        {
            RequireNonEmpty(finding.Code, "quality.findings.code");
            if (!FindingSeverities.Contains(finding.Severity))
            {
                throw new CollectionSnapshotContractException($"Unknown finding severity '{finding.Severity}'.");
            }
            RequireNonEmpty(finding.Message, "quality.findings.message");
            ValidateNullableNormalizedString(finding.SourceKey, "quality.findings.sourceKey");
        }
        foreach (var retry in quality.Retries)
        {
            RequireNonEmpty(retry.SourceKey, "quality.retries.sourceKey");
            if (retry.Attempt < 1) throw new CollectionSnapshotContractException("Retry attempt must be at least 1.");
            RequireNonEmpty(retry.Reason, "quality.retries.reason");
            if (retry.CompletedAt < retry.StartedAt)
            {
                throw new CollectionSnapshotContractException("Retry completedAt cannot precede startedAt.");
            }
            if (!RetryOutcomes.Contains(retry.Outcome))
            {
                throw new CollectionSnapshotContractException($"Unknown retry outcome '{retry.Outcome}'.");
            }
        }
    }

    private static void ValidatePageEvidence(SnapshotPage page)
    {
        var path = $"page[{page.SourceKey}].sourceEvidence";
        ValidateSourceScalar(page.SourceEvidence.Count, path + ".count");
        ValidateSourceScalar(page.SourceEvidence.OnHref, path + ".onHref");
        ValidateSourceScalar(page.SourceEvidence.OffHref, path + ".offHref");
        ValidateSourceScalar(page.SourceEvidence.QualityReason, path + ".qualityReason");
        ValidateSourceScalar(page.SourceEvidence.Err, path + ".err");
    }

    private static void ValidateDuplicates(
        SnapshotBuilding building,
        SnapshotSubArea subArea,
        SnapshotPage page)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var repeatedCardName = page.Cards
            .GroupBy(card => card.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (repeatedCardName is not null)
        {
            throw new CollectionSnapshotContractException(
                $"Page {page.SourceKey} contains more than one unique card record named {repeatedCardName}; " +
                "extra observations belong in duplicates evidence.");
        }
        var cardsByName = page.Cards.ToDictionary(card => card.Name, StringComparer.Ordinal);
        var duplicateExtras = 0;
        foreach (var duplicate in page.Duplicates)
        {
            RequireNonEmpty(duplicate.Name, $"page[{page.SourceKey}].duplicates.name");
            if (!names.Add(duplicate.Name))
            {
                throw new CollectionSnapshotContractException(
                    $"Page {page.SourceKey} repeats duplicate evidence for {duplicate.Name}.");
            }
            if (!cardsByName.TryGetValue(duplicate.Name, out var card))
            {
                throw new CollectionSnapshotContractException(
                    $"Page {page.SourceKey} duplicate evidence references absent card {duplicate.Name}.");
            }
            var expectedSourceKeys = Enumerable.Range(1, duplicate.Copies)
                .Select(occurrence => DeviceIdentityKeyBuilder.BuildSourceKey(new DeviceSourceIdentity(
                    building.Building,
                    subArea.Idx,
                    page.Page,
                    card.Name,
                    occurrence)))
                .ToHashSet(StringComparer.Ordinal);
            if (duplicate.Copies < 2 ||
                duplicate.SourceKeys.Count != duplicate.Copies ||
                duplicate.SourceKeys.Distinct(StringComparer.Ordinal).Count() != duplicate.SourceKeys.Count ||
                !new HashSet<string>(duplicate.SourceKeys, StringComparer.Ordinal)
                    .SetEquals(expectedSourceKeys))
            {
                throw new CollectionSnapshotContractException(
                    $"Page {page.SourceKey} duplicate evidence for {duplicate.Name} is inconsistent.");
            }
            duplicateExtras += duplicate.Copies - 1;
        }
        if (page.RawCount - page.UniqueCount != duplicateExtras)
        {
            throw new CollectionSnapshotContractException(
                $"Page {page.SourceKey} duplicate evidence accounts for {duplicateExtras} extras, " +
                $"but raw-unique={page.RawCount - page.UniqueCount}.");
        }
    }

    private static void ValidateCard(
        SnapshotCard card,
        HashSet<string> sourceKeys,
        HashSet<string> deviceUids)
    {
        RequireSourceKey(card.SourceKey, sourceKeys, "card.sourceKey");
        if (card.DeviceUid is not null)
        {
            if (!DeviceIdentityKeyBuilder.IsDeviceUid(card.DeviceUid))
            {
                throw new CollectionSnapshotContractException(
                    $"Card {card.SourceKey} has invalid v1 deviceUid '{card.DeviceUid}'.");
            }
            if (!deviceUids.Add(card.DeviceUid))
            {
                throw new CollectionSnapshotContractException(
                    $"deviceUid '{card.DeviceUid}' is assigned to more than one unique device.");
            }
        }
        RequireNonEmpty(card.Name, $"card[{card.SourceKey}].name");
        if (card.Switch is not null && card.Switch is not ("ON" or "OFF"))
        {
            throw new CollectionSnapshotContractException($"Card {card.SourceKey} has invalid switch '{card.Switch}'.");
        }
        ValidateNullableNormalizedString(card.Mode, $"card[{card.SourceKey}].mode", rejectZero: true);
        ValidateFinite(card.Indoor, $"card[{card.SourceKey}].indoor");
        ValidateFinite(card.SetTemp, $"card[{card.SourceKey}].setTemp");
        ValidateNullableNormalizedString(card.Fan, $"card[{card.SourceKey}].fan", rejectZero: true);
        ValidateNullableNormalizedString(card.Indicator, $"card[{card.SourceKey}].indicator");
        if (!CommunicationStates.Contains(card.Comm))
        {
            throw new CollectionSnapshotContractException($"Card {card.SourceKey} has invalid comm '{card.Comm}'.");
        }

        var raw = card.SourceEvidence.Raw;
        ValidateSourceScalar(raw.Name, $"card[{card.SourceKey}].sourceEvidence.raw.name");
        ValidateSourceScalar(raw.Switch, $"card[{card.SourceKey}].sourceEvidence.raw.switch");
        ValidateSourceScalar(raw.Mode, $"card[{card.SourceKey}].sourceEvidence.raw.mode");
        ValidateSourceScalar(raw.Indoor, $"card[{card.SourceKey}].sourceEvidence.raw.indoor");
        ValidateSourceScalar(raw.SetTemp, $"card[{card.SourceKey}].sourceEvidence.raw.setTemp");
        ValidateSourceScalar(raw.Fan, $"card[{card.SourceKey}].sourceEvidence.raw.fan");
        ValidateSourceScalar(raw.Indicator, $"card[{card.SourceKey}].sourceEvidence.raw.indicator");
        ValidateSourceScalar(raw.Comm, $"card[{card.SourceKey}].sourceEvidence.raw.comm");
    }

    private static void ValidateEvidenceReferences(CollectionSnapshotV1 snapshot, HashSet<string> sourceKeys)
    {
        foreach (var finding in snapshot.Quality.Findings.Where(finding => finding.SourceKey is not null))
        {
            if (!sourceKeys.Contains(finding.SourceKey!))
            {
                throw new CollectionSnapshotContractException(
                    $"Finding {finding.Code} references unknown sourceKey {finding.SourceKey}.");
            }
        }
        foreach (var retry in snapshot.Quality.Retries)
        {
            if (!sourceKeys.Contains(retry.SourceKey))
            {
                throw new CollectionSnapshotContractException(
                    $"Retry attempt references unknown sourceKey {retry.SourceKey}.");
            }
        }
    }

    private static void ValidateSourceScalar(JsonElement value, string path)
    {
        if (value.ValueKind is not (
            JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or
            JsonValueKind.False or JsonValueKind.Null))
        {
            throw new CollectionSnapshotContractException($"{path} must be a source scalar or null.");
        }
    }

    private static void RequireSourceKey(string value, HashSet<string> sourceKeys, string path)
    {
        RequireNonEmpty(value, path);
        if (!sourceKeys.Add(value))
        {
            throw new CollectionSnapshotContractException(
                $"sourceKey must be unique across all entity levels; duplicate '{value}'.");
        }
    }

    private static void ValidateDecision(string value, string path)
    {
        if (!QualityDecisions.Contains(value))
        {
            throw new CollectionSnapshotContractException($"Unknown {path} '{value}'.");
        }
    }

    private static void ValidateUniqueStrings(IEnumerable<string> values, string path)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            RequireNonEmpty(value, path);
            if (!seen.Add(value)) throw new CollectionSnapshotContractException($"{path} contains duplicate '{value}'.");
        }
    }

    private static void ValidateNullableNormalizedString(string? value, string path, bool rejectZero = false)
    {
        if (value is null) return;
        RequireNonEmpty(value, path);
        if (value == "-" || rejectZero && value == "0")
        {
            throw new CollectionSnapshotContractException($"{path} uses legacy missing-value sentinel '{value}'; use null.");
        }
    }

    private static void ValidateNullableHash(string? value, string path)
    {
        if (value is null) return;
        if (value.Length != 64 || value.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new CollectionSnapshotContractException($"{path} must be a lowercase SHA-256 value.");
        }
    }

    private static void RequireIdentifier(string value, int maxLength, bool allowColon, string path)
    {
        RequireNonEmpty(value, path);
        if (value.Length > maxLength || !IsAsciiAlphaNumeric(value[0]) || value.Any(character =>
                !IsAsciiAlphaNumeric(character) && character is not ('.' or '_' or '-') &&
                !(allowColon && character == ':')))
        {
            throw new CollectionSnapshotContractException($"{path} is not a valid contract identifier.");
        }
    }

    private static bool IsAsciiAlphaNumeric(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';

    private static void RequireNonEmpty(string value, string path)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CollectionSnapshotContractException($"{path} must be a non-empty string.");
        }
    }

    private static void RequireEqual(string actual, string expected, string path)
    {
        if (!actual.Equals(expected, StringComparison.Ordinal))
        {
            throw new CollectionSnapshotContractException($"Unsupported {path} '{actual}'; expected '{expected}'.");
        }
    }

    private static void ValidateNonNegative(int value, string path)
    {
        if (value < 0) throw new CollectionSnapshotContractException($"{path} cannot be negative.");
    }

    private static void ValidateFinite(double? value, string path)
    {
        if (value.HasValue && !double.IsFinite(value.Value))
        {
            throw new CollectionSnapshotContractException($"{path} must be finite or null.");
        }
    }

    private static void RequireCount(int declared, int actual, string name)
    {
        if (declared != actual)
        {
            throw new CollectionSnapshotContractException(
                $"CollectionSnapshot counts.{name}={declared}, computed={actual}.");
        }
    }

    private static void EnsureNoDuplicateProperties(JsonElement element, string path, bool recursive)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new CollectionSnapshotContractException($"Duplicate JSON property '{path}.{property.Name}'.");
                }
                if (recursive) EnsureNoDuplicateProperties(property.Value, $"{path}.{property.Name}", true);
            }
        }
        else if (recursive && element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                EnsureNoDuplicateProperties(item, $"{path}[{index++}]", true);
            }
        }
    }
}

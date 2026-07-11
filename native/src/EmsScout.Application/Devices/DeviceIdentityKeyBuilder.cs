using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace EmsScout.Application.Devices;

public readonly record struct DeviceSourceIdentity(
    string Building,
    long SubAreaIndex,
    string PageName,
    string DeviceName,
    int Occurrence = 1);

public static class DeviceIdentityKeyBuilder
{
    public const string SourceKeyPrefix = "sk1_";
    public const string DeviceUidPrefix = "duid1_";

    public static string BuildSourceKey(DeviceSourceIdentity identity)
    {
        var building = NormalizeRequired(identity.Building, nameof(identity.Building));
        var pageName = NormalizeRequired(identity.PageName, nameof(identity.PageName));
        var deviceName = NormalizeRequired(identity.DeviceName, nameof(identity.DeviceName));
        var subAreaIndex = identity.SubAreaIndex.ToString(CultureInfo.InvariantCulture);
        if (identity.Occurrence < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(identity.Occurrence), "Occurrence must be at least 1.");
        }

        var components = identity.Occurrence == 1
            ? new[] { building, subAreaIndex, pageName, deviceName }
            : [building, subAreaIndex, pageName, deviceName, identity.Occurrence.ToString(CultureInfo.InvariantCulture)];
        return SourceKeyPrefix + HashCanonical("ems.source-key/v1;", components);
    }

    public static string CreateInitialDeviceUid(DeviceSourceIdentity identity) =>
        CreateInitialDeviceUid(BuildSourceKey(identity with { Occurrence = 1 }));

    public static string CreateInitialDeviceUid(string sourceKey)
    {
        var normalized = (sourceKey ?? string.Empty).Trim().ToLowerInvariant();
        if (!IsSourceKey(normalized))
        {
            throw new ArgumentException("A valid v1 source key is required.", nameof(sourceKey));
        }

        return DeviceUidPrefix + HashCanonical("ems.device-uid/v1;", normalized);
    }

    public static bool IsSourceKey(string? value) =>
        HasHexPayload(value, SourceKeyPrefix);

    public static bool IsDeviceUid(string? value) =>
        HasHexPayload(value, DeviceUidPrefix);

    public static string NormalizeIdentityText(string? value) =>
        (value ?? string.Empty).Trim().Normalize(NormalizationForm.FormC).ToUpperInvariant();

    private static string NormalizeRequired(string? value, string parameterName)
    {
        var normalized = NormalizeIdentityText(value);
        return string.IsNullOrWhiteSpace(normalized)
            ? throw new ArgumentException("Identity components cannot be blank.", parameterName)
            : normalized;
    }

    private static string HashCanonical(string domain, params string[] components)
    {
        var canonical = new StringBuilder(domain);
        foreach (var component in components)
        {
            var byteCount = Encoding.UTF8.GetByteCount(component);
            canonical.Append(byteCount.ToString(CultureInfo.InvariantCulture));
            canonical.Append(':');
            canonical.Append(component);
            canonical.Append(';');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool HasHexPayload(string? value, string prefix)
    {
        if (value is null || value.Length != prefix.Length + 64 ||
            !value.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        return value.AsSpan(prefix.Length).ToString().All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }
}

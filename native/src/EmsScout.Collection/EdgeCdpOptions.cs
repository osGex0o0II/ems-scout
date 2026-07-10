namespace EmsScout.Collection;

public sealed record EdgeCdpOptions(
    Uri Endpoint,
    string UserDataDirectory,
    TimeSpan ConnectionTimeout);

using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;

namespace EmsScout.Infrastructure.Sqlite;

/// <summary>
/// Observes commits made by other SQLite connections and database-file replacement.
/// Instances are safe for concurrent callers and retain one read-only SQLite connection.
/// </summary>
public sealed class DeviceDataRevisionMonitor : IDisposable
{
    private readonly object _gate = new();
    private SqliteConnection? _connection;
    private string? _databasePath;
    private DatabaseFileIdentity _fileIdentity;
    private long _dataVersion;
    private long _revision;
    private bool _disposed;

    public long GetRevision(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.GetFullPath(databasePath);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try
            {
                var identity = DatabaseFileIdentity.Read(fullPath);
                if (_connection is null ||
                    !string.Equals(_databasePath, fullPath, StringComparison.OrdinalIgnoreCase) ||
                    !_fileIdentity.Equals(identity))
                {
                    OpenConnection(fullPath, identity);
                    return NextRevision();
                }

                var dataVersion = ReadDataVersion(_connection);
                if (dataVersion != _dataVersion)
                {
                    _dataVersion = dataVersion;
                    return NextRevision();
                }

                return _revision;
            }
            catch
            {
                ResetConnection();
                throw;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ResetConnection();
        }
    }

    private void OpenConnection(string fullPath, DatabaseFileIdentity identity)
    {
        ResetConnection();
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Cannot find EMS SQLite database.", fullPath);
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        try
        {
            _dataVersion = ReadDataVersion(connection);
            _connection = connection;
            _databasePath = fullPath;
            _fileIdentity = identity;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private long NextRevision()
    {
        if (_revision == long.MaxValue)
        {
            throw new InvalidOperationException("Device data revision counter exhausted.");
        }

        return ++_revision;
    }

    private static long ReadDataVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA data_version";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private void ResetConnection()
    {
        _connection?.Dispose();
        _connection = null;
        _databasePath = null;
        _fileIdentity = default;
        _dataVersion = 0;
    }

    private readonly record struct DatabaseFileIdentity(
        ulong Volume,
        ulong File,
        long Length,
        long CreationTimeUtcTicks,
        long LastWriteTimeUtcTicks,
        long ChangeTimeUtcTicks)
    {
        public static DatabaseFileIdentity Read(string path)
        {
            if (!System.IO.File.Exists(path))
            {
                throw new FileNotFoundException("Cannot find EMS SQLite database.", path);
            }

            if (OperatingSystem.IsWindows())
            {
                using SafeFileHandle handle = System.IO.File.OpenHandle(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                if (!GetFileInformationByHandle(handle, out var info))
                {
                    throw new IOException(
                        $"Cannot read database file identity: {path}",
                        Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
                }

                if (!GetFileInformationByHandleEx(
                        handle,
                        FileBasicInfo,
                        out var basicInfo,
                        (uint)Marshal.SizeOf<NativeFileBasicInformation>()))
                {
                    throw new IOException(
                        $"Cannot read database file change time: {path}",
                        Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
                }

                return new DatabaseFileIdentity(
                    info.VolumeSerialNumber,
                    ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow,
                    ((long)info.FileSizeHigh << 32) | info.FileSizeLow,
                    FileTimeToTicks(info.CreationTime),
                    FileTimeToTicks(info.LastWriteTime),
                    basicInfo.ChangeTime);
            }

            var file = new FileInfo(path);
            return new DatabaseFileIdentity(
                Volume: 0,
                File: 0,
                file.Length,
                file.CreationTimeUtc.Ticks,
                file.LastWriteTimeUtc.Ticks,
                ChangeTimeUtcTicks: 0);
        }

        private static long FileTimeToTicks(NativeFileTime value) =>
            ((long)value.HighDateTime << 32) | value.LowDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    private const int FileBasicInfo = 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int fileInformationClass,
        out NativeFileBasicInformation fileInformation,
        uint bufferSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public NativeFileTime CreationTime;
        public NativeFileTime LastAccessTime;
        public NativeFileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileBasicInformation
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }
}

using Invest.Sign.Router.Core.Abstractions;

namespace Invest.Sign.Router.Infrastructure.Files;

/// <summary>Settings of <see cref="FileSystemSignedFileSource"/>.</summary>
public sealed class SignedFilesOptions
{
    public const string SectionName = "SignedFiles";

    /// <summary>
    /// Absolute path of the share root as seen by the service (e.g. a CSI mount in Kubernetes, open question 8).
    /// Its existence is not checked at startup: an unavailable share is a technical failure retried per document.
    /// </summary>
    public string Root { get; set; } = "";

    /// <summary>Max time to read one file. A hung share read must not block the engine.</summary>
    public TimeSpan ReadTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Root) || !Path.IsPathFullyQualified(Root))
        {
            errors.Add($"{nameof(Root)} must be an absolute path, got '{Root}'.");
        }

        if (ReadTimeout <= TimeSpan.Zero)
        {
            errors.Add($"{nameof(ReadTimeout)} must be positive.");
        }

        return errors;
    }
}

/// <summary>
/// Reads signed files from a directory tree: <c>file_ref</c> is a path relative to <see cref="SignedFilesOptions.Root"/>.
/// Paths resolving outside the root are refused. Both <c>/</c> and <c>\</c> are accepted as separators.
/// </summary>
/// <remarks>
/// The file is opened with <see cref="FileShare.Read"/>: a file still held for writing fails with
/// <see cref="IOException"/>, which the engine retries. Symbolic links inside the root are followed as is.
/// </remarks>
public sealed class FileSystemSignedFileSource : ISignedFileSource
{
    private const int BufferSize = 81920;

    private readonly string _root;
    private readonly TimeSpan _readTimeout;
    private readonly Func<string, CancellationToken, Task<byte[]>> _read;

    public FileSystemSignedFileSource(SignedFilesOptions options)
        : this(options, ReadFileAsync)
    {
    }

    /// <summary>Test seam: replaces the actual read to simulate a hanging share.</summary>
    internal FileSystemSignedFileSource(SignedFilesOptions options, Func<string, CancellationToken, Task<byte[]>> read)
    {
        var errors = options.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException($"Invalid signed files options: {string.Join(" ", errors)}", nameof(options));
        }

        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Root));
        _readTimeout = options.ReadTimeout;
        _read = read;
    }

    public async Task<byte[]> ReadAsync(string fileRef, CancellationToken cancellationToken)
    {
        var path = Resolve(fileRef);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_readTimeout);
        try
        {
            // WaitAsync also bounds reads that ignore cancellation (e.g. a stuck network file system call);
            // such a read is abandoned and its thread is released when the call eventually returns.
            return await _read(path, timeout.Token).WaitAsync(_readTimeout, cancellationToken);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Reading signed file '{fileRef}' did not finish within {_readTimeout}.", ex);
        }
    }

    /// <summary>Maps <paramref name="fileRef"/> to a full path under the root.</summary>
    /// <exception cref="InvalidFileRefException">The reference is empty, absolute or points outside the root.</exception>
    internal string Resolve(string fileRef)
    {
        if (string.IsNullOrWhiteSpace(fileRef))
        {
            throw new InvalidFileRefException(fileRef, "the reference is empty");
        }

        var relative = fileRef.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(relative))
        {
            throw new InvalidFileRefException(fileRef, "the reference must be relative to the share root");
        }

        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(_root, relative));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidFileRefException(fileRef, ex.Message);
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, comparison))
        {
            throw new InvalidFileRefException(fileRef, "the reference points outside the share root");
        }

        return full;
    }

    private static async Task<byte[]> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var buffer = new MemoryStream(stream.Length > 0 && stream.Length <= int.MaxValue ? (int)stream.Length : 0);
        await stream.CopyToAsync(buffer, BufferSize, cancellationToken);
        return buffer.ToArray();
    }
}

/// <summary><c>file_ref</c> cannot be mapped to a file under the share root.</summary>
public sealed class InvalidFileRefException(string? fileRef, string reason)
    : Exception($"Invalid signed file reference '{fileRef}': {reason}.")
{
    public string? FileRef { get; } = fileRef;
}

using Invest.Sign.Router.Core.Abstractions;

namespace Invest.Sign.Router.Infrastructure.Files;

/// <summary>Настройки <see cref="FileSystemSignedFileSource"/>.</summary>
public sealed class SignedFilesOptions
{
    public const string SectionName = "SignedFiles";

    /// <summary>
    /// Абсолютный путь корня общего ресурса с точки зрения сервиса (например, CSI-монтирование в Kubernetes, открытый вопрос 8).
    /// Его существование при запуске не проверяется: недоступный ресурс — техническая ошибка, повторяемая для каждого документа.
    /// </summary>
    public string Root { get; set; } = "";

    /// <summary>Максимальное время чтения одного файла. Зависшее чтение с общего ресурса не должно блокировать движок.</summary>
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
/// Читает подписанные файлы из дерева каталогов: <c>file_ref</c> — путь относительно <see cref="SignedFilesOptions.Root"/>.
/// Пути, выходящие за пределы корня, отклоняются. В качестве разделителей принимаются и <c>/</c>, и <c>\</c>.
/// </summary>
/// <remarks>
/// Файл открывается с <see cref="FileShare.Read"/>: файл, ещё открытый на запись, приводит к
/// <see cref="IOException"/>, которое движок повторяет. Символические ссылки внутри корня обрабатываются как есть.
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

    /// <summary>Точка подмены для тестов: заменяет реальное чтение, чтобы имитировать зависший общий ресурс.</summary>
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
            // WaitAsync ограничивает и чтения, игнорирующие отмену (например, зависший вызов сетевой файловой системы);
            // такое чтение бросается, а его поток освобождается, когда вызов в конце концов вернётся.
            return await _read(path, timeout.Token).WaitAsync(_readTimeout, cancellationToken);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Reading signed file '{fileRef}' did not finish within {_readTimeout}.", ex);
        }
    }

    /// <summary>Отображает <paramref name="fileRef"/> в полный путь внутри корня.</summary>
    /// <exception cref="InvalidFileRefException">Ссылка пустая, абсолютная или указывает за пределы корня.</exception>
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

/// <summary><c>file_ref</c> не удаётся отобразить в файл внутри корня общего ресурса.</summary>
public sealed class InvalidFileRefException(string? fileRef, string reason)
    : Exception($"Invalid signed file reference '{fileRef}': {reason}.")
{
    public string? FileRef { get; } = fileRef;
}

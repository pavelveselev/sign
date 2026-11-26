using System.Text.Json;
using Invest.Sign.Router.Core.Abstractions;
using Invest.Sign.Router.Core.Model;
using Invest.Sign.Router.Infrastructure.Files;

namespace Invest.Sign.Router.Infrastructure.Stages;

/// <summary>Настройки <see cref="CopyFileStageHandler"/>: корни, между которыми копируются файлы.</summary>
public sealed class CopyFileOptions
{
    public const string SectionName = "CopyFile";

    /// <summary>Абсолютный путь корня, относительно которого задаётся исходный файл документа.</summary>
    public string SourceRoot { get; set; } = "";

    /// <summary>Абсолютный путь корня, в каталоги которого копируются файлы.</summary>
    public string TargetRoot { get; set; } = "";

    /// <summary>Максимальное время копирования одного файла. Должно быть меньше <c>Engine:LeaseDuration</c>.</summary>
    public TimeSpan CopyTimeout { get; set; } = TimeSpan.FromMinutes(1);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(SourceRoot) || !Path.IsPathFullyQualified(SourceRoot))
        {
            errors.Add($"{nameof(SourceRoot)} must be an absolute path, got '{SourceRoot}'.");
        }

        if (string.IsNullOrWhiteSpace(TargetRoot) || !Path.IsPathFullyQualified(TargetRoot))
        {
            errors.Add($"{nameof(TargetRoot)} must be an absolute path, got '{TargetRoot}'.");
        }

        if (CopyTimeout <= TimeSpan.Zero)
        {
            errors.Add($"{nameof(CopyTimeout)} must be positive.");
        }

        return errors;
    }
}

/// <summary>Настройки этапа копирования файла.</summary>
/// <param name="TargetFolder">Каталог назначения относительно <see cref="CopyFileOptions.TargetRoot"/>.</param>
/// <param name="SourceAttribute">
/// Атрибут документа с путём исходного файла относительно <see cref="CopyFileOptions.SourceRoot"/>.
/// </param>
/// <param name="FileName">
/// Имя файла в каталоге назначения. Подстановки: <c>{docId}</c>, <c>{requestId}</c>, <c>{fileName}</c> (имя исходного файла).
/// По умолчанию к имени добавляется идентификатор документа, чтобы файлы разных документов не перезаписывали друг друга.
/// </param>
public sealed record CopyFileStageSettings(
    string TargetFolder,
    string SourceAttribute = "file_ref",
    string FileName = "{docId}_{fileName}");

/// <summary>
/// Этап, который копирует файл документа в каталог назначения и сразу завершается, например, чтобы передать файл
/// на ручную обработку. В данные этапа записываются исходный и итоговый пути.
/// </summary>
/// <remarks>
/// Копирование идемпотентно: файл пишется во временный файл рядом с целевым и атомарно переименовывается с перезаписью,
/// поэтому повтор шага после сбоя даёт тот же результат, а читатель каталога никогда не видит недописанный файл.
/// Отсутствующий атрибут и путь за пределами корня — бизнес-ошибки (<see cref="StageOutcome.Failed"/>);
/// недоступный ресурс или отсутствующий исходный файл — технические ошибки, которые движок повторяет.
/// </remarks>
public sealed class CopyFileStageHandler : IStageHandler
{
    private const int BufferSize = 81920;

    private readonly string _sourceRoot;
    private readonly string _targetRoot;
    private readonly TimeSpan _timeout;

    public CopyFileStageHandler(CopyFileOptions options)
    {
        var errors = options.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException($"Invalid copy file options: {string.Join(" ", errors)}", nameof(options));
        }

        _sourceRoot = SharePath.NormalizeRoot(options.SourceRoot);
        _targetRoot = SharePath.NormalizeRoot(options.TargetRoot);
        _timeout = options.CopyTimeout;
    }

    public async Task<StageOutcome> StartAsync(StageContext stage, CancellationToken cancellationToken)
    {
        var settings = stage.GetSettings<CopyFileStageSettings>();

        string source;
        string target;
        try
        {
            source = SharePath.Resolve(_sourceRoot, stage.Document.GetString(settings.SourceAttribute));
            var fileName = settings.FileName
                .Replace("{docId}", stage.Document.ExternalDocId, StringComparison.Ordinal)
                .Replace("{requestId}", stage.RequestId, StringComparison.Ordinal)
                .Replace("{fileName}", Path.GetFileName(source), StringComparison.Ordinal);
            target = SharePath.Resolve(_targetRoot, Path.Combine(settings.TargetFolder, fileName));
        }
        catch (Exception ex) when (ex is DocumentAttributeException or InvalidFileRefException)
        {
            return new StageOutcome.Failed(ex.Message);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            await CopyAsync(source, target, stage.RequestId, timeout.Token).WaitAsync(_timeout, cancellationToken);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Copying '{source}' to '{target}' did not finish within {_timeout}.", ex);
        }

        var data = JsonSerializer.Serialize(new
        {
            source = SharePath.ToRelative(_sourceRoot, source),
            target = SharePath.ToRelative(_targetRoot, target),
        });
        return new StageOutcome.Done(Data: data);
    }

    /// <summary>Этап завершается в <see cref="StartAsync"/> и в <c>REQUESTED</c> не бывает.</summary>
    public Task<StageOutcome> CheckAsync(StageContext stage, CancellationToken cancellationToken) =>
        throw new InvalidOperationException($"Copy file stage '{stage.StageKey}' completes on start and is never polled.");

    public IReadOnlyList<string> ValidateSettings(object? settings)
    {
        if (settings is not CopyFileStageSettings s)
        {
            return [$"expected {nameof(CopyFileStageSettings)}, got {settings?.GetType().Name ?? "null"}"];
        }

        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(s.SourceAttribute))
        {
            errors.Add("source attribute must not be empty");
        }

        if (string.IsNullOrWhiteSpace(s.FileName))
        {
            errors.Add("file name must not be empty");
        }

        try
        {
            SharePath.Resolve(_targetRoot, Path.Combine(s.TargetFolder ?? "", "probe"));
        }
        catch (InvalidFileRefException)
        {
            errors.Add($"target folder '{s.TargetFolder}' must be a relative path inside the target root");
        }

        return errors;
    }

    private static async Task CopyAsync(string source, string target, string requestId, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = $"{target}.{requestId}.tmp";
        try
        {
            await using (var input = new FileStream(
                source, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(
                temp, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous))
            {
                await input.CopyToAsync(output, BufferSize, cancellationToken);
            }

            File.Move(temp, target, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

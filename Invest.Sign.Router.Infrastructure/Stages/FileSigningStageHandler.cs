using System.Text.Json;
using Invest.Sign.Router.Core.Abstractions;
using Invest.Sign.Router.Core.Model;

namespace Invest.Sign.Router.Infrastructure.Stages;

/// <summary>Каталоги, по которым проходит файл документа при подписании через каталоги.</summary>
public sealed class FileSigningOptions
{
    public const string SectionName = "FileSigning";

    /// <summary>Откуда берётся файл документа по имени из вьюхи входящих документов.</summary>
    public string SourceDirectory { get; set; } = "";

    /// <summary>Куда файл перемещается на подписание; этот каталог читает ПО подписания.</summary>
    public string SigningDirectory { get; set; } = "";

    /// <summary>Куда ПО подписания кладёт подписанный файл и файл подписи.</summary>
    public string SignedDirectory { get; set; } = "";

    /// <summary>Куда сервис перемещает подписанный файл и подпись по завершении.</summary>
    public string FinalDirectory { get; set; } = "";

    /// <summary>Суффикс файла подписи: подпись файла <c>doc.pdf</c> — <c>doc.pdf.sig</c>.</summary>
    public string SignatureSuffix { get; set; } = ".sig";

    /// <summary>Максимальное время одной файловой операции. Должно быть меньше <c>Engine:LeaseDuration</c>.</summary>
    public TimeSpan IoTimeout { get; set; } = TimeSpan.FromMinutes(1);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        var directories = new (string Name, string Value)[]
        {
            (nameof(SourceDirectory), SourceDirectory),
            (nameof(SigningDirectory), SigningDirectory),
            (nameof(SignedDirectory), SignedDirectory),
            (nameof(FinalDirectory), FinalDirectory),
        };
        foreach (var (name, value) in directories)
        {
            if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            {
                errors.Add($"{name} must be an absolute path, got '{value}'.");
            }
        }

        var distinct = directories
            .Where(d => !string.IsNullOrWhiteSpace(d.Value) && Path.IsPathFullyQualified(d.Value))
            .Select(d => Path.TrimEndingDirectorySeparator(Path.GetFullPath(d.Value)))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .Count();
        if (errors.Count == 0 && distinct != directories.Length)
        {
            errors.Add("Source, signing, signed and final directories must be different.");
        }

        if (string.IsNullOrWhiteSpace(SignatureSuffix) || SignatureSuffix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            errors.Add($"{nameof(SignatureSuffix)} must be a non-empty file name suffix, got '{SignatureSuffix}'.");
        }

        if (IoTimeout <= TimeSpan.Zero)
        {
            errors.Add($"{nameof(IoTimeout)} must be positive.");
        }

        return errors;
    }
}

/// <summary>Настройки этапа подписания через каталоги.</summary>
/// <param name="FileNameAttribute">Атрибут документа с именем файла (только имя, без каталогов).</param>
public sealed record FileSigningStageSettings(string FileNameAttribute = "file_name");

/// <summary>
/// Подписание через каталоги: файл документа перемещается из <see cref="FileSigningOptions.SourceDirectory"/> в
/// <see cref="FileSigningOptions.SigningDirectory"/>; этап ждёт, пока ПО подписания положит файл и подпись
/// (<c>{имя}{SignatureSuffix}</c>) в <see cref="FileSigningOptions.SignedDirectory"/>, затем перемещает оба в
/// <see cref="FileSigningOptions.FinalDirectory"/> и возвращает подпись движку, который сохраняет её в базе.
/// </summary>
/// <remarks>
/// <para>
/// Каждый шаг идемпотентен и определяется тем, где сейчас лежат файлы, поэтому повтор после сбоя безопасен:
/// файл уже на подписании — ждём; файл и подпись частично перенесены в финальный каталог — доносим остальное.
/// </para>
/// <para>
/// Файл, ещё открытый на запись ПО подписания, не трогается: этап продолжает ждать.
/// Отсутствие исходного файла — техническая ошибка с повтором (файл мог ещё не доехать); после
/// <c>Engine:MaxAttempts</c> маршрут завершается с <c>ATTEMPTS_EXHAUSTED</c>. Некорректное имя файла — бизнес-ошибка.
/// По истечении срока этапа файлы не перемещаются: решение о них остаётся за оператором.
/// </para>
/// </remarks>
public sealed class FileSigningStageHandler : IStageHandler
{
    private const int BufferSize = 81920;

    private readonly string _source;
    private readonly string _signing;
    private readonly string _signed;
    private readonly string _final;
    private readonly string _suffix;
    private readonly TimeSpan _timeout;

    public FileSigningStageHandler(FileSigningOptions options)
    {
        var errors = options.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException($"Invalid file signing options: {string.Join(" ", errors)}", nameof(options));
        }

        _source = Path.GetFullPath(options.SourceDirectory);
        _signing = Path.GetFullPath(options.SigningDirectory);
        _signed = Path.GetFullPath(options.SignedDirectory);
        _final = Path.GetFullPath(options.FinalDirectory);
        _suffix = options.SignatureSuffix;
        _timeout = options.IoTimeout;
    }

    public async Task<StageOutcome> StartAsync(StageContext stage, CancellationToken cancellationToken)
    {
        if (!TryGetFileName(stage, out var fileName, out var failure))
        {
            return failure;
        }

        var data = Data(fileName);
        await RunAsync(stage, () =>
        {
            var inSigning = Path.Combine(_signing, fileName);
            if (File.Exists(inSigning)
                || File.Exists(Path.Combine(_signed, fileName))
                || File.Exists(Path.Combine(_final, fileName)))
            {
                // Файл уже передан на подписание (повтор шага) или уже подписан.
                return;
            }

            var source = Path.Combine(_source, fileName);
            if (!File.Exists(source))
            {
                throw new FileNotFoundException($"Document file '{fileName}' is not found in the source directory.", source);
            }

            Directory.CreateDirectory(_signing);
            File.Move(source, inSigning, overwrite: false);
        }, cancellationToken);

        return new StageOutcome.Waiting(data);
    }

    public async Task<StageOutcome> CheckAsync(StageContext stage, CancellationToken cancellationToken)
    {
        if (!TryGetFileName(stage, out var fileName, out var failure))
        {
            return failure;
        }

        var signatureName = fileName + _suffix;
        byte[]? signature = null;
        await RunAsync(stage, () =>
        {
            var names = new[] { fileName, signatureName };
            var pending = new List<string>();
            foreach (var name in names)
            {
                if (File.Exists(Path.Combine(_signed, name)))
                {
                    if (!IsComplete(Path.Combine(_signed, name)))
                    {
                        return;
                    }

                    pending.Add(name);
                }
                else if (!File.Exists(Path.Combine(_final, name)))
                {
                    // Файл или подпись ещё не появились.
                    return;
                }
            }

            Directory.CreateDirectory(_final);
            foreach (var name in pending)
            {
                // Перезапись: в финальном каталоге может остаться копия от прерванного перемещения между томами.
                File.Move(Path.Combine(_signed, name), Path.Combine(_final, name), overwrite: true);
            }

            signature = File.ReadAllBytes(Path.Combine(_final, signatureName));
        }, cancellationToken);

        return signature is null
            ? new StageOutcome.Waiting()
            : new StageOutcome.Done(Data: Data(fileName), File: new StageFile(signatureName, Content: signature));
    }

    public IReadOnlyList<string> ValidateSettings(object? settings) => settings switch
    {
        FileSigningStageSettings { FileNameAttribute: var a } when string.IsNullOrWhiteSpace(a) =>
            ["file name attribute must not be empty"],
        FileSigningStageSettings => [],
        _ => [$"expected {nameof(FileSigningStageSettings)}, got {settings?.GetType().Name ?? "null"}"],
    };

    /// <summary>Имя файла из атрибута документа: только имя, без каталогов и относительных сегментов.</summary>
    private static bool TryGetFileName(StageContext stage, out string fileName, out StageOutcome failure)
    {
        var settings = stage.GetSettings<FileSigningStageSettings>();
        fileName = "";
        failure = null!;

        string? value;
        try
        {
            value = stage.Document.GetString(settings.FileNameAttribute);
        }
        catch (DocumentAttributeException ex)
        {
            failure = new StageOutcome.Failed(ex.Message);
            return false;
        }

        if (string.IsNullOrWhiteSpace(value)
            || value is "." or ".."
            || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || value.Contains('/') || value.Contains('\\')
            || Path.GetFileName(value) != value)
        {
            failure = new StageOutcome.Failed($"Document file name '{value}' must be a plain file name.");
            return false;
        }

        fileName = value;
        return true;
    }

    /// <summary>Файл дописан: его удаётся открыть монопольно.</summary>
    private static bool IsComplete(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, BufferSize);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Выполняет файловые операции с тайм-аутом: зависший вызов сетевой файловой системы не должен держать аренду.
    /// </summary>
    private async Task RunAsync(StageContext stage, Action action, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Run(action, cancellationToken).WaitAsync(_timeout, cancellationToken);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(
                $"File operations of stage '{stage.StageKey}' did not finish within {_timeout}.", ex);
        }
    }

    private static string Data(string fileName) => JsonSerializer.Serialize(new { fileName });
}

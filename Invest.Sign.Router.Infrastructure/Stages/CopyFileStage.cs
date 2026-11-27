using Invest.Sign.Router.Core.Routing;

namespace Invest.Sign.Router.Infrastructure.Stages;

/// <summary>
/// Этап, который копирует файл документа в каталог назначения и сразу завершается (<see cref="CopyFileStageHandler"/>).
/// Корни — секция <c>CopyFile</c>, обработчик подключается <c>AddCopyFileStage()</c>.
/// </summary>
public abstract class CopyFileStage : RouteStage<CopyFileStageHandler>
{
    /// <summary>Каталог назначения относительно <c>CopyFile:TargetRoot</c>.</summary>
    protected abstract string TargetFolder { get; }

    /// <summary>Атрибут документа с путём исходного файла относительно <c>CopyFile:SourceRoot</c>.</summary>
    protected virtual string SourceAttribute => "file_ref";

    /// <summary>Шаблон имени целевого файла, см. <see cref="CopyFileStageSettings.FileName"/>.</summary>
    protected virtual string FileName => "{docId}_{fileName}";

    public sealed override object? Settings => new CopyFileStageSettings(TargetFolder, SourceAttribute, FileName);
}

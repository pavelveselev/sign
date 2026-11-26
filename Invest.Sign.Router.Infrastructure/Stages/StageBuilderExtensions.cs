using Invest.Sign.Router.Core.Routing;

namespace Invest.Sign.Router.Infrastructure.Stages;

/// <summary>Сокращения fluent API для этапов из этой сборки.</summary>
public static class StageBuilderExtensions
{
    /// <summary>Копирует файл документа в <paramref name="targetFolder"/> и сразу завершает этап.</summary>
    /// <param name="targetFolder">Каталог назначения относительно <c>CopyFile:TargetRoot</c>.</param>
    /// <param name="sourceAttribute">Атрибут документа с путём исходного файла относительно <c>CopyFile:SourceRoot</c>.</param>
    /// <param name="fileName">Шаблон имени целевого файла, см. <see cref="CopyFileStageSettings.FileName"/>.</param>
    public static StageBuilder CopyFile(
        this StageBuilder stage,
        string targetFolder,
        string sourceAttribute = "file_ref",
        string fileName = "{docId}_{fileName}") =>
        stage.Handler<CopyFileStageHandler>(new CopyFileStageSettings(targetFolder, sourceAttribute, fileName));

    /// <summary>
    /// Подписание через каталоги (<see cref="FileSigningStageHandler"/>): файл документа уходит на подписание,
    /// подписанный файл и подпись переносятся в финальный каталог, подпись сохраняется в базе.
    /// </summary>
    /// <param name="fileNameAttribute">Атрибут документа с именем файла.</param>
    public static StageBuilder FileSigning(this StageBuilder stage, string fileNameAttribute = "file_name") =>
        stage.Handler<FileSigningStageHandler>(new FileSigningStageSettings(fileNameAttribute));

    /// <summary>Выполняет этап HTTP-сервисом по пути <paramref name="path"/> относительно <c>HttpStage:BaseAddress</c>.</summary>
    public static StageBuilder Http(this StageBuilder stage, string path) =>
        stage.Handler<HttpStageHandler>(new HttpStageSettings(path));
}

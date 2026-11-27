using Invest.Sign.Router.Core.Routing;

namespace Invest.Sign.Router.Infrastructure.Stages;

/// <summary>
/// Этап подписания через каталоги для маршрутов-классов (<see cref="FileSigningStageHandler"/>): файл документа
/// уходит на подписание, подписанный файл и подпись переносятся в финальный каталог, подпись сохраняется в базе.
/// Каталоги — секция <c>FileSigning</c>.
/// </summary>
public abstract class FileSigningStage : RouteStage<FileSigningStageHandler>
{
    /// <summary>Атрибут документа (ключ JSON <c>attributes</c> из вьюхи) с именем файла.</summary>
    protected virtual string FileNameAttribute => "file_name";

    public sealed override object? Settings => new FileSigningStageSettings(FileNameAttribute);
}

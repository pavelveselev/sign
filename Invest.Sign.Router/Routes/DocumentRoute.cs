using Invest.Sign.Router.Core.Routing;
using Invest.Sign.Router.Infrastructure.Stages;

namespace Invest.Sign.Router.Routes;

/// <summary>
/// Путь документа через каталоги: строка вьюхи входящих документов с <c>doc_type = 'document'</c> →
/// файл <c>attributes.file_name</c> из <c>FileSigning:SourceDirectory</c> в <c>SigningDirectory</c> →
/// ожидание файла и подписи в <c>SignedDirectory</c> → перенос в <c>FinalDirectory</c>, подпись в Postgres.
/// </summary>
/// <remarks>
/// <c>document</c> — заглушка для реального <c>doc_type</c> (открытый вопрос 9). Изменение этапов при наличии
/// документов в работе — новая версия в <see cref="Versions"/>; старая версия остаётся.
/// </remarks>
public sealed class DocumentRoute : SigningRoute
{
    public override string Code => "document";

    public override IReadOnlyList<RouteVersion> Versions { get; } =
    [
        new(1, new SignStage()),
    ];

    /// <summary>Подписание через каталоги; имя файла — атрибут <c>file_name</c>.</summary>
    private sealed class SignStage : FileSigningStage
    {
        public override string Key => "sign";

        public override TimeSpan Timeout => TimeSpan.FromDays(7);
    }
}

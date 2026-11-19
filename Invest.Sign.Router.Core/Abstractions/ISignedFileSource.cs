namespace Invest.Sign.Router.Core.Abstractions;

/// <summary>Читает подписанные файлы, размещённые ПО подписания (открытый вопрос 8: способ доступа не выбран).</summary>
public interface ISignedFileSource
{
    /// <summary>
    /// Читает файл целиком. Любое исключение (файл отсутствует, заблокирован, общий ресурс недоступен) является технической
    /// ошибкой и повторяется движком.
    /// </summary>
    Task<byte[]> ReadAsync(string fileRef, CancellationToken cancellationToken);
}

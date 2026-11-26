namespace Invest.Sign.Router.Infrastructure.Files;

/// <summary>Отображает относительные ссылки на файлы в пути внутри корня общего ресурса.</summary>
internal static class SharePath
{
    private static StringComparison Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Полный путь корня без завершающего разделителя.</summary>
    public static string NormalizeRoot(string root) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    /// <summary>
    /// Отображает <paramref name="fileRef"/> в полный путь внутри <paramref name="root"/> (результат <see cref="NormalizeRoot"/>).
    /// В качестве разделителей принимаются и <c>/</c>, и <c>\</c>.
    /// </summary>
    /// <exception cref="InvalidFileRefException">Ссылка пустая, абсолютная или указывает за пределы корня.</exception>
    public static string Resolve(string root, string? fileRef)
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
            full = Path.GetFullPath(Path.Combine(root, relative));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidFileRefException(fileRef, ex.Message);
        }

        if (!full.StartsWith(root + Path.DirectorySeparatorChar, Comparison))
        {
            throw new InvalidFileRefException(fileRef, "the reference points outside the share root");
        }

        return full;
    }

    /// <summary>Путь относительно корня с разделителем <c>/</c>, как его хранят в данных этапа.</summary>
    public static string ToRelative(string root, string full) =>
        Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/');
}

using Microsoft.Extensions.Options;

namespace Invest.Sign.Router;

internal static class OptionsRegistration
{
    /// <summary>
    /// Привязывает <typeparamref name="T"/> к секции конфигурации и проверяет его при запуске хоста
    /// собственным методом проверки класса настроек.
    /// </summary>
    public static IServiceCollection AddValidatedOptions<T>(
        this IServiceCollection services, string sectionName, Func<T, IReadOnlyList<string>> validate)
        where T : class
    {
        services.AddOptions<T>()
            .BindConfiguration(sectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<T>>(new DelegateValidator<T>(sectionName, validate));
        return services;
    }

    private sealed class DelegateValidator<T>(string sectionName, Func<T, IReadOnlyList<string>> validate)
        : IValidateOptions<T>
        where T : class
    {
        public ValidateOptionsResult Validate(string? name, T options)
        {
            var errors = validate(options);
            return errors.Count == 0
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail(errors.Select(e => $"{sectionName}: {e}"));
        }
    }
}

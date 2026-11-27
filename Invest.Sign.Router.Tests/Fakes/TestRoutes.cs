using Invest.Sign.Router.Core.Model;
using Invest.Sign.Router.Core.Routing;

namespace Invest.Sign.Router.Tests.Fakes;

/// <summary>Маршрут для тестов: код и версии задаются в конструкторе.</summary>
public sealed class TestRoute(string code, params IReadOnlyList<RouteVersion> versions) : SigningRoute
{
    public override string Code => code;

    public override IReadOnlyList<RouteVersion> Versions => versions;
}

/// <summary>Этап подписания во внешней системе, которую эмулирует <see cref="InMemoryDatabase"/>.</summary>
public sealed class SigningStage(
    string key, string signer, TimeSpan timeout, Func<DocumentContext, bool>? when = null)
    : RouteStage<FakeSigningStageHandler>
{
    public override string Key => key;

    public override TimeSpan Timeout => timeout;

    public override object? Settings => new FakeSigningSettings(signer);

    public override Func<DocumentContext, bool>? Condition => when;
}

/// <summary>Этап, который выполняет <see cref="ScriptedStageHandler"/>.</summary>
public sealed class ScriptedStage(string key, object? settings, TimeSpan timeout) : RouteStage<ScriptedStageHandler>
{
    public override string Key => key;

    public override TimeSpan Timeout => timeout;

    public override object? Settings => settings;
}

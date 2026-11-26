using Invest.Sign.Router.Core.Abstractions;

namespace Invest.Sign.Router;

/// <summary>Находит обработчики этапов в контейнере DI: обработчик регистрируется под своим конкретным типом.</summary>
internal sealed class ServiceProviderStageHandlerResolver(IServiceProvider services) : IStageHandlerResolver
{
    public IStageHandler? Find(Type handlerType) => services.GetService(handlerType) as IStageHandler;
}

using PageToMovie.Core.Options;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace PageToMovie.Fakes;

public static class ServiceCollectionExtensions
{
    /// <summary>Register fake Grok clients (video/image/chat/vision).</summary>
    public static IServiceCollection AddPageToMovieFakes(this IServiceCollection services)
    {
        services.AddSingleton<IVideoClient, FakeGrokVideoClient>();
        services.AddSingleton<IImageClient, FakeGrokImageClient>();
        services.AddSingleton<IChatClient, FakeGrokChatClient>();
        services.AddSingleton<IVisionClient, FakeGrokVisionClient>();
        return services;
    }
}

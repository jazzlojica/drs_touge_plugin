using AssettoServer.Server.Plugin;
using Autofac;
using Microsoft.Extensions.Hosting;

namespace DrsTougePlugin;

public class DrsTougeModule : AssettoServerModule<DrsTougeConfiguration>
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.RegisterType<RankingsClient>().AsSelf().SingleInstance();
        builder.RegisterType<ResultPoster>().AsSelf().SingleInstance();
        // Registered as a hosted service so it starts with the server on every AssettoServer version.
        builder.RegisterType<TougeManager>().AsSelf().As<IHostedService>().SingleInstance();
        builder.RegisterType<TougeCommands>().AsSelf();
    }
}

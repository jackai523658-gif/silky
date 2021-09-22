using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Silky.Core.Modularity;
using Silky.Rpc;

namespace Silky.RegistryCenter.Zookeeper
{
    [DependsOn(typeof(RpcModule))]
    public class ZookeeperModule : SilkyModule
    {
        public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
        {
            services.AddZookeeperRegistryCenter();
        }
    }
}
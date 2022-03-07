using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Silky.Core.Exceptions;
using Silky.Core.Runtime.Rpc;
using Silky.Core.Serialization;
using Silky.HealthChecks.Rpc.ServerCheck;
using Silky.Http.Core.Handlers;
using Silky.Rpc.Endpoint;
using Silky.Rpc.Extensions;
using Silky.Rpc.Runtime.Server;
using Silky.Rpc.Security;

namespace Silky.HealthChecks.Rpc
{
    public class SilkyRpcHealthCheck : SilkyHealthCheckBase
    {
        public SilkyRpcHealthCheck(IServerManager serverManager,
            IServerHealthCheck serverHealthCheck,
            ICurrentRpcToken currentRpcToken,
            ISerializer serializer,
            IHttpHandleDiagnosticListener httpHandleDiagnosticListener,
            IHttpContextAccessor httpContextAccessor,
            IServiceEntryLocator serviceEntryLocator) : base(serverManager, serverHealthCheck, currentRpcToken,
            serializer, httpHandleDiagnosticListener, httpContextAccessor, serviceEntryLocator)
        {
        }

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
            CancellationToken cancellationToken = new CancellationToken())
        {
            _currentRpcToken.SetRpcToken();
            _httpContextAccessor.HttpContext.SetHttpHandleAddressInfo();
            var messageId = GetMessageId(_httpContextAccessor.HttpContext);
            var serviceEntry = _serviceEntryLocator.GetServiceEntryById(HealthCheckConstants.HealthCheckServiceEntryId);
            var tracingTimestamp =
                _httpHandleDiagnosticListener.TracingBefore(messageId, serviceEntry, _httpContextAccessor.HttpContext,
                    Array.Empty<object>());
            try
            {
                var rpcServers =
                    _serverManager.Servers.Where(p => p.Endpoints.Any(e => e.ServiceProtocol == ServiceProtocol.Tcp));
                var healthData = new Dictionary<string, object>();
                foreach (var server in rpcServers)
                {
                    foreach (var endpoint in server.Endpoints)
                    {
                        if (endpoint.ServiceProtocol == ServiceProtocol.Tcp)
                        {
                            var endpointHealthData = new ServerHealthData()
                            {
                                HostName = server.HostName,
                                Address = endpoint.GetAddress(),
                                ServiceProtocol = endpoint.ServiceProtocol,
                            };
                            bool isHealth;
                            try
                            {
                                isHealth = await _serverHealthCheck.IsHealth(endpoint);
                            }
                            catch (Exception e)
                            {
                                isHealth = false;
                            }

                            endpointHealthData.Health = isHealth;
                            healthData[endpoint.GetAddress()] = endpointHealthData;
                        }
                    }
                }

                var gatewayServers =
                    _serverManager.Servers.Where(p => p.Endpoints.Any(e =>
                        e.ServiceProtocol == ServiceProtocol.Http || e.ServiceProtocol == ServiceProtocol.Https));

                foreach (var gatewayServer in gatewayServers)
                {
                    foreach (var endpoint in gatewayServer.Endpoints)
                    {
                        if (endpoint.ServiceProtocol == ServiceProtocol.Http ||
                            endpoint.ServiceProtocol == ServiceProtocol.Https)
                        {
                            var endpointHealthData = new ServerHealthData()
                            {
                                HostName = gatewayServer.HostName,
                                Address = endpoint.GetAddress(),
                                ServiceProtocol = endpoint.ServiceProtocol,
                            };
                            bool isHealth;
                            try
                            {
                                isHealth = await _serverHealthCheck.IsHealth(endpoint);
                            }
                            catch (Exception e)
                            {
                                isHealth = false;
                            }

                            if (!isHealth)
                            {
                                gatewayServer.RemoveEndpoint(endpoint);
                            }

                            endpointHealthData.Health = isHealth;
                            healthData[endpoint.GetAddress()] = endpointHealthData;
                        }
                    }
                }

                var serverHealthList = healthData.Values.Select(p => (ServerHealthData)p);
                var serverHealthGroups = serverHealthList.GroupBy(p => p.HostName);
                var healthCheckDescriptions = new Dictionary<string, object>();
                foreach (var serverHealthGroup in serverHealthGroups)
                {
                    var serverDesc = new List<string>();
                    var healthCount = serverHealthGroup.Count(p => p.Health);
                    if (healthCount > 0)
                    {
                        serverDesc.Add($"HealthCount:{healthCount}");
                    }

                    var unHealthCount = serverHealthGroup.Count(p => !p.Health);
                    if (unHealthCount > 0)
                    {
                        serverDesc.Add($"UnHealthCount:{unHealthCount}");
                    }

                    healthCheckDescriptions[serverHealthGroup.Key] = serverDesc;
                }

                var detail = _serializer.Serialize(healthCheckDescriptions, false);
                if (healthData.Values.All(p => ((ServerHealthData)p).Health))
                {
                    return HealthCheckResult.Healthy(
                        $"There are a total of {healthData.Count} Rpc service provider instances." +
                        $"{Environment.NewLine} server detail:{detail}.",
                        healthData);
                }

                if (healthData.Values.All(p => !((ServerHealthData)p).Health))
                {
                    return HealthCheckResult.Unhealthy(
                        $"There are a total of {healthData.Count} Rpc service provider instances, and all service provider instances are unhealthy." +
                        $"{Environment.NewLine} server detail:{detail}.",
                        null, healthData);
                }

                var unHealthData = healthData.Values.Where(p => !((ServerHealthData)p).Health)
                    .Select(p => (ServerHealthData)p).ToArray();

                return HealthCheckResult.Degraded(
                    $"There are a total of {healthData.Count}  Rpc service provider instances," +
                    $" of which {unHealthData.Count()}" +
                    $" service instances are unhealthy{Environment.NewLine}." +
                    $" unhealthy instances:{string.Join(",", unHealthData.Select(p => p.Address))}." +
                    $" server detail:{detail}.",
                    null, healthData);
            }
            catch (Exception ex)
            {
                _httpHandleDiagnosticListener.TracingError(tracingTimestamp, messageId, serviceEntry,
                    _httpContextAccessor.HttpContext, ex, StatusCode.ServerError);
                return HealthCheckResult.Unhealthy("health error", ex);
            }
            finally
            {
                _httpHandleDiagnosticListener.TracingAfter(tracingTimestamp, messageId, serviceEntry,
                    _httpContextAccessor.HttpContext, null);
            }
        }

        protected override async Task<bool> CheckHealthEndpoint(IRpcEndpoint endpoint)
        {
            bool isHealth;
            try
            {
                isHealth = await _serverHealthCheck.IsHealth(endpoint);
            }
            catch (Exception e)
            {
                isHealth = false;
            }

            return isHealth;
        }

        protected override ICollection<IServer> GetServers()
        {
            return _serverManager.Servers.Where(p => p.Endpoints.Any(e => e.ServiceProtocol == ServiceProtocol.Tcp))
                .ToArray();
        }

        private string GetMessageId(HttpContext httpContext)
        {
            httpContext.SetHttpMessageId();
            return httpContext.TraceIdentifier;
        }
    }
}
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.ApplicationInsights.WorkerService;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Extensions.Configuration.AzureAppConfiguration;
using Microsoft.Xrm.Sdk;
using Azure.Core;
using static Cpi.Processes.Eoc.EocProcessing;
using Cpi.Processes.Eoc;

namespace Cpi.Preprocessor.Core.Lead
    {
        class InitializeComponents
        {
            private static string _envName = Environment.GetEnvironmentVariable("AZURE_FUNCTION_ENVIRONMENT");
            public static async Task Main(string[] args)
            {
                var builder = FunctionsApplication.CreateBuilder(args);
                var appConfigConnectionString = Environment.GetEnvironmentVariable("AZURE_APPCONFIG_CONNECTION_STRING");

                // Add Azure App Configuration
                builder.Configuration.AddAzureAppConfiguration(options =>
                {
                    //Uri endpoint = new(Environment.GetEnvironmentVariable("AZURE_APPCONFIG_ENDPOINT") ??
                    //  throw new InvalidOperationException("The environment variable 'AZURE_APPCONFIG_ENDPOINT' is not set or is empty."));
                    options.Connect(appConfigConnectionString)
                            .Select(Environment.GetEnvironmentVariable("FunctionNameAppConfigPrefix") + ":*", _envName.ToLower())
                            .Select(Environment.GetEnvironmentVariable("FunctionNameAppConfigPrefix") + ":*", LabelFilter.Null)
                            .Select("APPLICATIONINSIGHTS_CONNECTION_STRING", _envName.ToLower());
                    options.ConfigureKeyVault(kv =>
                    {
                        kv.SetCredential(new Azure.Identity.ChainedTokenCredential(new VisualStudioCredential(),
                                                                    new DefaultAzureCredential()));
                    });
                });

                builder.Services
                    .AddApplicationInsightsTelemetryWorkerService()
                    .ConfigureFunctionsApplicationInsights()
                    .AddAzureAppConfiguration()
                    .AddMemoryCache()
                    .AddSingleton<ListenerServiceBusClient>(sp =>
                    {
                        var configuration = sp.GetRequiredService<IConfiguration>();
                        var listenerNamespace = Environment.GetEnvironmentVariable("eoc_processing:ServiceBus__fullyQualifiedNamespace");

                        return new ListenerServiceBusClient(new ServiceBusClient(listenerNamespace, new Azure.Identity.ChainedTokenCredential(new VisualStudioCredential(),
                                                                    new DefaultAzureCredential())));
                    })

                    // Register SENDER client (for sending to external topic namespace)
                    .AddSingleton<SenderServiceBusClient>(sp =>
                    {
                        var configuration = sp.GetRequiredService<IConfiguration>();
                        var senderNamespace = Environment.GetEnvironmentVariable("eoc_processing:ServiceBus__fullyQualifiedNamespace");

                        return new SenderServiceBusClient(new ServiceBusClient(senderNamespace, new DefaultAzureCredential()));
                    })

                    // Register ServiceBusSender using the SENDER client
                    .AddSingleton<ServiceBusSender>(sp =>
                    {
                        var senderClient = sp.GetRequiredService<SenderServiceBusClient>();
                        var configuration = sp.GetRequiredService<IConfiguration>();
                        var topicName = "eoc-processing";

                        return senderClient.Client.CreateSender(topicName);
                    })

                   // Default credential for general services (uses System-Assigned Identity)
                   .AddSingleton<DefaultAzureCredential>(sp => new DefaultAzureCredential())

                   // User-Assigned credential specifically for Dataverse
                   .AddSingleton<TokenCredential>(sp =>
                   {
                       var clientId = Environment.GetEnvironmentVariable("Dataverse:UserAssignedClientId");
                       if (string.IsNullOrEmpty(clientId))
                       {
                           return new Azure.Identity.ChainedTokenCredential(new VisualStudioCredential(),
                                                                    new DefaultAzureCredential());
                       }
                       var options = new DefaultAzureCredentialOptions
                       {
                           ManagedIdentityClientId = clientId
                       };
                       return new DefaultAzureCredential(options);
                   })

                   .AddSingleton<IOrganizationServiceAsync2, ServiceClient>(provider =>
                   {
                       var credential = provider.GetRequiredService<TokenCredential>();
                       var environment = Environment.GetEnvironmentVariable("Dataverse:EnvironmentUrl");
                       var cache = provider.GetService<IMemoryCache>();
                       return new ServiceClient(
                        tokenProviderFunction: f => GetToken(environment, credential, cache),
                        instanceUrl: new Uri(environment),
                        useUniqueInstance: true);
                   })
                   .AddSingleton<ICrmClient, CrmClient>()
                    .Configure<ApplicationInsightsServiceOptions>(options =>
                    {
                        //options.ConnectionString = Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING");
                        options.ConnectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];

                    })
                    .Configure<MessagingOptions>(builder.Configuration.GetSection(Environment.GetEnvironmentVariable("FunctionNameAppConfigPrefix") + ":Messaging"));

                // Configure logging BEFORE building
                builder.Logging.ClearProviders();
                builder.Logging.AddConsole();
                builder.Logging.SetMinimumLevel(LogLevel.Information);

                // Set specific log levels for your namespaces
                builder.Services.Configure<LoggerFilterOptions>(options =>
                {
                    // Clear any restrictive default rules
                    options.MinLevel = LogLevel.Information;

                    // Your application - allow Information and above
                    options.Rules.Add(new LoggerFilterRule(
                        null,
                        "Cpi.BizApps.Processes.Eoc",
                        LogLevel.Information,
                        null));

                    // Allow Function execution logs
                    options.Rules.Add(new LoggerFilterRule(
                        null,
                        "Function",
                        LogLevel.Information,
                        null));

                    // Reduce Microsoft noise
                    options.Rules.Add(new LoggerFilterRule(
                        null,
                        "Microsoft",
                        LogLevel.Warning,
                        null));
                });

                // CRITICAL: Set the connection string as environment variable BEFORE building
                var aiConnectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
                Console.WriteLine($"APPLICATIONINSIGHTS_CONNECTION_STRING from config: {(!string.IsNullOrEmpty(aiConnectionString) ? "SET" : "NOT SET")}");

                if (!string.IsNullOrEmpty(aiConnectionString))
                {
                    // The Functions Worker needs this as an environment variable
                    Environment.SetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING", aiConnectionString);
                    Console.WriteLine("Application Insights connection string set in environment.");

                    // Also configure TelemetryConfiguration directly
                    builder.Services.Configure<Microsoft.ApplicationInsights.Extensibility.TelemetryConfiguration>(config =>
                    {
                        config.ConnectionString = aiConnectionString;
                    });
                }
                else
                {
                    Console.WriteLine("WARNING: Application Insights connection string is missing!");
                }
                Console.WriteLine("=== End Configuration Troubleshooting ===");

                builder.ConfigureFunctionsWebApplication();
                builder.Build().Run();
            }

            static async Task<string> GetToken(string environment, Azure.Core.TokenCredential credential, IMemoryCache cache)
            {
                var accessToken = await cache.GetOrCreateAsync(environment, async (cacheEntry) =>
                {
                    cacheEntry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(50);
                    var token = (await credential.GetTokenAsync(new Azure.Core.TokenRequestContext(new[] { $"{environment}/.default" }), CancellationToken.None));
                    return token;
                });
                return accessToken.Token;
            }
        }
    }
using System;
using System.CommandLine.Builder;
using System.CommandLine.Hosting;
using System.CommandLine.Parsing;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polly;
using Serilog;
using Tommy.Extensions.Configuration;

namespace UnityDataMiner
{
    public class MinerOptions
    {
        public string? NuGetSource { get; set; }
        public string? NuGetSourceKey { get; set; }
    }

    internal static class Program
    {
        private static readonly MinerOptions Options = new();

        public static async Task<int> Main(string[] args)
        {
            Log.Logger = new LoggerConfiguration()
                .WriteTo.Console()
                .CreateBootstrapLogger();

            try
            {
                return await new CommandLineBuilder(new MineCommand())
                    .UseHost(builder =>
                    {
                        builder
                            .UseConsoleLifetime(opts => opts.SuppressStatusMessages = true)
                            .ConfigureAppConfiguration(configuration => configuration.AddTomlFile("config.toml", true))
                            .ConfigureServices(services =>
                            {
                                services.AddOptions<MinerOptions>().BindConfiguration("MinerOptions");

                                services.AddHttpClient("unity", client =>
                                {
                                    client.BaseAddress = new Uri("https://unity.com/");
                                }).AddTransientHttpErrorPolicy(policy => policy.WaitAndRetryAsync(5, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt))));
                            })
                            .UseCommandHandler<MineCommand, MineCommand.Handler>()
                            .UseSerilog((context, services, loggerConfiguration) => loggerConfiguration
                                .MinimumLevel.Debug()
                                .ReadFrom.Configuration(context.Configuration)
                                .Enrich.FromLogContext()
                                .WriteTo.Console());
                    })
                    .UseDefaults()
                    .UseExceptionHandler((ex, _) => Log.Fatal(ex, "Exception, cannot continue!"), -1)
                    .Build()
                    .InvokeAsync(args);
            }
            finally
            {
                await Log.CloseAndFlushAsync();
            }
        }
    }
}

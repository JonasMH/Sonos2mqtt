using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using Sonos2Mqtt;
using Sonos2Mqtt.Client;
using Sonos2Mqtt.Commands;
using ToMqttNet;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Logging.AddSimpleConsole(options =>
{
    options.IncludeScopes = true;
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});

var services = builder.Services;

services.AddOptions<SonosOptions>()
    .BindConfiguration(SonosOptions.Section)
    .Validate(x => x.SeedHosts.Count > 0, "At least one Sonos:SeedHosts entry is required")
    .ValidateOnStart();

services.AddHealthChecks();
services.AddOpenTelemetry()
    .WithMetrics(builder =>
    {
        builder.AddPrometheusExporter();
        builder.AddMeter(SonosMetrics.MeterName);
        builder.AddMeter("ToMqttNet");
        builder.AddMeter("System.Net.Http",
                         "Microsoft.AspNetCore.Hosting",
                         "Microsoft.AspNetCore.Server.Kestrel");
    });

services.AddHttpClient(SonosSoapClient.HttpClientName)
    .ConfigureHttpClient((serviceProvider, client) =>
        client.Timeout = serviceProvider.GetRequiredService<IOptions<SonosOptions>>().Value.RequestTimeout);

services.AddMqttConnection();
services.AddSingleton<SonosMetrics>();
services.AddSingleton<SonosSoapClient>();
services.AddSingleton<SonosTopicHelper>();
services.AddSingleton<SonosSpeakerRegistry>();
services.AddSingleton<SonosNotificationPlayer>();
services.AddSingleton<SonosEventSubscriber>();
services.AddSingleton<SonosCommandExecutor>();
services.AddHostedService<SonosHouseholdService>();
services.AddHostedService<SonosCommandHandler>();

var app = builder.Build();

app.MapHealthChecks("/health");
app.MapPrometheusScrapingEndpoint("/metrics");
app.MapMethods(SonosEventSubscriber.CallbackPath, ["NOTIFY"], app.Services.GetRequiredService<SonosEventSubscriber>().HandleNotifyAsync);

app.Run();

public partial class Program { }

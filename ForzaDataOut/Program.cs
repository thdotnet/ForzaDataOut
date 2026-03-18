using ForzaDataOut;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateDefaultBuilder(args)
    .ConfigureServices((hostContext, services) =>
    {
        services.AddHostedService<DataOutService>();
    });

var host = builder.Build();

Console.WriteLine("ForzaDataOut Console Application");
Console.WriteLine("===================================");
Console.WriteLine("Press Ctrl+C to stop");
Console.WriteLine();

await host.RunAsync();

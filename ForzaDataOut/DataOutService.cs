using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net.Sockets;
using System.Text;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;

namespace ForzaDataOut
{
    public class DataOutService : BackgroundService
    {
        private UdpClient? DataOutClient;
        private readonly IConfiguration Config;
        private readonly ILogger<DataOutService> Logger;

        private FileStream? CaptureFile;
        private bool IsFirstCaptureEntry = true;

        private EventHubProducerClient? EventHubClient;

        public DataOutService(IConfiguration config, ILogger<DataOutService> logger)
        {
            Config = config;
            Logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var consoleOutput = Config.GetValue<bool>("ForzaDataOut:ConsoleOutput:Enabled");
            var consoleOutputDrivingOnly = Config.GetValue<bool>("ForzaDataOut:ConsoleOutput:DrivingOnly");

            var capture = Config.GetValue<bool>("ForzaDataOut:Capture:Enabled");
            var captureDrivingOnly = Config.GetValue<bool>("ForzaDataOut:Capture:DrivingOnly");
            var savePath = Config.GetValue<string>("ForzaDataOut:Capture:SavePath");

            var eventHubEnabled = Config.GetValue<bool>("ForzaDataOut:EventHub:Enabled");
            var eventHubDrivingOnly = Config.GetValue<bool>("ForzaDataOut:EventHub:DrivingOnly");
            var eventHubConnectionString = Config.GetValue<string>("ForzaDataOut:EventHub:ConnectionString");

            var listenBindAddress = Config.GetValue<string>("ForzaDataOut:BindAddress");
            var listenPort = Config.GetValue<int>("ForzaDataOut:ListenPort");
            ListenServer listenServer = new ListenServer(listenBindAddress, listenPort, Logger);
            listenServer.Start(stoppingToken);

            var dataOutPort = Config.GetValue<int>("ForzaDataOut:DataOutPort");
            Logger.LogInformation($"ForzaDataOut listening on port {dataOutPort}");

            if (consoleOutput)
            {
                Logger.LogInformation($"Console output enabled (DrivingOnly: {consoleOutputDrivingOnly})");
            }

            if (eventHubEnabled && !string.IsNullOrWhiteSpace(eventHubConnectionString))
            {
                try
                {
                    EventHubClient = new EventHubProducerClient(eventHubConnectionString);
                    Logger.LogInformation($"Event Hub enabled (DrivingOnly: {eventHubDrivingOnly})");
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Failed to create Event Hub client");
                }
            }

            try
            {
                DataOutClient = new UdpClient(dataOutPort);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"Failed to create UDP client on port {dataOutPort}");
                return;
            }

            if (capture)
            {
                await CreateCaptureFile(savePath);
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (DataOutClient == null)
                    {
                        Logger.LogError("DataOutClient is null, exiting");
                        break;
                    }

                    var receiveResult = await DataOutClient.ReceiveAsync(stoppingToken);
                    var telemetryData = new TelemetryPacketDecoder(receiveResult.Buffer);
                    var json = telemetryData.toJson();

                    if (consoleOutput && (!consoleOutputDrivingOnly || (consoleOutputDrivingOnly && telemetryData.IsDriving)))
                    {
                        Console.WriteLine($"[{telemetryData.Timestamp}] Speed: {telemetryData.Speed:F2} m/s | RPM: {telemetryData.EngineCurrentRpm:F0} | Gear: {telemetryData.Gear} | Position: ({telemetryData.PositionX:F1}, {telemetryData.PositionY:F1}, {telemetryData.PositionZ:F1})");
                        Console.WriteLine(json);
                        Console.WriteLine();
                    }

                    if (capture && (!captureDrivingOnly || (captureDrivingOnly && telemetryData.IsDriving)))
                    {
                        await WriteToCaptureFile(json);
                        Logger.LogDebug($"[{telemetryData.Timestamp}] {receiveResult.Buffer.Length} bytes captured");
                    }

                    if (EventHubClient != null && (!eventHubDrivingOnly || (eventHubDrivingOnly && telemetryData.IsDriving)))
                    {
                        await SendToEventHub(json);
                    }

                    await listenServer.Broadcast(Encoding.UTF8.GetBytes(json + "\n"));
                }
                catch (OperationCanceledException)
                {
                    Logger.LogInformation("Data reception cancelled");
                    break;
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error processing telemetry data");
                }
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            await CloseCaptureFile();

            if (EventHubClient != null)
            {
                try
                {
                    await EventHubClient.CloseAsync(cancellationToken);
                    await EventHubClient.DisposeAsync();
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Error closing Event Hub client");
                }
            }

            if (DataOutClient != null)
            {
                try
                {
                    DataOutClient.Close();
                    DataOutClient.Dispose();
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Error closing DataOutClient");
                }
            }
        }

        private async Task CreateCaptureFile(string savePath)
        {
            var fileName = DateTime.Now.ToString("yyyyMMdd-HHmm") + ".json";

            if (string.IsNullOrWhiteSpace(savePath))
            {
                savePath = Path.GetTempPath();
                Logger.LogWarning($"SavePath not configured, using temp directory: {savePath}");
            }

            if (!Directory.Exists(savePath))
            {
                try
                {
                    Directory.CreateDirectory(savePath);
                    Logger.LogInformation($"Created capture directory: {savePath}");
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, $"Error creating capture directory: {savePath}");
                    return;
                }
            }

            try
            {
                var fullPath = Path.Combine(savePath, fileName);
                CaptureFile = File.Open(fullPath, FileMode.Create);
                IsFirstCaptureEntry = true;
                await WriteToCaptureFile("[");
                Logger.LogInformation($"Created capture file: {fullPath}");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"Error creating capture file: {fileName}");
            }
        }

        private async Task WriteToCaptureFile(string text)
        {
            if (CaptureFile != null)
            {
                try
                {
                    if (!IsFirstCaptureEntry)
                    {
                        await CaptureFile.WriteAsync(Encoding.UTF8.GetBytes(","));
                    }
                    else
                    {
                        IsFirstCaptureEntry = false;
                    }
                    await CaptureFile.WriteAsync(Encoding.UTF8.GetBytes(text));
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, $"Error writing to capture file");
                }
            }
        }

        private async Task CloseCaptureFile()
        {
            if (CaptureFile != null)
            {
                await WriteToCaptureFile("]");
                await CaptureFile.FlushAsync();
                CaptureFile.Close();
                CaptureFile = null;
            }
        }

        private async Task SendToEventHub(string json)
        {
            if (EventHubClient != null)
            {
                try
                {
                    using EventDataBatch eventBatch = await EventHubClient.CreateBatchAsync();
                    if (!eventBatch.TryAdd(new EventData(Encoding.UTF8.GetBytes(json))))
                    {
                        Logger.LogWarning("Event is too large for the batch");
                        return;
                    }
                    await EventHubClient.SendAsync(eventBatch);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error sending to Event Hub");
                }
            }
        }
    }
}
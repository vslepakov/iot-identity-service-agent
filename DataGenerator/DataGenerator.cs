using Microsoft.Azure.Devices.Client;
using Microsoft.Azure.Devices.Client.Exceptions;
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DataGenerator
{
    public class DataGenerator
    {
        private const int TemperatureThreshold = 30;
        private readonly TimeSpan sleepDuration = TimeSpan.FromSeconds(5);

        public async Task GenerateDataAsync(Func<Message, CancellationToken, Task> client, CancellationToken cancellationToken)
        {
            var messageCount = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                (Message message, string payload) = PrepareMessage(messageCount);

                try
                {
                    await client(message, cancellationToken);
                    Console.WriteLine($"Sent message {messageCount} of {payload}");

                    message.Dispose();
                    messageCount++;
                }
                catch (IotHubException ex) when (ex.IsTransient)
                {
                    // Inspect the exception to figure out if operation should be retried, or if user-input is required.
                    Console.WriteLine($"An IotHubException was caught, but will try to recover and retry: {ex}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Unexpected error {ex}");
                }

                await Task.Delay(sleepDuration, cancellationToken);
            }
        }

        private static (Message, string) PrepareMessage(int messageId)
        {
            var rnd = new Random();
            var temperature = rnd.Next(20, 35);
            var humidity = rnd.Next(60, 80);
            string messagePayload = $"{{\"temperature\":{temperature},\"humidity\":{humidity}}}";

            var eventMessage = new Message(Encoding.UTF8.GetBytes(messagePayload))
            {
                MessageId = messageId.ToString(),
                ContentEncoding = Encoding.UTF8.ToString(),
                ContentType = "application/json",
            };
            eventMessage.Properties.Add("temperatureAlert", (temperature > TemperatureThreshold) ? "true" : "false");

            return (eventMessage, messagePayload);
        }
    }
}

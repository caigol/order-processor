using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using RabbitMQ.Client;
using System;
using System.Text;

namespace OrderProcessor.Services
{
    public interface IRabbitMqPublisher
    {
        void PublishEvent(string messageJson, string queueName);
    }

    public class RabbitMqPublisher : IRabbitMqPublisher
    {
        private readonly ILogger<RabbitMqPublisher> _logger;
        private readonly IConfiguration _config;
        private RetryPolicy _retryPolicy;

        public RabbitMqPublisher(ILogger<RabbitMqPublisher> logger, IConfiguration config)
        {
            _logger = logger;
            _config = config;
            
            _retryPolicy = Policy
                .Handle<Exception>()
                .WaitAndRetry(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)), 
                    (exception, timeSpan, retryCount, context) => 
                    {
                        _logger.LogWarning("Falha ao conectar no RabbitMQ (Tentativa {RetryCount}). Erro: {Error}", retryCount, exception.Message);
                    });
        }

        public void PublishEvent(string messageJson, string queueName)
        {
            _retryPolicy.Execute(() => 
            {
                var factory = new ConnectionFactory { HostName = _config["RabbitMQ:HostName"] ?? "localhost" };
                using var connection = factory.CreateConnection();
                using var channel = connection.CreateModel();
                
                channel.QueueDeclare(queue: queueName, durable: true, exclusive: false, autoDelete: false, arguments: null);
                
                var body = Encoding.UTF8.GetBytes(messageJson);
                var properties = channel.CreateBasicProperties();
                properties.Persistent = true;

                channel.BasicPublish(exchange: "", routingKey: queueName, basicProperties: properties, body: body);
                
                _logger.LogInformation("Evento publicado com sucesso na fila {QueueName}: {Message}", queueName, messageJson);
            });
        }
    }
}

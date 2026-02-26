using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OrderProcessor.Data;

namespace OrderProcessor.Services
{
    public class OutboxWorker : BackgroundService
    {
        private readonly ILogger<OutboxWorker> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly IRabbitMqPublisher _publisher;

        public OutboxWorker(ILogger<OutboxWorker> logger, IServiceProvider serviceProvider, IRabbitMqPublisher publisher)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            _publisher = publisher;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                    var pendingEvents = dbContext.OutboxEvents
                        .Where(e => !e.Processed)
                        .OrderBy(e => e.CreatedAt)
                        .ToList();

                    foreach (var evt in pendingEvents)
                    {
                        try
                        {
                            _publisher.PublishEvent(evt.Payload, "orders_queue");
                            evt.Processed = true;
                            evt.ProcessedAt = DateTime.UtcNow;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Erro no envio pelo OutboxWorker do evento {EventId}", evt.Id);
                            // Interrompe processamento atual se o RabbitMQ estiver fora, 
                            // para não descartar/atualizar outros. 
                            // Tenta de novo no próximo ciclo de 5s.
                            break; 
                        }
                    }

                    if (pendingEvents.Any(e => e.Processed))
                    {
                        await dbContext.SaveChangesAsync(stoppingToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Erro no loop principal do OutboxWorker.");
                }

                await Task.Delay(5000, stoppingToken);
            }
        }
    }
}

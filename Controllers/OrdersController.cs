using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderProcessor.Data;
using OrderProcessor.Models;

namespace OrderProcessor.Controllers
{
    [ApiController]
    [Route("orders")]
    public class OrdersController : ControllerBase
    {
        private readonly AppDbContext _dbContext;
        private readonly ILogger<OrdersController> _logger;

        public OrdersController(AppDbContext dbContext, ILogger<OrdersController> logger)
        {
            _dbContext = dbContext;
            _logger = logger;
        }

        [HttpPost]
        public async Task<IActionResult> CreateOrder([FromBody] OrderRequest request)
        {
            _logger.LogInformation("Recebendo requisição para novo pedido: {OrderId}", request.OrderId);

            // 1. Evitar processamento duplicado (Idempotência) verificando no banco
            var existingOrder = await _dbContext.Orders.FirstOrDefaultAsync(o => o.OrderId == request.OrderId);
            if (existingOrder != null)
            {
                _logger.LogWarning("Pedido {OrderId} já processado anteriormente. Retornando 409 Conflict.", request.OrderId);
                return Conflict(new { message = "Id do pedido já processado." });
            }

            var order = new Order
            {
                OrderId = request.OrderId,
                Amount = request.Amount,
                CreatedAt = DateTime.UtcNow
            };

            var eventPayload = JsonSerializer.Serialize(new { order.OrderId, order.Amount, order.CreatedAt });
            var outboxEvent = new OutboxEvent
            {
                EventType = "OrderCreated",
                Payload = eventPayload
            };

            // Garantimos Consistência usando Transaction: Salva dados E evento na mesma transação
            using var transaction = await _dbContext.Database.BeginTransactionAsync();
            try
            {
                _dbContext.Orders.Add(order);
                _dbContext.OutboxEvents.Add(outboxEvent);
                
                await _dbContext.SaveChangesAsync();
                await transaction.CommitAsync();
                
                _logger.LogInformation("Pedido {OrderId} persistido no banco e evento salvo no Outbox com sucesso.", order.OrderId);

                return Ok(order);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Erro ao processar pedido {OrderId}.", request.OrderId);
                return StatusCode(500, new { message = "Erro interno no processamento." });
            }
        }
    }
}

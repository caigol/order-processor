# Teste Técnico - Caique Silva Pereira

## Cenário do Desafio

Desenvolver um microserviço chamado OrderProcessor responsável por receber pedidos via API, persistir dados e publicar evento no RabbitMQ.

**POST** `/orders`
```json
{
  "orderId": "guid",
  "amount": 100
}
```

## Requisitos Técnicos

- ASP.NET Core
- Persistência (SQL Server ou MongoDB)
- Publicação de evento em RabbitMQ
- Implementação de retry simples
- Logging estruturado

## Perguntas Conceituais

### 1) Como evitar processamento duplicado?
**Resposta:** Através da adoção da **Idempotência**.
Isso significa que o serviço deve conseguir receber a mesma requisição *n* vezes sem causar efeitos colaterais após a primeira execução bem-sucedida. Na nossa implementação (no `OrdersController`), verificamos no banco de dados se já existe um pedido usando o `OrderId` da requisição (que foi gerado pelo cliente antes da chamada). 
- Caso o pedido já exista, sabemos que se trata de uma retentativa de uma chamada que sofreu falha de rede na ponta do cliente etc., e imediatamente rejeitamos retornando `409 Conflict` (ou poderíamos retornar `200 OK` novamente) sem reinserir no banco, e evitamos disparar um segundo evento ao RabbitMQ:
```csharp
var existingOrder = await _dbContext.Orders.FirstOrDefaultAsync(o => o.OrderId == request.OrderId);
if (existingOrder != null) { return Conflict(new { message = "Id do pedido já processado." }); }
```
Do lado de quem consome do RabbitMQ (o Consumer), seria exigido o mesmo conceito: registrar qual Id de mensagem já foi processado ou usar *Upsert* no banco de leitura.

### 2) O que fazer se o RabbitMQ estiver indisponível?
**Resposta:** Devemos ter duas camadas de segurança: uma de **Retentativa Inteligente (Retry Policies)** e outra baseada em **Outbox Pattern**.
No código, foi adicionado o pacote **Polly** no `RabbitMqPublisher` para fazer *Exponential Backoff*. Isso significa que, se a primeira tentativa de publicação falhar por indisponibilidade repentina de rede, ele retentará algumas vezes (dando um tempo exponencial entre as tentativas).
Porém, e se o RabbitMQ estiver fora do ar por horas? É aí que entra o **OutboxWorker**. Se a comunicação com a fila falhar no decorrer do processamento, nosso cliente da API **não receberá erro**, pois nós não tentamos publicar na fila diretamente de forma síncrona impedindo a requisição web de terminar. O trabalhador em background (`OutboxWorker`) é quem vai processar a publicação. Se persistir inativo, a rotina interromperá as tentativas de publicar os eventos e não marcará a mensagem como `Processed = true`. No próximo ciclo de 5 segundos, ela voltará a tentar com segurança, não descartando nossas mensagens à toa.

### 3) Como garantir consistência entre banco e fila?
**Resposta:** Utilizando o **Transactional Outbox Pattern** (o padrão Caixa de Saída Transacional).
O maior problema de microsserviços é a chamada *"Double Write"* (gravar no DB e depois na fila em sistemas distribuídos). Se salvamos no banco e a aplicação fecha bruscamente antes do envio à fila, temos inconsistência (o dado existe no banco mas os outros serviços nunca saberão). 
A nossa garantia ocorre no `OrdersController`: abrimos uma transação no banco de dados (`using var transaction = await _dbContext.Database.BeginTransactionAsync()`) e realizamos o `Add` das duas entidades de interesse de modo coeso:
1. O Pedido físico no banco (`_dbContext.Orders.Add(order)`)
2. Um Evento de outbox (`_dbContext.OutboxEvents.Add(outboxEvent)`)

Quando executamos o `CommitAsync()`, o banco garante a **Atomicidade** (ACID): ou as duas coisas são gravadas na mesma fração de segundo, ou as duas coisas revertem juntas (*Rollback*). A responsabilidade de publicar na fila é delegada única e exclusivamente ao serviço secundário em background `OutboxWorker` (focado só em observar a tabela OutboxEvents). Eventos são guardados em segurança de modo transacional, fazendo com que o banco e a fila mantenham consistência 100% das vezes.

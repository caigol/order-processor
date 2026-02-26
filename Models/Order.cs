using System;

namespace OrderProcessor.Models
{
    public class Order
    {
        public Guid OrderId { get; set; }
        public decimal Amount { get; set; }
        public DateTime CreatedAt { get; set; }
    }
    
    public class OrderRequest
    {
        public Guid OrderId { get; set; }
        public decimal Amount { get; set; }
    }
}

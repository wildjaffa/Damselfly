using Damselfly.PaymentProcessing.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Damselfly.PaymentProcessing.PaymentProcessors
{
    public class PaymentProcessorFactory(PayPalPaymentProcessor payPalPaymentProcessor): IPaymentProcessorFactory
    {
        
        private List<IPaymentProcessor> _paymentProcessors = new() 
        { 
            payPalPaymentProcessor,
        };
        
        public IPaymentProcessor CreatePaymentProcessor(PaymentProcessorEnum paymentProcessorName)
        {
            var paymentProcessor = _paymentProcessors.FirstOrDefault(x => x.CanHandle(paymentProcessorName));
            if(paymentProcessor == null)
            {
                throw new Exception($"Payment processor {paymentProcessorName} not found");
            }
            return paymentProcessor;
        }
    }
}
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Damselfly.PaymentProcessing.Models.PayPal
{
    public class Paypal
    {
        [JsonProperty("experience_context")]
        public ExperienceContext ExperienceContext { get; set; }
    }
}

using Azure.Messaging.ServiceBus;
using System;
using System.Collections.Generic;
using System.Text;

namespace Cpi.Preprocessor.Core.Lead
{
    /// <summary>
    /// ServiceBusClient for receiving messages (listener)
    /// </summary>
    public class ListenerServiceBusClient
    {
        public ServiceBusClient Client { get; }

        public ListenerServiceBusClient(ServiceBusClient client)
        {
            Client = client;
        }
    }

    /// <summary>
    /// ServiceBusClient for sending messages (sender)
    /// </summary>
    public class SenderServiceBusClient
    {
        public ServiceBusClient Client { get; }

        public SenderServiceBusClient(ServiceBusClient client)
        {
            Client = client;
        }
    }
}

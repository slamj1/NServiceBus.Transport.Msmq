namespace NServiceBus.Transport.Msmq
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Threading.Tasks;
    using System.Transactions;
    using Performance.TimeToBeReceived;
    using Routing;
    using Settings;
    using Support;
    using Transport;

    class MsmqTransportInfrastructure : TransportInfrastructure
    {
        public MsmqTransportInfrastructure(ReadOnlySettings settings)
        {
            this.settings = settings;
        }

        public override IEnumerable<Type> DeliveryConstraints { get; } = new[]
        {
            typeof(DiscardIfNotReceivedBefore),
            typeof(NonDurableDelivery)
        };

        public override TransportTransactionMode TransactionMode { get; } = TransportTransactionMode.TransactionScope;
        public override OutboundRoutingPolicy OutboundRoutingPolicy { get; } = new OutboundRoutingPolicy(OutboundRoutingType.Unicast, OutboundRoutingType.Unicast, OutboundRoutingType.Unicast);

        ReceiveStrategy SelectReceiveStrategy(TransportTransactionMode minimumConsistencyGuarantee, TransactionOptions transactionOptions)
        {
            switch (minimumConsistencyGuarantee)
            {
                case TransportTransactionMode.TransactionScope:
                    return new TransactionScopeStrategy(transactionOptions, new MsmqFailureInfoStorage(1000));
                case TransportTransactionMode.SendsAtomicWithReceive:
                    return new SendsAtomicWithReceiveNativeTransactionStrategy(new MsmqFailureInfoStorage(1000));
                case TransportTransactionMode.ReceiveOnly:
                    return new ReceiveOnlyNativeTransactionStrategy(new MsmqFailureInfoStorage(1000));
                case TransportTransactionMode.None:
                    return new NoTransactionStrategy();
                default:
                    throw new NotSupportedException($"TransportTransactionMode {minimumConsistencyGuarantee} is not supported by the MSMQ transport");
            }
        }

        public override EndpointInstance BindToLocalEndpoint(EndpointInstance instance) => instance.AtMachine(RuntimeEnvironment.MachineName);

        public override string ToTransportAddress(LogicalAddress logicalAddress)
        {
            string machine;
            if (!logicalAddress.EndpointInstance.Properties.TryGetValue("machine", out machine))
            {
                machine = RuntimeEnvironment.MachineName;
            }
            string queueName;
            if (!logicalAddress.EndpointInstance.Properties.TryGetValue("queue", out queueName))
            {
                queueName = logicalAddress.EndpointInstance.Endpoint;
            }
            var queue = new StringBuilder(queueName);
            if (logicalAddress.EndpointInstance.Discriminator != null)
            {
                queue.Append("-" + logicalAddress.EndpointInstance.Discriminator);
            }
            if (logicalAddress.Qualifier != null)
            {
                queue.Append("." + logicalAddress.Qualifier);
            }
            return queue + "@" + machine;
        }

        public override string MakeCanonicalForm(string transportAddress)
        {
            return MsmqAddress.Parse(transportAddress).ToString();
        }

        public override TransportReceiveInfrastructure ConfigureReceiveInfrastructure()
        {
            CheckMachineNameForCompliance.Check();

            // The following check avoids creating some sub-queues, if the endpoint sub queue has the capability to exceed the max length limitation for queue format name. 
            var bindings = settings.Get<QueueBindings>();
            foreach (var queue in bindings.ReceivingAddresses)
            {
                CheckEndpointNameComplianceForMsmq.Check(queue);
            }

            MsmqScopeOptions scopeOptions;

            if (!settings.TryGet(out scopeOptions))
            {
                scopeOptions = new MsmqScopeOptions();
            }

            var msmqSettings = settings.Get<MsmqSettings>();

            return new TransportReceiveInfrastructure(
                () => new MessagePump(guarantee => SelectReceiveStrategy(guarantee, scopeOptions.TransactionOptions)),
                () => new MsmqQueueCreator(msmqSettings.UseTransactionalQueues),
                () =>
                {
                    foreach (var address in bindings.ReceivingAddresses)
                    {
                        QueuePermissions.CheckQueue(address);
                    }
                    return Task.FromResult(StartupCheckResult.Success);
                });
        }

        public override TransportSendInfrastructure ConfigureSendInfrastructure()
        {
            CheckMachineNameForCompliance.Check();

            if (!settings.TryGet("msmqLabelGenerator", out Func<IReadOnlyDictionary<string, string>, string> messageLabelGenerator))
            {
                messageLabelGenerator = headers => string.Empty;
            }

            var msmqSettings = settings.Get<MsmqSettings>();

            return new TransportSendInfrastructure(
                () => new MsmqMessageDispatcher(msmqSettings, messageLabelGenerator),
                () =>
                {
                    var bindings = settings.Get<QueueBindings>();

                    foreach (var address in bindings.SendingAddresses)
                    {
                        QueuePermissions.CheckQueue(address);
                    }

                    var result = new MsmqTimeToBeReceivedOverrideCheck(settings).CheckTimeToBeReceivedOverrides();
                    return Task.FromResult(result);
                });
        }

        public override TransportSubscriptionInfrastructure ConfigureSubscriptionInfrastructure()
        {
            throw new NotImplementedException("MSMQ does not support native pub/sub.");
        }

        ReadOnlySettings settings;
    }
}
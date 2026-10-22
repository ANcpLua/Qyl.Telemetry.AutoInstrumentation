using System.Diagnostics;
using Qyl.Telemetry.AutoInstrumentation.Internal;

namespace Qyl.Telemetry.AutoInstrumentation.GeneratedCode;

/// <summary>Confluent.Kafka producer and consumer spans.</summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
[QylIntegration(QylAutoInstrumentationIds.Kafka)]
[QylIntercept("Confluent.Kafka.IProducer`2", "Produce", "ProduceAsync", Shape = QylShapes.KafkaProduce, Start = nameof(Send))]
[QylIntercept("Confluent.Kafka.IConsumer`2", "Consume", Shape = QylShapes.KafkaConsume, Start = nameof(Receive))]
public static class QylInterceptedKafka
{
    /// <summary>Starts the producer span for a topic or topic partition.</summary>
    public static Activity? Send(
        [QylFromArgument(0, Type = "string")]
        [QylFromArgument(0, Type = "Confluent.Kafka.TopicPartition", Convert = "{0}.Topic")]
        string? topic,
        [QylFromArgument(0, Type = "Confluent.Kafka.TopicPartition", Convert = "{0}.Partition.IsSpecial ? null : (int?){0}.Partition.Value")]
        int? partitionId)
        => QylMessagingActivityPolicy.StartKafkaProducerActivity(topic, partitionId);

    /// <summary>Starts the pull-based receive span.</summary>
    public static Activity? Receive()
        => QylMessagingActivityPolicy.StartKafkaConsumerActivity();
}

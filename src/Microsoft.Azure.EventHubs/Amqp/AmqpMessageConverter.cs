﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.Azure.EventHubs.Amqp
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Runtime.Serialization;
    using Microsoft.Azure.Amqp;
    using Microsoft.Azure.Amqp.Encoding;
    using Microsoft.Azure.Amqp.Framing;

    static class AmqpMessageConverter
    {
        const SectionFlag ClientAmqpPropsSetOnSendToEventHub =
            SectionFlag.ApplicationProperties |
            SectionFlag.MessageAnnotations |
            SectionFlag.DeliveryAnnotations |
            SectionFlag.Properties;

        public const string EnqueuedTimeUtcName = "x-opt-enqueued-time";
        public const string SequenceNumberName = "x-opt-sequence-number";
        public const string OffsetName = "x-opt-offset";

        public const string PublisherName = "x-opt-publisher";
        public const string PartitionKeyName = "x-opt-partition-key";
        public const string TimeSpanName = AmqpConstants.Vendor + ":timespan";
        public const string UriName = AmqpConstants.Vendor + ":uri";
        public const string DateTimeOffsetName = AmqpConstants.Vendor + ":datetime-offset";

        public static EventData AmqpMessageToEventData(AmqpMessage amqpMessage)
        {
            if (amqpMessage == null)
            {
                throw Fx.Exception.ArgumentNull("amqpMessage");
            }

            EventData eventData = new EventData(StreamToBytes(amqpMessage.BodyStream));
            UpdateEventDataHeaderAndProperties(amqpMessage, eventData);
            return eventData;
        }

        public static AmqpMessage EventDatasToAmqpMessage(IEnumerable<EventData> eventDatas, string partitionKey)
        {
            if (eventDatas == null)
            {
                throw new ArgumentNullException(nameof(eventDatas));
            }

            AmqpMessage returnMessage = null;
            var dataCount = eventDatas.Count();
            if (dataCount > 1)
            {
                IList<Data> bodyList = new List<Data>();
                EventData firstEvent = null;
                foreach (EventData data in eventDatas)
                {
                    if (firstEvent == null)
                    {
                        firstEvent = data;
                    }

                    // Create AMQP message if not created yet.
                    AmqpMessage amqpMessage = data.AmqpMessage;
                    if (amqpMessage == null)
                    {
                        amqpMessage = EventDataToAmqpMessage(data);
                    }

                    UpdateAmqpMessagePartitionKey(amqpMessage, partitionKey);
                    amqpMessage.Batchable = true;

                    if ((amqpMessage.Sections & ClientAmqpPropsSetOnSendToEventHub) == 0 && data.Body.Array == null)
                    {
                        throw new InvalidOperationException(Resources.CannotSendAnEmptyEvent.FormatForUser(data.GetType().Name));
                    }

                    ArraySegment<byte> buffer = StreamToBytes(amqpMessage.ToStream());
                    bodyList.Add(new Data { Value = buffer });
                }

                returnMessage = AmqpMessage.Create(bodyList);
                returnMessage.MessageFormat = AmqpConstants.AmqpBatchedMessageFormat;
                UpdateAmqpMessageHeadersAndProperties(returnMessage, null, firstEvent, copyUserProperties: false);
            }
            else if (dataCount == 1) // ??? can't be null
            {
                var data = eventDatas.First();

                // Create AMQP message if not created yet.
                returnMessage = data.AmqpMessage;
                if (returnMessage == null)
                {
                    returnMessage = EventDataToAmqpMessage(data);
                }

                if ((returnMessage.Sections & ClientAmqpPropsSetOnSendToEventHub) == 0 && data.Body.Array == null)
                {
                    throw new InvalidOperationException(Resources.CannotSendAnEmptyEvent.FormatForUser(data.GetType().Name));
                }
            }

            returnMessage.Batchable = true;
            UpdateAmqpMessagePartitionKey(returnMessage, partitionKey);

            return returnMessage;
        }

        public static AmqpMessage EventDataToAmqpMessage(EventData eventData)
        {
            AmqpMessage amqpMessage  = AmqpMessage.Create(new Data { Value = eventData.Body });
            UpdateAmqpMessageHeadersAndProperties(amqpMessage, null, eventData, true);

            return amqpMessage;
        }

        static void UpdateAmqpMessageHeadersAndProperties(
            AmqpMessage message,
            string publisher,
            EventData eventData,
            bool copyUserProperties = true)
        {
            if (!string.IsNullOrEmpty(publisher))
            {
                message.MessageAnnotations.Map[PublisherName] = publisher;
            }

            if (copyUserProperties && eventData.Properties != null && eventData.Properties.Count > 0)
            {
                if (message.ApplicationProperties == null)
                {
                    message.ApplicationProperties = new ApplicationProperties();
                }

                foreach (var pair in eventData.Properties)
                {
                    object amqpObject;
                    if (TryGetAmqpObjectFromNetObject(pair.Value, MappingType.ApplicationProperty, out amqpObject))
                    {
                        message.ApplicationProperties.Map[pair.Key] = amqpObject;
                    }
                }
            }
        }

        static void UpdateAmqpMessagePartitionKey(AmqpMessage message, string partitionKey)
        {
            if (partitionKey != null)
            {
                message.MessageAnnotations.Map[PartitionKeyName] = partitionKey;
            }
        }

        static void UpdateEventDataHeaderAndProperties(AmqpMessage amqpMessage, EventData data)
        {
            SectionFlag sections = amqpMessage.Sections;
            if ((sections & SectionFlag.MessageAnnotations) != 0)
            {
                // services (e.g. IoTHub) assumes that all Amqp message annotation will get bubbled up so we will cycle
                // through the list and add them to system properties as well.
                foreach (var keyValuePair in amqpMessage.MessageAnnotations.Map)
                {
                    if (data.Properties == null)
                    {
                        data.Properties = new Dictionary<string, object>();
                    }

                    object netObject;
                    if (TryGetNetObjectFromAmqpObject(keyValuePair.Value, MappingType.ApplicationProperty, out netObject))
                    {
                        data.Properties[keyValuePair.Key.ToString()] = netObject;
                    }
                }

                // Custom override for EventHub scenario. Note that these 
                // "can" override existing properties, which is intentional as
                // in the EH these system properties take precedence over Amqp data.
                //string publisher;
                //if (amqpMessage.MessageAnnotations.Map.TryGetValue<string>(PublisherName, out publisher))
                //{
                //    data.Publisher = publisher;
                //}

//#if DEBUG
//                short partitionId;
//                if (amqpMessage.MessageAnnotations.Map.TryGetValue<short>(PartitionIdName, out partitionId))
//                {
//                    data.PartitionId = partitionId;
//                }
//#endif

                if (data.SystemProperties == null)
                {
                    data.SystemProperties = new EventData.SystemPropertiesCollection();
                }

                string partitionKey;
                if (amqpMessage.MessageAnnotations.Map.TryGetValue(AmqpMessageConverter.PartitionKeyName, out partitionKey))
                {
                    data.SystemProperties.PartitionKey = partitionKey;
                }

                DateTime enqueuedTimeUtc;
                if (amqpMessage.MessageAnnotations.Map.TryGetValue(AmqpMessageConverter.EnqueuedTimeUtcName, out enqueuedTimeUtc))
                {
                    data.SystemProperties.EnqueuedTimeUtc = enqueuedTimeUtc;
                }

                long sequenceNumber;
                if (amqpMessage.MessageAnnotations.Map.TryGetValue(AmqpMessageConverter.SequenceNumberName, out sequenceNumber))
                {
                    data.SystemProperties.SequenceNumber = sequenceNumber;
                }

                string offset;
                if (amqpMessage.MessageAnnotations.Map.TryGetValue(AmqpMessageConverter.OffsetName, out offset))
                {
                    data.SystemProperties.Offset = offset;
                }
            }

            if ((sections & SectionFlag.ApplicationProperties) != 0)
            {
                foreach (KeyValuePair<MapKey, object> pair in amqpMessage.ApplicationProperties.Map)
                {
                    if (data.Properties == null)
                    {
                        data.Properties = new Dictionary<string, object>();
                    }

                    object netObject;
                    if (TryGetNetObjectFromAmqpObject(pair.Value, MappingType.ApplicationProperty, out netObject))
                    {
                        data.Properties[pair.Key.ToString()] = netObject;
                    }
                }
            }
        }

        static ArraySegment<byte> StreamToBytes(Stream stream)
        {
            ArraySegment<byte> buffer;

            if (stream == null)
            {
                buffer = new ArraySegment<byte>();
            }
            else
            {
                MemoryStream memoryStream = new MemoryStream(512);
                stream.CopyTo(memoryStream, 512);
                buffer = new ArraySegment<byte>(memoryStream.ToArray());
            }

            return buffer;
        }

        static bool TryGetAmqpObjectFromNetObject(object netObject, MappingType mappingType, out object amqpObject)
        {
            amqpObject = null;
            if (netObject == null)
            {
                return true;
            }

            switch (SerializationUtilities.GetTypeId(netObject))
            {
                case PropertyValueType.Byte:
                case PropertyValueType.SByte:
                case PropertyValueType.Int16:
                case PropertyValueType.Int32:
                case PropertyValueType.Int64:
                case PropertyValueType.UInt16:
                case PropertyValueType.UInt32:
                case PropertyValueType.UInt64:
                case PropertyValueType.Single:
                case PropertyValueType.Double:
                case PropertyValueType.Boolean:
                case PropertyValueType.Decimal:
                case PropertyValueType.Char:
                case PropertyValueType.Guid:
                case PropertyValueType.DateTime:
                case PropertyValueType.String:
                    amqpObject = netObject;
                    break;
                case PropertyValueType.Stream:
                    if (mappingType == MappingType.ApplicationProperty)
                    {
                        amqpObject = StreamToBytes((Stream)netObject);
                    }
                    break;
                case PropertyValueType.Uri:
                    amqpObject = new DescribedType((AmqpSymbol)UriName, ((Uri)netObject).AbsoluteUri);
                    break;
                case PropertyValueType.DateTimeOffset:
                    amqpObject = new DescribedType((AmqpSymbol)DateTimeOffsetName, ((DateTimeOffset)netObject).UtcTicks);
                    break;
                case PropertyValueType.TimeSpan:
                    amqpObject = new DescribedType((AmqpSymbol)TimeSpanName, ((TimeSpan)netObject).Ticks);
                    break;
                case PropertyValueType.Unknown:
                    if (netObject is Stream)
                    {
                        if (mappingType == MappingType.ApplicationProperty)
                        {
                            amqpObject = StreamToBytes((Stream)netObject);
                        }
                    }
                    else if (mappingType == MappingType.ApplicationProperty)
                    {
                        throw Fx.Exception.AsError(new SerializationException(Resources.FailedToSerializeUnsupportedType.FormatForUser(netObject.GetType().FullName)));
                    }
                    else if (netObject is byte[])
                    {
                        amqpObject = new ArraySegment<byte>((byte[])netObject);
                    }
                    else if (netObject is IList)
                    {
                        // Array is also an IList
                        amqpObject = netObject;
                    }
                    else if (netObject is IDictionary)
                    {
                        amqpObject = new AmqpMap((IDictionary)netObject);
                    }
                    break;
            }

            return amqpObject != null;
        }

        static bool TryGetNetObjectFromAmqpObject(object amqpObject, MappingType mappingType, out object netObject)
        {
            netObject = null;
            if (amqpObject == null)
            {
                return true;
            }

            switch (SerializationUtilities.GetTypeId(amqpObject))
            {
                case PropertyValueType.Byte:
                case PropertyValueType.SByte:
                case PropertyValueType.Int16:
                case PropertyValueType.Int32:
                case PropertyValueType.Int64:
                case PropertyValueType.UInt16:
                case PropertyValueType.UInt32:
                case PropertyValueType.UInt64:
                case PropertyValueType.Single:
                case PropertyValueType.Double:
                case PropertyValueType.Boolean:
                case PropertyValueType.Decimal:
                case PropertyValueType.Char:
                case PropertyValueType.Guid:
                case PropertyValueType.DateTime:
                case PropertyValueType.String:
                    netObject = amqpObject;
                    break;
                case PropertyValueType.Unknown:
                    if (amqpObject is AmqpSymbol)
                    {
                        netObject = ((AmqpSymbol)amqpObject).Value;
                    }
                    else if (amqpObject is ArraySegment<byte>)
                    {
                        ArraySegment<byte> binValue = (ArraySegment<byte>)amqpObject;
                        if (binValue.Count == binValue.Array.Length)
                        {
                            netObject = binValue.Array;
                        }
                        else
                        {
                            byte[] buffer = new byte[binValue.Count];
                            Buffer.BlockCopy(binValue.Array, binValue.Offset, buffer, 0, binValue.Count);
                            netObject = buffer;
                        }
                    }
                    else if (amqpObject is DescribedType)
                    {
                        DescribedType describedType = (DescribedType)amqpObject;
                        if (describedType.Descriptor is AmqpSymbol)
                        {
                            AmqpSymbol symbol = (AmqpSymbol)describedType.Descriptor;
                            if (symbol.Equals((AmqpSymbol)UriName))
                            {
                                netObject = new Uri((string)describedType.Value);
                            }
                            else if (symbol.Equals((AmqpSymbol)TimeSpanName))
                            {
                                netObject = new TimeSpan((long)describedType.Value);
                            }
                            else if (symbol.Equals((AmqpSymbol)DateTimeOffsetName))
                            {
                                netObject = new DateTimeOffset(new DateTime((long)describedType.Value, DateTimeKind.Utc));
                            }
                        }
                    }
                    else if (mappingType == MappingType.ApplicationProperty)
                    {
                        throw Fx.Exception.AsError(new SerializationException(Resources.FailedToSerializeUnsupportedType.FormatForUser(amqpObject.GetType().FullName)));
                    }
                    else if (amqpObject is AmqpMap)
                    {
                        AmqpMap map = (AmqpMap)amqpObject;
                        Dictionary<string, object> dictionary = new Dictionary<string, object>();
                        foreach (var pair in map)
                        {
                            dictionary.Add(pair.Key.ToString(), pair.Value);
                        }

                        netObject = dictionary;
                    }
                    else
                    {
                        netObject = amqpObject;
                    }
                    break;
            }

            return netObject != null;
        }


    }
}

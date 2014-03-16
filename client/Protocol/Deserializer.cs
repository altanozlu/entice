﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Web.Script.Serialization;
using Protocol.Messages;
using Protocol.Messages.Update;

namespace Protocol
{
        public class Deserializer
        {
                private readonly ConcurrentDictionary<Type, KeyValuePair<DateTime, Message>> _lastMessages = new ConcurrentDictionary<Type, KeyValuePair<DateTime, Message>>();
                private readonly Dictionary<Type, Delegate> _messageHandlers;
                private readonly Dictionary<string, Type> _typedMessages;
                private string _remainingData = "";

                public Deserializer()
                {
                        _typedMessages = new Dictionary<string, Type>();
                        _messageHandlers = new Dictionary<Type, Delegate>();

                        IEnumerable<Type> typedMessagesTypes = Assembly.GetExecutingAssembly().GetTypes().Where(t => t.IsSubclassOf(typeof (Message)));

                        foreach (Type type in typedMessagesTypes)
                        {
                                _typedMessages.Add(type.Name, type);
                        }
                }

                public int WaitForMessage<T1, T2>(int timeout, out T1 messagePossibility1, out T2 messagePossibility2)
                        where T1 : Message
                        where T2 : Message
                {
                        DateTime timeStamp = DateTime.Now;

                        messagePossibility1 = null;
                        messagePossibility2 = null;

                        while (DateTime.Now.Subtract(timeStamp).TotalMilliseconds < timeout)
                        {
                                if (MessageAvailable(timeStamp, out messagePossibility1)) return 1;
                                if (MessageAvailable(timeStamp, out messagePossibility2)) return 2;

                                Thread.Sleep(1);
                        }

                        return 0;
                }

                public bool WaitForMessage<T>(int timeout, out T message) where T : Message
                {
                        DateTime timeStamp = DateTime.Now;

                        message = null;

                        while (DateTime.Now.Subtract(timeStamp).TotalMilliseconds < timeout)
                        {
                                if (MessageAvailable(timeStamp, out message)) return true;

                                Thread.Sleep(1);
                        }

                        return false;
                }

                private bool MessageAvailable<T>(DateTime timeStamp, out T message) where T : Message
                {
                        KeyValuePair<DateTime, Message> lastMessage;
                        if (_lastMessages.TryGetValue(typeof (T), out lastMessage))
                        {
                                if (lastMessage.Key > timeStamp)
                                {
                                        message = (T) lastMessage.Value;
                                        return true;
                                }
                        }

                        message = null;
                        return false;
                }

                public void RegisterMessageHandler<T>(Action<T, Socket> handler) where T : Message
                {
                        if (_messageHandlers.ContainsKey(typeof (T)))
                        {
                                _messageHandlers[typeof (T)] = handler;
                        }
                        else
                        {
                                _messageHandlers.Add(typeof (T), handler);
                        }
                }

                public void Deserialize(byte[] data, Socket socket = null)
                {
                        var r = new StreamReader(new MemoryStream(data));
                        string t = r.ReadToEnd();

                        string dataToProcess = _remainingData + (t[0] > 0 ? t : t.Substring(4));

                        while (dataToProcess.Length > 0)
                        {
                                bool deserializedSomethingSuccessfully = false;

                                for (int i = 0; i < dataToProcess.Length; i++)
                                {
                                        if (dataToProcess[i] != '}') continue;

                                        string messagePart = dataToProcess.Substring(0, i + 1);

                                        Message message = DeserializeMessage(messagePart);

                                        if (message != null)
                                        {
                                                dataToProcess = (i + 5 <= dataToProcess.Length ? dataToProcess.Substring(i + 5) : "");

                                                if (!(message is UpdateCommand)) Console.Write("received " + message);

                                                Delegate handler;
                                                if (_messageHandlers.TryGetValue(message.GetType(), out handler))
                                                {
                                                        handler.DynamicInvoke(message, socket);
                                                }

                                                deserializedSomethingSuccessfully = true;

                                                break;
                                        }
                                }

                                if (!deserializedSomethingSuccessfully)
                                {
                                        _remainingData = dataToProcess;
                                        return;
                                }
                        }

                        _remainingData = "";
                }

                private Message DeserializeMessage(string text)
                {
                        var serializer = new JavaScriptSerializer();
                        serializer.RegisterConverters(new[] {new CustomJsonResolver()});

                        Message message;
                        try
                        {
                                message = serializer.Deserialize<Message>(text);
                        }
                        catch (Exception)
                        {
                                return null;
                        }

                        var newValue = new KeyValuePair<DateTime, Message>(DateTime.Now, message);
                        _lastMessages.AddOrUpdate(message.GetType(), newValue, (k, v) => newValue);

                        return message;
                }
        }
}
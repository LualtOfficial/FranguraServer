using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using FiguraServer.Server.WebSockets.Messages;

namespace FiguraServer.Server.WebSockets
{
    public class PubSubManager
    {
        public static ConcurrentDictionary<Guid, PubSubChannel> currentChannels = new ConcurrentDictionary<Guid, PubSubChannel>();

        private static PubSubChannel MakeChannel(Guid id)
        {
            var ret = new PubSubChannel();
            ret.ownerID = id;
            ret.onEmptied = () => OnEmptied(id);
            return ret;
        }

        private static bool TryGetChannel(Guid myID, out PubSubChannel psc) => currentChannels.TryGetValue(myID, out psc);

        private static PubSubChannel GetChannel(Guid myID) => currentChannels.GetOrAdd(myID, MakeChannel);

        public static void Subscribe(Guid myID, Guid target) => GetChannel(myID).AddSubscription(target);

        public static void Unsubscribe(Guid myID, Guid target) => GetChannel(myID).RemoveSubscription(target);

        public static void UnsubscribeAll(Guid myID) => GetChannel(myID).ClearSubs();

        public static void SendMessage(Guid myID, MessageSender message, Action onFinished = null) => GetChannel(myID).SendMessage(message, onFinished);

        private static void OnEmptied(Guid myID) => currentChannels.TryRemove(myID, out var psc);

        public class PubSubChannel
        {
            public Guid ownerID;
            public bool isValid = true;
            public HashSet<Guid> subscriptionList = new HashSet<Guid>();

            private object lockObject = new object();
            private Task workTask = Task.CompletedTask;

            public Action onEmptied;

            //Adds a subscription
            public void AddSubscription(Guid id)
            {
                AddTask(() =>
                {
                    if (!isValid)
                        return;

                    Logger.LogMessage("Subscribing user " + ownerID + " to " + id);

                    subscriptionList.Add(id);
                });
            }

            //Removes a subscription
            public void RemoveSubscription(Guid id)
            {
                AddTask(() =>
                {
                    if (!isValid)
                        return;

                    subscriptionList.Remove(id);

                    Logger.LogMessage("Unsubscribing user " + ownerID + " from " + id);

                    if (subscriptionList.Count == 0)
                    {
                        isValid = false;
                        onEmptied?.Invoke();
                    }
                });
            }

            //Sends a message to all subscribed members
            public void SendMessage(MessageSender sender, Action onFinished = null)
            {
                AddTask(() =>
                {
                    if (!isValid)
                        return;

                    //Foreach subscription
                    foreach (var id in subscriptionList)
                    {
                        //If subscription is the owner of this channel, don't send a message.
                        if (id == ownerID)
                            continue;

                        Logger.LogMessage("Sending message " + sender.GetType().Name + " to user " + id);


                        //If the subscription has a channel.
                        if (TryGetChannel(id, out var psc))
                        {
                            //Add task to subscription.
                            psc.AddTask(() =>
                            {
                                if (!isValid)
                                    return;

                                //If subscription is also subscribed to this channel
                                if (psc.subscriptionList.Contains(ownerID))
                                {
                                    Logger.LogMessage("Sending message " + sender.GetType().Name + " to user " + id);
                                    if (WebSocketConnection.openedConnections.TryGetValue(id, out var conn))
                                    {
                                        conn.SendMessage(sender);
                                    }
                                }
                                else
                                {
                                    Logger.LogMessage("Failed to send message " + sender.GetType().Name + " to user " + id + ", not mutual subscription");
                                }
                            });
                        } else
                        {
                            Logger.LogMessage("User " + id + " has no channel open");
                        }
                    }

                    onFinished?.Invoke();
                });
            }

            public void Close()
            {
                AddTask(() =>
                {
                    isValid = false;
                });
            }

            //Adds a task to be done by this channel when available.
            private void AddTask(Action a)
            {
                lock (lockObject)
                {
                    if (!isValid)
                        return;

                    workTask = workTask.ContinueWith((t) => a());
                }
            }

            public void ClearSubs()
            {
                AddTask(() =>
                {
                    subscriptionList.Clear();
                    isValid = false;
                    onEmptied?.Invoke();

                    Logger.LogMessage("Cleared all subscriptions for user " + ownerID);
                });
            }
        }
    }
}

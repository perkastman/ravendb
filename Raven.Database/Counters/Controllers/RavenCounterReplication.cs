using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Data.Services.Client;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Formatting;
using System.Threading;
using System.Threading.Tasks;
using Raven.Abstractions;
using Raven.Abstractions.Connection;
using Raven.Abstractions.Data;
using Raven.Abstractions.Extensions;
using Raven.Abstractions.Logging;
using Raven.Abstractions.Replication;
using Raven.Abstractions.Util;
using Raven.Json.Linq;

namespace Raven.Database.Counters.Controllers
{
	public class RavenCounterReplication:IDisposable
	{
        private static readonly ILog log = LogManager.GetCurrentClassLogger();

        private readonly object waitForCounterUpdate = new object();
        private int actualWorkCounter = 0; // represents the number of changes in 
        private int replicatedWorkCounter = 0; // represents the last actualWorkCounter value that was checked in the last replication iteration
        private bool shouldPause = false;
        public bool IsRunning { get; private set; }
		private readonly ConcurrentDictionary<string, DestinationStats> destinationsFailureCount = 
			new ConcurrentDictionary<string, DestinationStats>(StringComparer.OrdinalIgnoreCase);
        private int replicationAttempts;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> activeReplicationTasks = new ConcurrentDictionary<string, SemaphoreSlim>();
        public readonly ConcurrentQueue<Task> activeTasks = new ConcurrentQueue<Task>();
        private HttpRavenRequestFactory httpRavenRequestFactory;

		public static string GetServerNameForWire(string server)
		{
			var uri = new Uri(server);
			return uri.Host + ":" + uri.Port;
		}

		private readonly CounterStorage storage;
		private readonly CancellationTokenSource cancellation;

		public RavenCounterReplication(CounterStorage storage)
		{
			this.storage = storage;
			this.storage.CounterUpdated += SignalCounterUpdate;
			cancellation = new CancellationTokenSource();
		}
        
	    public void StartReplication()
	    {
            var replicationTask = new Task(ReplicationAction, TaskCreationOptions.LongRunning);

            httpRavenRequestFactory = new HttpRavenRequestFactory { RequestTimeoutInMs = storage.ReplicationTimeoutInMs };
            replicationTask.Start();
	    }

	    private void ReplicationAction()
	    {
	        var runningBecauseOfDataModification = false;
            var timeToWaitInMinutes = TimeSpan.FromMinutes(5);

            NotifySiblings();

            while (!cancellation.IsCancellationRequested)
            {
                Replicate(runningBecauseOfDataModification);
                runningBecauseOfDataModification = WaitForCountersUpdate(timeToWaitInMinutes);
                timeToWaitInMinutes = runningBecauseOfDataModification ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(5);
            }

	        IsRunning = false;
	    }
        
	    public void SignalCounterUpdate()
	    {
	        lock (waitForCounterUpdate)
	        {
                Interlocked.Increment(ref actualWorkCounter);
	            Monitor.PulseAll(waitForCounterUpdate);
	        }
	    }

	    private bool WaitForCountersUpdate(TimeSpan timeout)
	    {
            if (Thread.VolatileRead(ref actualWorkCounter) != replicatedWorkCounter)
            {
                replicatedWorkCounter = actualWorkCounter;
                return true;
            } 
	        lock (waitForCounterUpdate)
	        {
                if (Thread.VolatileRead(ref actualWorkCounter) != replicatedWorkCounter)
                {
                    replicatedWorkCounter = actualWorkCounter;
                    return true;
                }

                log.Debug("No counter updates for counter storage {0} was found, will wait for updates", storage.Name);
                return Monitor.Wait(waitForCounterUpdate, timeout);
	        }
	    }
        public void Pause()
        {
            shouldPause = true;
        }

        public void Continue()
        {
            shouldPause = false;
        }


		private void RecordFailure(string url, string lastError)
		{
			var stats = destinationsFailureCount.GetOrAdd(url, new DestinationStats { Url = url });
			Interlocked.Increment(ref stats.FailureCountInternal);
			stats.LastFailureTimestamp = SystemTime.UtcNow;
			/*var stats = destinationStats.GetOrAdd(url, new DestinationStats { Url = url });
			Interlocked.Increment(ref stats.FailureCountInternal);
			stats.LastFailureTimestamp = SystemTime.UtcNow;
			if (string.IsNullOrWhiteSpace(lastError) == false)
				stats.LastError = lastError;
			*/
		}

        //Notifies servers which send us counters that we are back online
	    private void NotifySiblings() //TODO: implement
	    {
            /*try
        {
            collection.TryTake(out connectionStringOptions, 15 * 1000, docDb.WorkContext.CancellationToken);
            if (connectionStringOptions == null)
                return;
        }
        catch (Exception e)
        {
            log.ErrorException("Could not get connection string options to notify sibling servers about restart", e);
            return;
        }
        try
        {
            var url = connectionStringOptions.Url + "/replication/heartbeat?from=" + UrlEncodedServerUrl() + "&dbid=" + docDb.TransactionalStorage.Id;
            var request = httpRavenRequestFactory.Create(url, "POST", connectionStringOptions);
            request.WebRequest.ContentLength = 0;
            request.ExecuteRequest();
        }
        catch (Exception e)
        {
            log.WarnException("Could not notify " + connectionStringOptions.Url + " about sibling server being up & running", e);
        }*/
	    }
		
		private bool IsNotFailing(string destServerName, int currentReplicationAttempts)
        {
	        DestinationStats destinationStats;
			if (destinationsFailureCount.TryGetValue(destServerName, out destinationStats))
			{
				bool shouldReplicateTo = false;
				var failureCount = destinationStats.FailureCount;
				if (failureCount > 1000)
				{
					shouldReplicateTo = currentReplicationAttempts % 10 == 0;
				}
				if (failureCount > 100)
				{
					shouldReplicateTo = currentReplicationAttempts % 5 == 0;
				}
				if (failureCount > 10)
				{
					shouldReplicateTo = currentReplicationAttempts % 2 == 0;
				}
				log.Debug("Failure count for {0} is {1}, skipping replication: {2}",
							destServerName, failureCount, shouldReplicateTo == false);
				return shouldReplicateTo;
	        }
			return true;
        }

	    private bool ReplicateTo(string destinationUrl)
	    {
	        try
	        {
                var connectionStringOptions = new RavenConnectionStringOptions(); //todo: get connection string from counter storage for the server 'destinationUrl'
	            var url = string.Format("{0}/lastEtag/{1}", destinationUrl, GetServerNameForWire(storage.Name));
	            var request = httpRavenRequestFactory.Create(url, "GET", connectionStringOptions);
	            long etag = 0;
	            request.ExecuteRequest((TextReader etagString) => etag = long.Parse(etagString.ReadToEnd()));

	            var message = new ReplicationMessage {SendingServerName = storage.Name};
	            using (var reader = storage.CreateReader())
	            {
	                message.Counters = reader.GetCountersSinceEtag(etag).Take(10240).ToList(); //TODO: Capped this...how to get remaining values?
	            }
	            url = string.Format("{0}/replication", destinationUrl);

	            if (message.Counters.Count > 0)
	            {
	                request = httpRavenRequestFactory.Create(url, "POST", connectionStringOptions);
                    request.Write(message.GetRavenJObject());
	                request.ExecuteRequest();
	                return true;
	            }
	            return false;
	        }
	        catch (Exception ex)
	        {
	            log.ErrorException("Error occured replicating to: " + destinationUrl, ex);
	            return false;
	        }
	        finally
	        {
                var holder = activeReplicationTasks.GetOrAdd(destinationUrl, s => new SemaphoreSlim(0, 1));
                holder.Release();
	        }
	    }

		private void Replicate(bool runningBecauseOfDataModifications)
		{
			IsRunning = !shouldPause;
			if (IsRunning)
			{
				try
				{
				    var destinations = storage.Servers.Where(serverName => serverName != storage.Name).ToList();

				    if (destinations.Count > 0)
				    {
				        var currentReplicationAttempts = Interlocked.Increment(ref replicationAttempts);

				        var destinationForReplication = destinations.Where(
				            serverName => !runningBecauseOfDataModifications || IsNotFailing(serverName, currentReplicationAttempts));

				        var startedTasks = new List<Task>();

				        foreach (var destinationUrl in destinationForReplication)
				        {
				            var dest = destinationUrl;
				            var holder = activeReplicationTasks.GetOrAdd(dest, s => new SemaphoreSlim(1));
				            if (holder.Wait(0) == false)
                                continue;
				            var replicationTask = Task.Factory.StartNew(
				                async () =>
				                {
				                    //using (LogContext.WithDatabase(storage.Name)) //TODO: log with counter storage contexe
				                    //{
				                        try
				                        {
                                            if (ReplicateTo(dest)) SignalCounterUpdate();
				                        }
				                        catch (Exception e)
				                        {
				                            log.ErrorException("Could not replicate to " + dest, e);
				                        }
				                    //}
				                });

				            startedTasks.Add(replicationTask);
                            
				            activeTasks.Enqueue(replicationTask);
				            replicationTask.ContinueWith(
				                _ =>
				                {
				                    // here we purge all the completed tasks at the head of the queue
				                    Task task;
				                    while (activeTasks.TryPeek(out task))
				                    {
				                        if (!task.IsCompleted && !task.IsCanceled && !task.IsFaulted) break;
				                        activeTasks.TryDequeue(out task); // remove it from end
				                    }
				                });
				        }
				    }
				}
				catch (Exception e)
				{
				    log.ErrorException("Failed to perform replication", e);
				}
			}
		}

        public void Dispose()
        {
            Task task;
            cancellation.Cancel();
            SignalCounterUpdate();

            while (activeTasks.TryDequeue(out task))
            {
                task.Wait();
            }
        }
    }
}
﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Isam.Esent.Interop;
using Raven.Abstractions.Data;
using Raven.Abstractions.Logging;
using Raven.Abstractions.Util;
using Raven.Database.Impl.Synchronization;
using Raven.Database.Server;
using Raven.Database.Storage;
using System.Linq;
using Task = Raven.Database.Tasks.Task;
using Raven.Abstractions.Extensions;

namespace Raven.Database.Indexing
{
    public abstract class AbstractIndexingExecuter
    {
        const string DocumentReindexingWorkReason = "Some documents need reindexing. (One or more document touches)";

        protected WorkContext context;
        protected TaskScheduler scheduler;
        protected static readonly ILog Log = LogManager.GetCurrentClassLogger();
        protected ITransactionalStorage transactionalStorage;
        protected int workCounter;
        protected int lastFlushedWorkCounter;
        protected BaseBatchSizeAutoTuner autoTuner;

        protected AbstractIndexingExecuter(WorkContext context)
        {
            this.transactionalStorage = context.TransactionalStorage;
            this.context = context;
            this.scheduler = context.TaskScheduler;
        }

        public void Execute()
        {
            using (LogContext.WithDatabase(context.DatabaseName))
            {
                Init();
                var name = GetType().Name;
                var workComment = "WORK BY " + name;
                bool isIdle = false;
                while (context.RunIndexing)
                {
                    bool foundWork;
                    try
                    {
                        bool onlyFoundIdleWork;
                        foundWork = ExecuteIndexing(isIdle, out onlyFoundIdleWork);
                        if (foundWork && onlyFoundIdleWork == false)
                            isIdle = false;

                        while (context.RunIndexing) // we want to drain all of the pending tasks before the next run
                        {
                            if (ExecuteTasks() == false)
                                break;
                            foundWork = true;
                        }

                    }
                    catch (OutOfMemoryException oome)
                    {
                        foundWork = true;
                        HandleOutOfMemoryException(oome);
                    }
                    catch (AggregateException ae)
                    {
                        foundWork = true;
                        var actual = ae.ExtractSingleInnerException();
                        var oome = actual as OutOfMemoryException;
                        if (oome == null)
                        {
                            if (IsEsentOutOfMemory(actual))
                            {

                                autoTuner.OutOfMemoryExceptionHappened();
                            }
                            Log.ErrorException("Failed to execute indexing", ae);
                        }
                        else
                        {
                            HandleOutOfMemoryException(oome);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        Log.Info("Got rude cancellation of indexing as a result of shutdown, aborting current indexing run");
                        return;
                    }
                    catch (Exception e)
                    {
                        foundWork = true; // we want to keep on trying, anyway, not wait for the timeout or more work
                        Log.ErrorException("Failed to execute indexing", e);
                        if (IsEsentOutOfMemory(e))
                        {
                            autoTuner.OutOfMemoryExceptionHappened();
                        }
                    }
                    if (foundWork == false && context.RunIndexing)
                    {
                        isIdle = context.WaitForWork(context.Configuration.TimeToWaitBeforeRunningIdleIndexes, ref workCounter, () =>
                        {
                            try
                            {
                                FlushIndexes();
                            }
                            catch (Exception e)
                            {
                                Log.WarnException("Could not flush indexes properly", e);
                            }
                        }, name);
                    }
                    else // notify the tasks executer that it has work to do
                    {
                        context.ShouldNotifyAboutWork(() => workComment);
                        context.NotifyAboutWork();
                    }
                }
                Dispose();
            }
        }

        private bool IsEsentOutOfMemory(Exception actual)
        {
            var esentErrorException = actual as EsentErrorException;
            if (esentErrorException == null)
                return false;
            switch (esentErrorException.Error)
            {
                case JET_err.OutOfMemory:
                case JET_err.CurrencyStackOutOfMemory:
                case JET_err.SPAvailExtCacheOutOfMemory:
                case JET_err.VersionStoreOutOfMemory:
                case JET_err.VersionStoreOutOfMemoryAndCleanupTimedOut:
                    return true;
            }
            return false;
        }

        protected virtual void Dispose() { }

        protected virtual void Init() { }

        private void HandleOutOfMemoryException(Exception oome)
        {
            Log.WarnException(
                @"Failed to execute indexing because of an out of memory exception. Will force a full GC cycle and then become more conservative with regards to memory",
                oome);

            // On the face of it, this is stupid, because OOME will not be thrown if the GC could release
            // memory, but we are actually aware that during indexing, the GC couldn't find garbage to clean,
            // but in here, we are AFTER the index was done, so there is likely to be a lot of garbage.
            GC.Collect(GC.MaxGeneration);
            autoTuner.OutOfMemoryExceptionHappened();
        }

        private bool ExecuteTasks()
        {
            bool foundWork = false;
            transactionalStorage.Batch(actions =>
            {
                Task task = GetApplicableTask(actions);
                if (task == null)
                    return;

                context.UpdateFoundWork();

                Log.Debug("Executing {0}", task);
                foundWork = true;

                context.CancellationToken.ThrowIfCancellationRequested();

                try
                {
                    task.Execute(context);
                }
                catch (Exception e)
                {
                    Log.WarnException(
                        string.Format("Task {0} has failed and was deleted without completing any work", task),
                        e);
                }
            });
            return foundWork;
        }

        protected abstract Task GetApplicableTask(IStorageActionsAccessor actions);

        private void FlushIndexes()
        {
            if (lastFlushedWorkCounter == workCounter || context.DoWork == false)
                return;
            lastFlushedWorkCounter = workCounter;
            FlushAllIndexes();
        }

        protected abstract void FlushAllIndexes();

        protected abstract Etag GetSynchronizationEtag();

        protected abstract Etag CalculateSynchronizationEtag(Etag currentEtag, Etag lastProcessedEtag);

        protected bool ExecuteIndexing(bool isIdle, out bool onlyFoundIdleWork)
        {
            Etag synchronizationEtag = null;
            DocumentKeysAddedWhileIndexingInProgress = new ConcurrentQueue<string>();

            var indexesToWorkOn = new List<IndexToWorkOn>();
            var localFoundOnlyIdleWork = new Reference<bool> { Value = true };
            transactionalStorage.Batch(actions =>
            {
                foreach (var indexesStat in actions.Indexing.GetIndexesStats().Where(IsValidIndex))
                {
                    var failureRate = actions.Indexing.GetFailureRate(indexesStat.Name);
                    if (failureRate.IsInvalidIndex)
                    {
                        Log.Info("Skipped indexing documents for index: {0} because failure rate is too high: {1}",
                                       indexesStat.Name,
                                       failureRate.FailureRate);
                        continue;
                    }

                    synchronizationEtag = synchronizationEtag ?? GetSynchronizationEtag();

                    if (IsIndexStale(indexesStat, synchronizationEtag, actions, isIdle, localFoundOnlyIdleWork) == false)
                        continue;
                    var indexToWorkOn = GetIndexToWorkOn(indexesStat);
                    var index = context.IndexStorage.GetIndexInstance(indexesStat.Name);
                    if (index == null || // not there
                        index.CurrentMapIndexingTask != null) // busy doing indexing work already, not relevant for this batch
                        continue;

                    indexToWorkOn.Index = index;
                    indexesToWorkOn.Add(indexToWorkOn);
                }
            });
            onlyFoundIdleWork = localFoundOnlyIdleWork.Value;
            if (indexesToWorkOn.Count == 0)
                return false;

            context.UpdateFoundWork();
            context.CancellationToken.ThrowIfCancellationRequested();

            using (context.IndexDefinitionStorage.CurrentlyIndexing())
            {
                var lastIndexedGuidForAllIndexes = indexesToWorkOn.Min(x => new ComparableByteArray(x.LastIndexedEtag.ToByteArray())).ToEtag();
                var startEtag = CalculateSynchronizationEtag(synchronizationEtag, lastIndexedGuidForAllIndexes);

                ExecuteIndexingWork(indexesToWorkOn, startEtag);
            }

            ScheduleRelevantDocumentsForReindexIfNeeded();
            DocumentKeysAddedWhileIndexingInProgress = null;
            return true;
        }

        protected abstract IndexToWorkOn GetIndexToWorkOn(IndexStats indexesStat);

        protected abstract bool IsIndexStale(IndexStats indexesStat, Etag synchronizationEtag, IStorageActionsAccessor actions, bool isIdle, Reference<bool> onlyFoundIdleWork);

        protected abstract void ExecuteIndexingWork(IList<IndexToWorkOn> indexesToWorkOn, Etag startEtag);

        protected abstract bool IsValidIndex(IndexStats indexesStat);

        protected abstract ConcurrentQueue<string> DocumentKeysAddedWhileIndexingInProgress { get; set; }        

        protected abstract ConcurrentDictionary<string, ConcurrentBag<string>> ReferencingDocumentsByChildKeysWhichMightNeedReindexing { get; }

        private void ScheduleRelevantDocumentsForReindexIfNeeded()
        {
            try
            {
                var documentKeysAddedWhileIndexingInProgress = DocumentKeysAddedWhileIndexingInProgress;
                if (documentKeysAddedWhileIndexingInProgress != null && documentKeysAddedWhileIndexingInProgress.Count == 0)
                    return;

                transactionalStorage.Batch(actions =>
                {
                    bool touched = false;
                    string result;
                    while (documentKeysAddedWhileIndexingInProgress.TryDequeue(out result))
                    {
                        ConcurrentBag<string> bag;
                        if (ReferencingDocumentsByChildKeysWhichMightNeedReindexing.TryGetValue(result, out bag) == false)
                            continue;
                        foreach (var docKey in bag)
                        {
                            touched = true;
                            Etag etag;
                            Etag touchEtag;
                            actions.Documents.TouchDocument(docKey, out etag, out touchEtag);
                        }
                    }

                    if (touched)
                        context.ShouldNotifyAboutWork(() => DocumentReindexingWorkReason);
                });
            }
            finally
            {
                ReferencingDocumentsByChildKeysWhichMightNeedReindexing.Clear();
            }
        }
    }
}

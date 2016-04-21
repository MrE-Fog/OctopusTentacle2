using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Octopus.Shared.Diagnostics;
using Octopus.Shared.Util;

namespace Octopus.Shared.Tasks
{
    public class RunningTask : ITaskContext, IRunningTask
    {
        readonly string taskId;
        readonly string description;
        readonly Type rootTaskControllerType;
        readonly object arguments;
        readonly ILifetimeScope lifetimeScope;
        readonly TaskCompletionHandler completeCallback;
        readonly ManualResetEventSlim complete = new ManualResetEventSlim(false);
        readonly CancellationTokenSource cancel = new CancellationTokenSource();
        readonly Thread workThread;
        readonly ILog log = Log.Octopus();
        readonly LogContext taskLogContext;
        bool isPaused;

        public RunningTask(string taskId, string logCorrelationId, string description, Type rootTaskControllerType, object arguments, ILifetimeScope lifetimeScope, TaskCompletionHandler completeCallback)
        {
            this.taskId = taskId;
            this.description = description;
            this.rootTaskControllerType = rootTaskControllerType;
            this.arguments = arguments;
            this.lifetimeScope = lifetimeScope;
            this.completeCallback = completeCallback;

            taskLogContext = LogContext.CreateNew(logCorrelationId);
            workThread = new Thread(RunMainThread) {Name = taskId + ": " + description};
        }

        public string Id
        {
            get { return taskId; }
        }

        public string TaskId
        {
            get { return taskId; }
        }

        public bool IsCancellationRequested
        {
            get { return cancel.IsCancellationRequested; }
        }

        public CancellationToken CancellationToken
        {
            get { return cancel.Token; }
        }

        public void Start()
        {
            workThread.Start();
        }

        void RunMainThread()
        {
            using (log.WithinBlock(taskLogContext))
            {
                log.Info(description);

                using (var workScope = lifetimeScope.BeginLifetimeScope())
                {
                    Exception ex = null;
                    try
                    {
                        var builder = new ContainerBuilder();
                        builder.RegisterInstance<ITaskContext>(this);
                        builder.RegisterInstance(arguments).AsSelf().AsImplementedInterfaces();
                        builder.Update(workScope.ComponentRegistry);

                        var controller = (ITaskController)workScope.Resolve(rootTaskControllerType);

                        controller.Execute();
                    }
                    catch (Exception e)
                    {
                        ex = e;
                        var root = e.UnpackFromContainers();

                        if (root is OperationCanceledException || root is ThreadAbortException)
                        {
                            // These happen as part of cancellation. It's enough to just return them, without logging them.
                        }
                        else
                        {
                            log.Fatal(root.Message);
                        }
                    }
                    finally
                    {
                        CompleteTask(ex);
                    }
                }
                if (!IsPaused() || IsCancellationRequested)
                {
                    log.Finish();
                }
            }
        }

        public void Cancel()
        {
            using (log.WithinBlock(taskLogContext))
            {
                if (IsCancellationRequested)
                {
                    return;
                }

                log.Info("Requesting cancellation...");
                cancel.Cancel();
            }
        }

        public void Pause()
        {
            isPaused = true;
        }

        public bool IsPaused()
        {
            return isPaused;
        }

        public void EnsureNotCanceled()
        {
            if (IsCancellationRequested)
            {
                throw new TaskCanceledException("This task has been canceled.");
            }
        }

        void CompleteTask(Exception error)
        {
            try
            {
                complete.Set();

                if (completeCallback != null)
                {
                    completeCallback(taskId, error);
                }
            }
            catch (Exception completeEx)
            {
                log.Error(completeEx, "Unable to mark task " + Id + " as complete: " + completeEx.Message);
            }
        }
    }
}
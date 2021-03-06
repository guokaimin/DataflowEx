﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Gridsum.DataflowEx.Exceptions;
using Gridsum.DataflowEx.PatternMatch;

namespace Gridsum.DataflowEx
{
    /// <summary>
    /// Core concept of DataflowEx. Represents a reusable dataflow component with its processing logic, which
    /// may contain one or multiple blocks. Inheritors should call RegisterBlock in their constructors.
    /// </summary>
    public abstract class BlockContainer : IBlockContainer
    {
        private static ConcurrentDictionary<string, IntHolder> s_nameDict = new ConcurrentDictionary<string, IntHolder>();
        protected readonly BlockContainerOptions m_containerOptions;
        protected readonly DataflowLinkOptions m_defaultLinkOption;
        protected Lazy<Task> m_completionTask;
        protected ImmutableList<IChildMeta> m_children = ImmutableList.Create<IChildMeta>();
        protected string m_defaultName;

        public BlockContainer(BlockContainerOptions containerOptions)
        {
            m_containerOptions = containerOptions;
            m_defaultLinkOption = new DataflowLinkOptions() { PropagateCompletion = true };
            m_completionTask = new Lazy<Task>(GetCompletionTask, LazyThreadSafetyMode.ExecutionAndPublication);

            string friendlyName = Utils.GetFriendlyName(this.GetType());
            int count = s_nameDict.GetOrAdd(friendlyName, new IntHolder()).Increment();
            m_defaultName = friendlyName + count;
            
            if (m_containerOptions.ContainerMonitorEnabled || m_containerOptions.BlockMonitorEnabled)
            {
                StartPerformanceMonitorAsync();
            }
        }

        /// <summary>
        /// Display name of the container
        /// </summary>
        public virtual string Name
        {
            get { return m_defaultName; }
        }
        
        /// <summary>
        /// Register this block to block meta. Also make sure the container will fail if the registered block fails.
        /// </summary>
        protected void RegisterChild(IDataflowBlock block, Action<Task> blockCompletionCallback = null)
        {
            if (block == null)
            {
                throw new ArgumentNullException("block");
            }

            if (m_children.OfType<BlockMeta>().Any(bm => bm.Block.Equals(block)))
            {
                throw new ArgumentException("Duplicate block registered in " + this.Name);
            }

            m_children = m_children.Add(new BlockMeta(block, this, blockCompletionCallback));
        }

        protected void RegisterChild(BlockContainer childContainer, Action<Task> containerCompletionCallback = null)
        {
            if (childContainer == null)
            {
                throw new ArgumentNullException("childContainer");
            }

            if (m_children.OfType<BlockContainerMeta>().Any(cm => cm.Container.Equals(childContainer)))
            {
                throw new ArgumentException("Duplicate block container registered in " + this.Name);
            }

            m_children = m_children.Add(new BlockContainerMeta(childContainer, this, containerCompletionCallback));
        }
        
        //todo: add completion condition and cancellation token support
        private async Task StartPerformanceMonitorAsync()
        {
            while (true)
            {
                await Task.Delay(m_containerOptions.MonitorInterval ?? TimeSpan.FromSeconds(10));

                if (m_containerOptions.ContainerMonitorEnabled)
                {
                    int count = this.BufferedCount;

                    if (count != 0 || m_containerOptions.PerformanceMonitorMode == BlockContainerOptions.PerformanceLogMode.Verbose)
                    {
                        LogHelper.Logger.Debug(h => h("[{0}] has {1} todo items at this moment.", this.Name, count));
                    }
                }

                if (m_containerOptions.BlockMonitorEnabled)
                {
                    foreach(var child in m_children)
                    {
                        var count = child.BufferCount;

                        if (count != 0 || m_containerOptions.PerformanceMonitorMode == BlockContainerOptions.PerformanceLogMode.Verbose)
                        {
                            IChildMeta c = child;
                            LogHelper.Logger.Debug(h => h("{0} has {1} todo items at this moment.", c.DisplayName, count));
                        }
                    }
                }
            }
        }

        protected virtual async Task GetCompletionTask()
        {
            if (m_children.Count == 0)
            {
                throw new NoChildRegisteredException(this);
            }

            ImmutableList<IChildMeta> childrenSnapShot;

            do
            {
                childrenSnapShot = m_children;
                await TaskEx.AwaitableWhenAll(childrenSnapShot.Select(b => b.ChildCompletion).ToArray());
            } while (!object.ReferenceEquals(m_children, childrenSnapShot));

            this.CleanUp();
        }

        protected virtual void CleanUp()
        {
            //
        }

        /// <summary>
        /// Represents the completion of the whole container
        /// </summary>
        public Task CompletionTask
        {
            get
            {
                return m_completionTask.Value;
            }
        }

        public virtual IEnumerable<IDataflowBlock> Blocks { get { return m_children.SelectMany(bm => bm.Blocks); } }

        public virtual void Fault(Exception exception)
        {
            LogHelper.Logger.ErrorFormat("<{0}> Exception occur. Shutting down my working blocks...", exception, this.Name);

            foreach (var child in m_children)
            {
                if (!child.ChildCompletion.IsCompleted)
                {
                    string msg = string.Format("{0} is shutting down", child.DisplayName);
                    LogHelper.Logger.Error(msg);

                    //just pass on PropagatedException (do not use original exception here)
                    if (exception is PropagatedException)
                    {
                        child.Fault(exception);
                    }
                    else if (exception is TaskCanceledException)
                    {
                        child.Fault(new SiblingUnitCanceledException());
                    }
                    else
                    {
                        child.Fault(new SiblingUnitFailedException());
                    }
                }
            }
        }

        /// <summary>
        /// Sum of the buffer size of all blocks in the container
        /// </summary>
        public virtual int BufferedCount
        {
            get
            {
                return m_children.Sum(bm => bm.BufferCount);
            }
        }
    }

    public abstract class BlockContainer<TIn> : BlockContainer, IBlockContainer<TIn>
    {
        protected BlockContainer(BlockContainerOptions containerOptions) : base(containerOptions)
        {
        }

        public abstract ITargetBlock<TIn> InputBlock { get; }
        
        /// <summary>
        /// Helper method to read from a text reader and post everything in the text reader to the pipeline
        /// </summary>
        public void PullFrom(IEnumerable<TIn> reader)
        {
            long count = 0;
            foreach(var item in reader)
            {
                InputBlock.SafePost(item);
                count++;
            }

            LogHelper.Logger.Info(h => h("<{0}> Pulled and posted {1} {2}s to the input block {3}.", 
                this.Name, 
                count, 
                Utils.GetFriendlyName(typeof(TIn)), 
                Utils.GetFriendlyName(this.InputBlock.GetType())
                ));
        }

        public void LinkFrom(ISourceBlock<TIn> block)
        {
            block.LinkTo(this.InputBlock, m_defaultLinkOption);
        }
    }

    public abstract class BlockContainer<TIn, TOut> : BlockContainer<TIn>, IBlockContainer<TIn, TOut>
    {
        protected ImmutableList<Predicate<TOut>>.Builder m_condBuilder = ImmutableList<Predicate<TOut>>.Empty.ToBuilder();
        protected Lazy<ImmutableList<Predicate<TOut>>> m_frozenConditions;
        protected StatisticsRecorder GarbageRecorder { get; private set; }

        protected BlockContainer(BlockContainerOptions containerOptions) : base(containerOptions)
        {
            this.GarbageRecorder = new StatisticsRecorder();
            m_condBuilder = ImmutableList<Predicate<TOut>>.Empty.ToBuilder();
            m_frozenConditions = new Lazy<ImmutableList<Predicate<TOut>>>(() =>
            {
                return m_condBuilder.ToImmutable();
            });
        }

        public abstract ISourceBlock<TOut> OutputBlock { get; }
        
        protected void LinkBlockToContainer<T>(ISourceBlock<T> block, IBlockContainer<T> otherBlockContainer)
        {
            block.LinkTo(otherBlockContainer.InputBlock, new DataflowLinkOptions { PropagateCompletion = false });

            //manullay handle inter-container problem
            //we use WhenAll here to make sure this container fails before propogating to other container
            Task.WhenAll(block.Completion, this.CompletionTask).ContinueWith(whenAllTask => 
                {
                    if (!otherBlockContainer.CompletionTask.IsCompleted)
                    {
                        if (whenAllTask.IsFaulted)
                        {
                            otherBlockContainer.Fault(new LinkedContainerFailedException());
                        }
                        else if (whenAllTask.IsCanceled)
                        {
                            otherBlockContainer.Fault(new LinkedContainerCanceledException());
                        }
                        else
                        {
                            otherBlockContainer.InputBlock.Complete();
                        }
                    }
                });

            //Make sure other container also fails me
            otherBlockContainer.CompletionTask.ContinueWith(otherTask =>
                {
                    if (this.CompletionTask.IsCompleted)
                    {
                        return;
                    }

                    if (otherTask.IsFaulted)
                    {
                        LogHelper.Logger.InfoFormat("<{0}>Downstream block container faulted before I am done. Fault myself.", this.Name);
                        this.Fault(new LinkedContainerFailedException());
                    }
                    else if (otherTask.IsCanceled)
                    {
                        LogHelper.Logger.InfoFormat("<{0}>Downstream block container canceled before I am done. Cancel myself.", this.Name);
                        this.Fault(new LinkedContainerCanceledException());
                    }
                });
        }

        public void LinkTo(IBlockContainer<TOut> other)
        {
            LinkBlockToContainer(this.OutputBlock, other);
        }

        public void TransformAndLink<TTarget>(IBlockContainer<TTarget> other, Func<TOut, TTarget> transform, IMatchCondition<TOut> condition)
        {
            this.TransformAndLink(other, transform, new Predicate<TOut>(condition.Matches));
        }

        public void TransformAndLink<TTarget>(IBlockContainer<TTarget> other, Func<TOut, TTarget> transform, Predicate<TOut> predicate)
        {
            if (m_frozenConditions.IsValueCreated)
            {
                throw new InvalidOperationException("You cannot call TransformAndLink after LinkLeftToNull has been called");
            }

            m_condBuilder.Add(predicate);
            var converter = new TransformBlock<TOut, TTarget>(transform);
            this.OutputBlock.LinkTo(converter, m_defaultLinkOption, predicate);
            
            LinkBlockToContainer(converter, other);            
        }

        public void TransformAndLink<TTarget>(IBlockContainer<TTarget> other, Func<TOut, TTarget> transform)
        {
            this.TransformAndLink(other, transform, @out => true);
        }

        public void TransformAndLink<TTarget>(IBlockContainer<TTarget> other) where TTarget : TOut
        {
            this.TransformAndLink(other, @out => { return ((TTarget)@out); }, @out => @out is TTarget);
        }

        public void TransformAndLink<TTarget, TOutSubType>(IBlockContainer<TTarget> other, Func<TOutSubType, TTarget> transform) where TOutSubType : TOut
        {
            this.TransformAndLink(other, @out => { return transform(((TOutSubType)@out)); }, @out => @out is TOutSubType);
        }

        public void LinkLeftToNull()
        {
            var frozenConds = m_frozenConditions.Value;
            var left = new Predicate<TOut>(@out =>
                {
                    if (frozenConds.All(condition => !condition(@out)))
                    {
                        OnOutputToNull(@out);
                        return true;
                    }
                    else
                    {
                        return false;
                    }
                }
                );

            this.OutputBlock.LinkTo(DataflowBlock.NullTarget<TOut>(), m_defaultLinkOption, left);
        }

        protected virtual void OnOutputToNull(TOut output)
        {
            this.GarbageRecorder.RecordType(output.GetType());
        }
    }
}